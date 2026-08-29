using System.Net;
using System.Net.Http.Json;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

public class EventTopicTests
{
    [Theory]
    [InlineData("prd.approved", true)]
    [InlineData("docs.updated", true)]
    [InlineData("agent.docs-gardener.succeeded", true)]
    [InlineData("single", true)]
    [InlineData("Bad.Case", false)]
    [InlineData("spaces here", false)]
    [InlineData("trailing.", false)]
    [InlineData("wild.*", false)] // no wildcards when raising
    [InlineData("", false)]
    public void Topic_validation_is_strict(string topic, bool valid) =>
        Assert.Equal(valid, EventDispatcher.IsValidTopic(topic));

    [Theory]
    [InlineData("prd.approved", true)]
    [InlineData("agent.docs-gardener.*", true)]
    [InlineData("agent.*.succeeded", false)] // wildcard only at the end
    [InlineData("*", false)]
    public void Pattern_validation_allows_only_trailing_wildcards(string pattern, bool valid) =>
        Assert.Equal(valid, EventDispatcher.IsValidPattern(pattern));

    [Theory]
    [InlineData("prd.approved", "prd.approved", true)]
    [InlineData("prd.approved", "prd.approved.eu", false)]     // exact means exact
    [InlineData("agent.docs.*", "agent.docs.succeeded", true)]
    [InlineData("agent.docs.*", "agent.docs-two.succeeded", false)] // prefix includes the dot
    [InlineData("agent.docs.*", "agent.docs", false)]
    public void Matching_is_deterministic_exact_or_prefix(string pattern, string topic, bool matches) =>
        Assert.Equal(matches, EventDispatcher.Matches(pattern, topic));

    [Fact]
    public void Patterns_parse_from_newlines_and_commas_with_dedupe()
    {
        var patterns = EventDispatcher.ParsePatterns("a.b\n c.d , a.b\n\n");
        Assert.Equal(["a.b", "c.d"], patterns);
    }

    [Fact]
    public void Completion_topics_slug_the_agent_name()
    {
        Assert.Equal("agent.docs-gardener.succeeded", EventDispatcher.CompletionTopic("Docs Gardener!", true));
        Assert.Equal("agent.docs-gardener.failed", EventDispatcher.CompletionTopic("Docs Gardener!", false));
    }
}

public sealed class EventDispatcherTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly EventDispatcher _dispatcher = new(NullLogger<EventDispatcher>.Instance);

    public EventDispatcherTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private LoopAgent SeedListener(LooperDbContext db, string topics, bool enabled = true,
        TriggerMode mode = TriggerMode.Event)
    {
        var agent = new LoopAgent
        {
            Name = $"L-{Guid.NewGuid():N}"[..10], Prompt = "p", Enabled = enabled,
            TriggerMode = mode, TriggerTopics = topics, DryRun = true,
        };
        db.Agents.Add(agent);
        db.SaveChanges();
        return agent;
    }

    [Fact]
    public async Task Matching_listeners_get_deliveries_and_others_do_not()
    {
        await using var db = new LooperDbContext(_options);
        var hit = SeedListener(db, "prd.approved");
        var wildcard = SeedListener(db, "prd.*");
        var miss = SeedListener(db, "docs.updated");
        var disabled = SeedListener(db, "prd.approved", enabled: false);
        var scheduled = SeedListener(db, "prd.approved", mode: TriggerMode.Scheduled); // mutual exclusion

        await _dispatcher.RaiseAsync(db, "prd.approved", "payload", EventSource.User, null, null, 0, default);

        var deliveries = await db.EventDeliveries.Select(d => d.AgentId).ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(hit.Id, deliveries);
        Assert.Contains(wildcard.Id, deliveries);
        Assert.DoesNotContain(miss.Id, deliveries);
        Assert.DoesNotContain(disabled.Id, deliveries);
        Assert.DoesNotContain(scheduled.Id, deliveries);
    }

    [Fact]
    public async Task An_agent_never_triggers_itself()
    {
        await using var db = new LooperDbContext(_options);
        var self = SeedListener(db, "docs.updated");

        await _dispatcher.RaiseAsync(db, "docs.updated", "", EventSource.Agent, self.Id, null, 0, default);

        Assert.Equal(0, await db.EventDeliveries.CountAsync());
    }

    [Fact]
    public async Task The_chain_depth_cap_stops_matching()
    {
        await using var db = new LooperDbContext(_options);
        SeedListener(db, "deep.topic");

        await _dispatcher.RaiseAsync(db, "deep.topic", "", EventSource.Harness, null, null,
            EventDispatcher.MaxChainDepth, default);

        Assert.Equal(1, await db.Events.CountAsync());          // the event is still recorded
        Assert.Equal(0, await db.EventDeliveries.CountAsync()); // but nothing listens past the brake
    }
}

public class EventSystemApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record AgentResponse(Guid Id, string Name, bool Enabled, DateTime? NextRunAtUtc);
    private sealed record RunResponse(Guid Id, string Status, string Trigger);
    private sealed record EventResponse(Guid Id, string Topic, string Source, int ChainDepth, int Listeners);

    private async Task<AgentResponse> CreateAgent(string name, string triggerMode, string? topics)
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name,
            description = "",
            prompt = "React to the event.",
            model = "claude-haiku-4-5",
            effort = "Low",
            intervalMinutes = 60,
            triggerMode,
            triggerTopics = topics,
            maxTurns = 5,
            maxBudgetUsd = (decimal?)null,
            workingDirectory = (string?)null,
            allowedTools = (string?)null,
            bypassPermissions = true,
            dryRun = true,
            autonomyLevel = 3,
            resourceIds = Array.Empty<Guid>()
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options))!;
    }

    [Fact]
    public async Task Event_mode_requires_valid_topics()
    {
        var missing = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "Bad listener", description = "", prompt = "p", model = "claude-haiku-4-5",
            effort = "Low", intervalMinutes = 60, triggerMode = "Event", triggerTopics = "  ",
            maxTurns = 5, maxBudgetUsd = (decimal?)null, workingDirectory = (string?)null,
            allowedTools = (string?)null, bypassPermissions = true, dryRun = true,
            autonomyLevel = 3, resourceIds = Array.Empty<Guid>()
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Raising_an_event_starts_the_listener_and_its_completion_chains_a_harness_event()
    {
        var listener = await CreateAgent("Event listener", "Event", "prd.approved");
        try
        {
            // Enabling an event agent must NOT schedule it.
            var enable = await _client.PostAsJsonAsync($"/api/agents/{listener.Id}/enabled",
                new { enabled = true }, TestJson.Options);
            enable.EnsureSuccessStatusCode();
            var enabled = await enable.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options);
            Assert.Null(enabled!.NextRunAtUtc);

            // Raise: one listener matched, pumped immediately into a run.
            var raise = await _client.PostAsJsonAsync("/api/events",
                new { topic = "prd.approved", payload = "PRD v2 signed off." }, TestJson.Options);
            Assert.Equal(HttpStatusCode.Created, raise.StatusCode);
            var evt = await raise.Content.ReadFromJsonAsync<EventResponse>(TestJson.Options);
            Assert.Equal(1, evt!.Listeners);
            Assert.Equal("User", evt.Source);

            // The listener's run exists with the Event trigger and completes (dry).
            RunResponse? run = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var runs = await _client.GetFromJsonAsync<List<RunResponse>>(
                    $"/api/agents/{listener.Id}/runs", TestJson.Options);
                run = runs!.FirstOrDefault();
                if (run is not null && run.Status != "Running") break;
                await Task.Delay(300);
            }
            Assert.NotNull(run);
            Assert.Equal("Event", run!.Trigger);

            // The harness announced the completion at chain depth 1.
            var events = await _client.GetFromJsonAsync<List<EventResponse>>("/api/events?take=50", TestJson.Options);
            var completion = events!.FirstOrDefault(e => e.Topic == "agent.event-listener.succeeded"
                || e.Topic == "agent.event-listener.failed");
            Assert.NotNull(completion);
            Assert.Equal("Harness", completion!.Source);
            Assert.Equal(1, completion.ChainDepth);
        }
        finally
        {
            await _client.DeleteAsync($"/api/agents/{listener.Id}");
        }
    }

    [Fact]
    public async Task Invalid_topics_are_rejected_when_raising()
    {
        var response = await _client.PostAsJsonAsync("/api/events",
            new { topic = "Not A Topic!" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
