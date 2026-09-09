using System.Net;
using System.Net.Http.Json;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Looper.Api.Tests;

// ============================================================================
// Events as resources: an Event Raiser fires deterministically when the run ends
// the way it says; an Event Listener wakes its agent through the dispatcher in
// any trigger mode. Neither depends on the model doing anything.
// ============================================================================

public class EventModuleTests
{
    private static ResourceModuleContext Ctx(string json) => new(json, null, null, Guid.NewGuid(), "Newsletter sent", "");

    [Fact]
    public void A_raiser_tells_the_agent_the_harness_will_raise_it()
    {
        var section = Assert.Single(new EventRaiserModule().Contribute(Ctx("""{"topic":"newsletter.sent","when":"succeeded"}""")).PromptSections);
        Assert.Contains("EVENT 'newsletter.sent'", section);
        Assert.Contains("succeeds and every gate passes", section);
        Assert.Contains("do not need to raise it yourself", section);

        Assert.Contains("fails (or a gate fails)", new EventRaiserModule().Contribute(Ctx("""{"topic":"x.y","when":"failed"}""")).PromptSections[0]);
        Assert.Empty(new EventRaiserModule().Contribute(Ctx("{}")).PromptSections);
    }

    [Fact]
    public void Topics_are_validated_when_the_resource_is_saved()
    {
        var raiser = new EventRaiserModule();
        var ex = Assert.Throws<ArgumentException>(() => raiser.PrepareRun(Ctx("""{"topic":"Bad Topic"}""")));
        Assert.Contains("not a valid event topic", ex.Message);
        raiser.PrepareRun(Ctx("""{"topic":"newsletter.sent"}"""));   // fine
        raiser.PrepareRun(Ctx("{}"));                                 // empty is the form's problem, not ours
    }

    [Theory]
    [InlineData("succeeded", true, null, true)]
    [InlineData("succeeded", true, true, true)]
    [InlineData("succeeded", true, false, false)]   // a failed gate is not success
    [InlineData("succeeded", false, null, false)]
    [InlineData("failed", true, false, true)]
    [InlineData("failed", false, null, true)]
    [InlineData("failed", true, true, false)]
    [InlineData("always", false, null, true)]
    [InlineData("always", true, true, true)]
    public void Raisers_fire_on_exactly_the_outcome_they_name(string when, bool runOk, bool? gates, bool expected)
    {
        var config = new EventRaiserConfig("x.y", when, "");
        Assert.Equal(expected, EventResources.Fires(config, runOk, gates));
    }

    [Fact]
    public void Payload_templates_render_placeholders_and_default_to_a_status_line()
    {
        var runId = Guid.NewGuid();
        var config = new EventRaiserConfig("x.y", "always", "{agent} finished ({status}) in run {run}: {result}");
        Assert.Equal($"Campaign loop finished (succeeded) in run {runId}: 12 sent",
            EventResources.RenderPayload(config, "Campaign loop", runId, true, null, " 12 sent "));

        Assert.Equal("Campaign loop failed. boom",
            EventResources.RenderPayload(config with { Payload = "" }, "Campaign loop", runId, false, null, "boom"));

        var longResult = new string('x', 2000);
        Assert.EndsWith("…", EventResources.RenderPayload(config with { Payload = "{result}" }, "a", runId, true, true, longResult));
    }

    [Fact]
    public void Effective_patterns_are_the_agents_own_topics_and_only_in_event_mode()
    {
        Assert.Equal(["prd.approved", "newsletter.*"],
            EventDispatcher.EffectivePatterns(TriggerMode.Event, "prd.approved\nbad pattern\nnewsletter.*\nprd.approved"));
        Assert.Empty(EventDispatcher.EffectivePatterns(TriggerMode.Scheduled, "prd.approved"));
    }
}

public sealed class EventTriggerDispatchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly EventDispatcher _dispatcher = new(NullLogger<EventDispatcher>.Instance);

    public EventTriggerDispatchTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task An_event_mode_agent_is_woken_by_its_topic_and_a_scheduled_one_never_is()
    {
        await using var db = new LooperDbContext(_options);
        var wired = new LoopAgent { Name = "Wired", Prompt = "p", Enabled = true, TriggerMode = TriggerMode.Event, TriggerTopics = "newsletter.sent", DryRun = true };
        var prefix = new LoopAgent { Name = "Prefix", Prompt = "p", Enabled = true, TriggerMode = TriggerMode.Event, TriggerTopics = "newsletter.*", DryRun = true };
        var other = new LoopAgent { Name = "Other", Prompt = "p", Enabled = true, TriggerMode = TriggerMode.Event, TriggerTopics = "docs.updated", DryRun = true };
        var off = new LoopAgent { Name = "Off", Prompt = "p", Enabled = false, TriggerMode = TriggerMode.Event, TriggerTopics = "newsletter.sent", DryRun = true };
        var scheduled = new LoopAgent { Name = "Scheduled", Prompt = "p", Enabled = true, TriggerMode = TriggerMode.Scheduled, TriggerTopics = "newsletter.sent", DryRun = true };
        db.Agents.AddRange(wired, prefix, other, off, scheduled);
        await db.SaveChangesAsync();

        await _dispatcher.RaiseAsync(db, "newsletter.sent", "42 sent", EventSource.Harness, null, null, 0, default);

        var delivered = await db.EventDeliveries.Select(d => d.AgentId).ToListAsync();
        Assert.Equal(2, delivered.Count);
        Assert.Contains(wired.Id, delivered);
        Assert.Contains(prefix.Id, delivered);
        Assert.DoesNotContain(scheduled.Id, delivered);   // a schedule is not a subscription
    }
}

public class EventResourceApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record IdResponse(Guid Id);
    private sealed record TopicResponse(string Topic, string Kind, string Source, bool IsPattern);
    private sealed record EventResponse(Guid Id, string Topic, int Listeners, string Source);
    private sealed record RunResponse(Guid Id, string Status, string Trigger);

    private async Task<Guid> CreateResource(string name, string typeKey, string configJson, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var response = await _client.PostAsJsonAsync("/api/resources", new
        {
            name, type = "Custom", customTypeKey = typeKey, description = "", configJson
        }, TestJson.Options);
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created) return Guid.Empty;
        return (await response.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
    }

    private Task<HttpResponseMessage> PostAgent(string name, string triggerMode, string? topics, params Guid[] resourceIds) =>
        _client.PostAsJsonAsync("/api/agents", new
        {
            name, description = "", prompt = "Loop.", model = "claude-haiku-4-5", effort = "Low", intervalMinutes = 60,
            triggerMode, triggerTopics = topics, maxTurns = 5, maxBudgetUsd = (decimal?)null,
            workingDirectory = (string?)null, allowedTools = (string?)null, bypassPermissions = true,
            dryRun = true, autonomyLevel = 2, resourceIds
        }, TestJson.Options);

    [Fact]
    public async Task Event_resources_are_built_in_types_and_bad_topics_are_rejected_on_save()
    {
        var types = await _client.GetFromJsonAsync<List<Dictionary<string, System.Text.Json.JsonElement>>>("/api/resource-types", TestJson.Options);
        Assert.Contains(types!, t => t["typeKey"].GetString() == "EventRaiser");
        Assert.DoesNotContain(types!, t => t["typeKey"].GetString() == "EventListener");   // retired: the agent's trigger is the listener

        var bad = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "Bad raiser", type = "Custom", customTypeKey = "EventRaiser", description = "", configJson = """{"topic":"Not A Topic","when":"succeeded"}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not a valid event topic", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_topic_catalog_lists_completions_raisers_listeners_and_seen_topics()
    {
        var raiser = await CreateResource("Newsletter sent", "EventRaiser", """{"topic":"newsletter.sent","when":"succeeded"}""");
        var agent = await PostAgent("Catalog agent", "Scheduled", null, raiser);
        var agentId = (await agent.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
        var listenerAgent = await PostAgent("On newsletter", "Event", "newsletter.*");
        var listenerAgentId = (await listenerAgent.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
        try
        {
            await _client.PostAsJsonAsync("/api/events", new { topic = "adhoc.ping", payload = "" }, TestJson.Options);

            var catalog = await _client.GetFromJsonAsync<List<TopicResponse>>("/api/events/topics", TestJson.Options);
            var completion = Assert.Single(catalog!, t => t.Topic == "agent.catalog-agent.succeeded");
            Assert.Equal("completion", completion.Kind);
            Assert.Contains("Catalog agent succeeds", completion.Source);

            var raised = Assert.Single(catalog!, t => t.Topic == "newsletter.sent");
            Assert.Equal("raiser", raised.Kind);
            Assert.Contains("Catalog agent", raised.Source);

            var listens = Assert.Single(catalog!, t => t.Topic == "newsletter.*");
            Assert.Equal("listener", listens.Kind);
            Assert.Contains("On newsletter listens", listens.Source);
            Assert.True(listens.IsPattern);

            Assert.Equal("seen", Assert.Single(catalog!, t => t.Topic == "adhoc.ping").Kind);
            Assert.Equal(catalog!.Count, catalog.Select(t => t.Topic).Distinct().Count());
        }
        finally
        {
            await _client.DeleteAsync($"/api/agents/{agentId}");
            await _client.DeleteAsync($"/api/agents/{listenerAgentId}");
            await _client.DeleteAsync($"/api/resources/{raiser}");
        }
    }

    [Fact]
    public async Task An_event_mode_agent_needs_at_least_one_topic()
    {
        var without = await PostAgent("No ears", "Event", "  ");
        Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.Contains("something to listen for", await without.Content.ReadAsStringAsync());

        var created = await PostAgent("Has ears", "Event", "prd.approved");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
        await _client.DeleteAsync($"/api/agents/{id}");
    }

    [Fact]
    public async Task An_event_mode_agent_starts_when_its_topic_is_raised()
    {
        var created = await PostAgent("Follow-up loop", "Event", "newsletter.sent");
        var agentId = (await created.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
        try
        {
            (await _client.PostAsJsonAsync($"/api/agents/{agentId}/enabled", new { enabled = true }, TestJson.Options)).EnsureSuccessStatusCode();

            var raise = await _client.PostAsJsonAsync("/api/events", new { topic = "newsletter.sent", payload = "42 sent" }, TestJson.Options);
            Assert.Equal(HttpStatusCode.Created, raise.StatusCode);
            Assert.Equal(1, (await raise.Content.ReadFromJsonAsync<EventResponse>(TestJson.Options))!.Listeners);

            RunResponse? run = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (run is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(200);
                var runs = await _client.GetFromJsonAsync<List<RunResponse>>($"/api/agents/{agentId}/runs", TestJson.Options);
                run = runs?.FirstOrDefault();
            }
            Assert.NotNull(run);
            Assert.Equal("Event", run!.Trigger);
        }
        finally
        {
            await _client.PostAsync($"/api/agents/{agentId}/cancel", null);
            await _client.DeleteAsync($"/api/agents/{agentId}");
        }
    }
}

/// <summary>The harness raises Event Raiser topics after a real run — through the real coordinator with a fake CLI.</summary>
public sealed class EventRaiserHarnessTests : IDisposable
{
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly string _dir;
    private readonly string _fakeClaude;

    public EventRaiserHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"looper-event-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "harness.db")}").Options;
        using (var db = new LooperDbContext(_options)) db.Database.EnsureCreated();
        _fakeClaude = Path.Combine(_dir, "claude");
        File.WriteAllText(_fakeClaude,
            "#!/bin/bash\necho '{\"result\":\"Sent 12 newsletters.\",\"total_cost_usd\":0.01,\"is_error\":false,\"num_turns\":1,\"duration_ms\":5,\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}'\n");
        File.SetUnixFileMode(_fakeClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Factory(DbContextOptions<LooperDbContext> options) : IDbContextFactory<LooperDbContext>
    {
        public LooperDbContext CreateDbContext() => new(options);
    }

    private static Resource Raiser(string name, string topic, string when, string payload = "") => new()
    {
        Name = name, Type = ResourceType.Custom, CustomTypeKey = "EventRaiser",
        ConfigJson = System.Text.Json.JsonSerializer.Serialize(new { topic, when, payload })
    };

    [Fact]
    public async Task Raisers_matching_the_outcome_fire_with_rendered_payloads_and_the_others_stay_silent()
    {
        Guid agentId;
        await using (var db = new LooperDbContext(_options))
        {
            var agent = new LoopAgent
            {
                Name = "Campaign loop", Prompt = "Send.", Model = "claude-opus-5", DryRun = false, MaxTurns = 5, WorkingDirectory = _dir,
                Resources =
                {
                    Raiser("Newsletter sent", "newsletter.sent", "succeeded", "{agent} finished: {result}"),
                    Raiser("Newsletter failed", "newsletter.failed", "failed"),
                    Raiser("Newsletter done", "newsletter.done", "always")
                }
            };
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
        }

        var looperOptions = Options.Create(new LooperOptions { MaxConcurrentRuns = 2, RunTimeoutMinutes = 5, ClaudeCommand = _fakeClaude });
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new EventRaiserModule());
        var coordinator = new AgentRunCoordinator(
            new Factory(_options),
            new ClaudeCliExecutor(looperOptions, new ClaudeAuthProvider(new Factory(_options)), registry, new GraphContextService(looperOptions, NullLogger<GraphContextService>.Instance), NullLogger<ClaudeCliExecutor>.Instance),
            new SimulatedAgentExecutor(),
            new TestingActionRunner(looperOptions),
            new ScriptRunner(looperOptions),
            new MetricRecorder(new Factory(_options), NullLogger<MetricRecorder>.Instance),
            new ReviewRunner(looperOptions, new ClaudeAuthProvider(new Factory(_options)), NullLogger<ReviewRunner>.Instance),
            new EventDispatcher(NullLogger<EventDispatcher>.Instance),
            looperOptions,
            NullLogger<AgentRunCoordinator>.Instance);

        var runId = (await coordinator.TriggerRunAsync(agentId, RunTrigger.Manual))!.Value;
        List<LooperEvent> events = [];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            await using var db = new LooperDbContext(_options);
            events = await db.Events.AsNoTracking().Where(e => e.SourceRunId == runId).ToListAsync();
            if (events.Count >= 3) break;
        }

        Assert.Equal(3, events.Count);
        Assert.Contains(events, e => e.Topic == "agent.campaign-loop.succeeded");
        var sent = Assert.Single(events, e => e.Topic == "newsletter.sent");
        Assert.Equal("Campaign loop finished: Sent 12 newsletters.", sent.Payload);
        Assert.Equal(EventSource.Harness, sent.Source);
        Assert.Equal(agentId, sent.SourceAgentId);
        Assert.Contains(events, e => e.Topic == "newsletter.done");
        Assert.DoesNotContain(events, e => e.Topic == "newsletter.failed");
    }
}
