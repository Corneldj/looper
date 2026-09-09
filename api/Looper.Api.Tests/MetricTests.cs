using System.Net;
using System.Net.Http.Json;
using Looper.Api.Domain;
using Looper.Api.Features.Metrics;
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
// User-defined metrics: the generic outcome layer. A Metric resource names what
// the user cares about; agents, scripts and people report values; the dashboard
// summarizes them per the metric's own semantics.
// ============================================================================

public class MetricModuleTests
{
    private static readonly Guid Id = Guid.NewGuid();

    private static ResourceContribution Contribute(string json, string name = "Sign-ups", string description = "") =>
        new MetricModule().Contribute(new ResourceModuleContext(json, null, null, Id, name, description));

    [Fact]
    public void A_sum_metric_injects_the_protocol_with_additive_semantics_and_the_goal()
    {
        var contribution = Contribute(
            """{"unit":"sign-ups","aggregation":"sum","direction":"higher","target":1000,"instructions":"Read the analytics API after each send."}""",
            description: "Newsletter conversions.");

        Assert.Equal(Id.ToString(), contribution.EnvironmentVariables["LOOPER_METRIC_SIGN_UPS"]);
        var section = Assert.Single(contribution.PromptSections);
        Assert.Contains("METRIC 'Sign-ups' (unit: sign-ups)", section);
        Assert.Contains("Newsletter conversions.", section);
        Assert.Contains("How to measure it: Read the analytics API", section);
        Assert.Contains("ADD UP", section);
        Assert.Contains("Higher is better; the target is 1000 sign-ups.", section);
        Assert.Contains($"\\\"metric\\\":\\\"{Id}\\\"", section);
        Assert.Contains("@metric sign-ups=<number>", section);
        Assert.Contains("never estimates", section);
    }

    [Fact]
    public void Gauge_and_average_metrics_explain_their_own_semantics()
    {
        Assert.Contains("LATEST value is the current reading", Contribute("""{"aggregation":"latest","direction":"lower"}""").PromptSections[0]);
        Assert.Contains("Lower is better.", Contribute("""{"aggregation":"latest","direction":"lower"}""").PromptSections[0]);
        Assert.Contains("AVERAGED", Contribute("""{"aggregation":"average"}""").PromptSections[0]);
    }

    [Fact]
    public void Without_a_resource_identity_nothing_is_contributed()
    {
        Assert.Empty(new MetricModule().Contribute(new ResourceModuleContext("""{"aggregation":"sum"}""")).PromptSections);
    }

    [Fact]
    public void Config_parses_with_forgiving_defaults()
    {
        var config = MetricResources.Parse(new ResourceModuleContext("""{"unit":" % ","aggregation":"AVG","direction":"LOWER","target":12.5}"""));
        Assert.Equal("%", config.Unit);
        Assert.Equal(MetricAggregation.Average, config.Aggregation);
        Assert.Equal(MetricDirection.Lower, config.Direction);
        Assert.Equal(12.5, config.Target);

        var defaults = MetricResources.Parse(new ResourceModuleContext("{}"));
        Assert.Equal(MetricAggregation.Latest, defaults.Aggregation);
        Assert.Equal(MetricDirection.Higher, defaults.Direction);
        Assert.Null(defaults.Target);
    }

    [Fact]
    public void Reporters_can_address_a_metric_by_id_name_or_slug()
    {
        var metric = new Resource { Name = "Sign-ups (weekly)", Type = ResourceType.Custom, CustomTypeKey = "Metric" };
        var rule = new Resource { Name = "sign-ups-weekly", Type = ResourceType.Rule };
        Resource[] pool = [rule, metric];

        Assert.Same(metric, MetricResources.Match(pool, metric.Id.ToString()));
        Assert.Same(metric, MetricResources.Match(pool, "sign-ups (WEEKLY)"));
        Assert.Same(metric, MetricResources.Match(pool, "Sign_ups weekly"));
        Assert.Null(MetricResources.Match(pool, "conversion"));
        Assert.Null(MetricResources.Match(pool, ""));
    }
}

public class MetricMathTests
{
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    private static MetricMath.Sample At(int daysAgo, double value) => new(value, Now.AddDays(-daysAgo));

    private static MetricConfig Config(MetricAggregation aggregation) => new("", aggregation, MetricDirection.Higher, null, "");

    [Fact]
    public void A_sum_metric_totals_the_window_and_trends_against_the_previous_window()
    {
        var summary = MetricMath.Summarize(Config(MetricAggregation.Sum),
            [At(20, 5), At(10, 3), At(6, 4), At(1, 6)], Now, days: 7);

        Assert.Equal(10, summary.Current);      // 4 + 6 inside the last 7 days
        Assert.Equal(3, summary.Previous);      // the 3 from 10 days ago; 20 days ago is out of both windows
        Assert.NotNull(summary.TrendPct);
        Assert.Equal(233.3, Math.Round(summary.TrendPct!.Value, 1));
        Assert.Equal(2, summary.CountInWindow);
        Assert.Equal(6, summary.Latest);
        Assert.Equal(2, summary.Series.Count);
        Assert.Equal(4, summary.Series[0].Value);
    }

    [Fact]
    public void A_gauge_reports_its_newest_reading_and_compares_with_where_it_stood_before_the_window()
    {
        var summary = MetricMath.Summarize(Config(MetricAggregation.Latest),
            [At(12, 2.0), At(9, 2.5), At(3, 3.0), At(3, 3.4)], Now, days: 7);

        Assert.Equal(3.4, summary.Current);
        Assert.Equal(2.5, summary.Previous);
        Assert.Equal(36, Math.Round(summary.TrendPct!.Value));
        // Same-day readings collapse to the last one in the sparkline.
        var point = Assert.Single(summary.Series);
        Assert.Equal(3.4, point.Value);
    }

    [Fact]
    public void An_average_metric_averages_each_window_and_a_daily_bucket()
    {
        var summary = MetricMath.Summarize(Config(MetricAggregation.Average),
            [At(10, 100), At(2, 40), At(2, 60), At(1, 80)], Now, days: 7);

        Assert.Equal(60, summary.Current);
        Assert.Equal(100, summary.Previous);
        Assert.Equal(-40, summary.TrendPct);
        Assert.Equal(50, summary.Series[0].Value);
    }

    [Fact]
    public void No_data_or_no_baseline_means_no_trend_rather_than_a_fake_one()
    {
        var empty = MetricMath.Summarize(Config(MetricAggregation.Sum), [], Now, 7);
        Assert.Null(empty.Current);
        Assert.Null(empty.TrendPct);
        Assert.Null(empty.Latest);

        var fresh = MetricMath.Summarize(Config(MetricAggregation.Latest), [At(1, 5)], Now, 7);
        Assert.Equal(5, fresh.Current);
        Assert.Null(fresh.Previous);
        Assert.Null(fresh.TrendPct);

        // A gauge with only stale readings still shows its last value as current.
        var stale = MetricMath.Summarize(Config(MetricAggregation.Latest), [At(30, 9)], Now, 7);
        Assert.Equal(9, stale.Current);
        Assert.Null(stale.TrendPct);
        Assert.Equal(0, stale.CountInWindow);
    }
}

public class MetricRecorderParsingTests
{
    [Fact]
    public void Parses_metric_lines_out_of_ordinary_script_output()
    {
        var lines = MetricRecorder.ParseScriptOutput(
            "Fetching…\n@metric sign-ups=12 from the campaign list\n  @metric CTR = 3.4\n@metric bad=abc\nnoise @metric x=1\n@metric neg=-2.5e1\n");

        Assert.Equal(3, lines.Count);
        Assert.Equal(new ScriptMetricLine("sign-ups", 12, "from the campaign list"), lines[0]);
        Assert.Equal(new ScriptMetricLine("CTR", 3.4, null), lines[1]);
        Assert.Equal(new ScriptMetricLine("neg", -25, null), lines[2]);
        Assert.Empty(MetricRecorder.ParseScriptOutput(null));
        Assert.Empty(MetricRecorder.ParseScriptOutput("plain output"));
    }
}

public class MetricsApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record IdResponse(Guid Id);
    private sealed record ValueResponse(Guid Id, Guid ResourceId, Guid? AgentId, string? AgentName, Guid? RunId, double Value, string? Note, string Source);
    private sealed record SummaryResponse(Guid ResourceId, string Name, string Unit, string Aggregation, string Direction, double? Target,
        double? Latest, double? Current, double? Previous, double? TrendPct, int CountInWindow, int TotalCount, List<PointResponse> Series, List<string> Agents);
    private sealed record PointResponse(string Date, double Value);
    private sealed record RunStart(Guid RunId);

    private async Task<Guid> CreateMetric(string name, string configJson)
    {
        var response = await _client.PostAsJsonAsync("/api/resources", new
        {
            name, type = "Custom", customTypeKey = "Metric", description = "test metric", configJson
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
    }

    private Task<HttpResponseMessage> Record(object body) =>
        _client.PostAsJsonAsync("/api/metrics/values", body, TestJson.Options);

    [Fact]
    public async Task Metric_is_a_built_in_type_in_the_catalog()
    {
        var types = await _client.GetFromJsonAsync<List<Dictionary<string, System.Text.Json.JsonElement>>>("/api/resource-types", TestJson.Options);
        var metric = Assert.Single(types!, t => t["typeKey"].GetString() == "Metric");
        Assert.True(metric["builtIn"].GetBoolean());
    }

    [Fact]
    public async Task A_metric_collects_values_by_id_name_or_slug_and_summarizes_them()
    {
        var id = await CreateMetric("Newsletter sign-ups", """{"unit":"sign-ups","aggregation":"sum","direction":"higher","target":100}""");
        try
        {
            Assert.Equal(HttpStatusCode.Created, (await Record(new { metric = id, value = 12, note = "launch day" })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await Record(new { metric = "newsletter sign-ups", value = 3 })).StatusCode);
            var bySlug = await Record(new { metric = "Newsletter_Sign_ups", value = 5, source = "manual" });
            Assert.Equal(HttpStatusCode.Created, bySlug.StatusCode);
            Assert.Equal("Manual", (await bySlug.Content.ReadFromJsonAsync<ValueResponse>(TestJson.Options))!.Source);

            var summaries = await _client.GetFromJsonAsync<List<SummaryResponse>>("/api/metrics?days=30", TestJson.Options);
            var summary = Assert.Single(summaries!, s => s.ResourceId == id);
            Assert.Equal("Newsletter sign-ups", summary.Name);
            Assert.Equal("Sum", summary.Aggregation);
            Assert.Equal(100, summary.Target);
            Assert.Equal(20, summary.Current);
            Assert.Equal(5, summary.Latest);
            Assert.Equal(3, summary.TotalCount);
            Assert.Null(summary.TrendPct);              // nothing in the previous window
            Assert.Single(summary.Series);              // all today
            Assert.Equal(20, summary.Series[0].Value);

            var values = await _client.GetFromJsonAsync<List<ValueResponse>>($"/api/metrics/{id}/values", TestJson.Options);
            Assert.Equal(3, values!.Count);
            Assert.Equal(5, values[0].Value);           // newest first
            Assert.Equal("launch day", values[2].Note);
            Assert.Equal("Api", values[2].Source);

            var delete = await _client.DeleteAsync($"/api/metrics/values/{values[0].Id}");
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
            Assert.Equal(2, (await _client.GetFromJsonAsync<List<ValueResponse>>($"/api/metrics/{id}/values", TestJson.Options))!.Count);
        }
        finally
        {
            await _client.DeleteAsync($"/api/resources/{id}");
        }

        // Deleting the metric takes its values with it and drops it from the dashboard.
        Assert.DoesNotContain(await _client.GetFromJsonAsync<List<SummaryResponse>>("/api/metrics", TestJson.Options) ?? [], s => s.ResourceId == id);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/metrics/{id}/values")).StatusCode);
    }

    [Fact]
    public async Task Values_reported_with_a_run_id_are_attributed_to_the_agent()
    {
        var metricId = await CreateMetric("Resolved tickets", """{"aggregation":"sum"}""");
        var agent = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "Support loop", description = "", prompt = "Resolve tickets.", model = "claude-haiku-4-5", effort = "Low",
            intervalMinutes = 60, triggerMode = "Scheduled", triggerTopics = (string?)null, maxTurns = 5,
            maxBudgetUsd = (decimal?)null, workingDirectory = (string?)null, allowedTools = (string?)null,
            bypassPermissions = true, dryRun = true, autonomyLevel = 2, resourceIds = new[] { metricId }
        }, TestJson.Options);
        var agentId = (await agent.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
        try
        {
            var run = await _client.PostAsync($"/api/agents/{agentId}/run", null);
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            var runId = (await run.Content.ReadFromJsonAsync<RunStart>(TestJson.Options))!.RunId;

            var recorded = await Record(new { metric = metricId, value = 4, runId, note = "closed 4 tickets" });
            Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
            var value = await recorded.Content.ReadFromJsonAsync<ValueResponse>(TestJson.Options);
            Assert.Equal(agentId, value!.AgentId);
            Assert.Equal("Support loop", value.AgentName);
            Assert.Equal("Agent", value.Source);

            var summary = Assert.Single((await _client.GetFromJsonAsync<List<SummaryResponse>>("/api/metrics", TestJson.Options))!, s => s.ResourceId == metricId);
            Assert.Equal(["Support loop"], summary.Agents);

            var mapAgent = (await _client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/architecture/map", TestJson.Options))
                .GetProperty("agents").EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == agentId);
            var mapMetric = Assert.Single(mapAgent.GetProperty("metrics").EnumerateArray());
            Assert.Equal("Resolved tickets", mapMetric.GetProperty("name").GetString());
            Assert.Equal(4, mapMetric.GetProperty("current").GetDouble());
        }
        finally
        {
            await _client.PostAsync($"/api/agents/{agentId}/cancel", null);
            await _client.DeleteAsync($"/api/agents/{agentId}");
            await _client.DeleteAsync($"/api/resources/{metricId}");
        }
    }

    [Fact]
    public async Task Bad_reports_are_rejected_with_guidance()
    {
        var unknownName = await Record(new { metric = "does-not-exist", value = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, unknownName.StatusCode);
        Assert.Contains("No metric named", await unknownName.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await Record(new { metric = Guid.NewGuid(), value = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Record(new { metric = "", value = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/metrics/values/{Guid.NewGuid()}")).StatusCode);
    }
}

/// <summary>Scripts report metrics by printing lines; the harness records them from both stages.</summary>
public sealed class MetricHarnessTests : IDisposable
{
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly string _dir;
    private readonly string _fakeClaude;

    public MetricHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"looper-metric-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        // File database: background run threads and the polling test each get their own connection.
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "harness.db")}").Options;
        using (var db = new LooperDbContext(_options)) db.Database.EnsureCreated();

        _fakeClaude = Path.Combine(_dir, "claude");
        File.WriteAllText(_fakeClaude,
            "#!/bin/bash\necho '{\"result\":\"done\",\"total_cost_usd\":0.01,\"is_error\":false,\"num_turns\":1,\"duration_ms\":5,\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}'\n");
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

    [Fact]
    public async Task Before_and_after_scripts_report_metrics_that_land_attributed_to_the_run()
    {
        var metric = new Resource
        {
            Name = "Sign-ups", Type = ResourceType.Custom, CustomTypeKey = "Metric",
            ConfigJson = """{"unit":"sign-ups","aggregation":"sum"}"""
        };
        var before = new Resource
        {
            Name = "Collect", Type = ResourceType.Custom, CustomTypeKey = "Script",
            ConfigJson = """{"language":"python","code":"print('list fetched')\nprint('@metric sign-ups=12 from the list')","trigger":"before"}"""
        };
        var after = new Resource
        {
            Name = "Verify", Type = ResourceType.Custom, CustomTypeKey = "Script",
            ConfigJson = """{"language":"bash","code":"echo '@metric Sign-ups=3'\necho '@metric unknown-thing=1'","trigger":"after"}"""
        };
        Guid agentId;
        await using (var db = new LooperDbContext(_options))
        {
            var agent = new LoopAgent
            {
                Name = "Campaign loop", Prompt = "Send the newsletter.", Model = "claude-opus-5",
                DryRun = false, MaxTurns = 5, WorkingDirectory = _dir, Resources = { metric, before, after }
            };
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
        }

        var looperOptions = Options.Create(new LooperOptions { MaxConcurrentRuns = 2, RunTimeoutMinutes = 5, ClaudeCommand = _fakeClaude });
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new ScriptModule());
        registry.RegisterBuiltIn(new MetricModule());
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

        try
        {
            var runId = (await coordinator.TriggerRunAsync(agentId, RunTrigger.Manual))!.Value;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            AgentRun? run = null;
            while (DateTime.UtcNow < deadline)
            {
                await using var db = new LooperDbContext(_options);
                run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
                if (run is not null && run.Status != RunStatus.Running) break;
                await Task.Delay(200);
            }
            Assert.True(run!.Status == RunStatus.Succeeded, $"run ended {run.Status}: {run.ErrorMessage}");

            // The after-stage recording happens right after finalize, on the run's own task —
            // wait for it rather than racing it.
            List<MetricValue> values = [];
            var valuesDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (values.Count < 2 && DateTime.UtcNow < valuesDeadline)
            {
                await Task.Delay(100);
                await using var db = new LooperDbContext(_options);
                values = await db.MetricValues.AsNoTracking().OrderBy(v => v.RecordedAtUtc).ToListAsync();
            }

            Assert.True(values.Count == 2, $"expected 2 values, found {values.Count}: " +
                string.Join(" | ", values.Select(v => $"{v.Value} ({v.Source}) {v.Note}")));
            Assert.All(values, v =>
            {
                Assert.Equal(metric.Id, v.ResourceId);
                Assert.Equal(agentId, v.AgentId);
                Assert.Equal(runId, v.RunId);
                Assert.Equal(MetricSource.Script, v.Source);
            });
            Assert.Equal(12, values[0].Value);
            Assert.Equal("from the list", values[0].Note);
            Assert.Equal(3, values[1].Value);

            await using var logDb = new LooperDbContext(_options);
            var logs = await logDb.RunLogs.Where(l => l.RunId == runId).Select(l => l.Message).ToListAsync();
            Assert.Contains(logs, m => m.Contains("Metric 'Sign-ups' = 12 recorded from script 'Collect'"));
            Assert.Contains(logs, m => m.Contains("'@metric unknown-thing'") && m.Contains("ignored"));
        }
        finally
        {
            ScriptResources.RemoveMaterialized(before.Id);
            ScriptResources.RemoveMaterialized(after.Id);
        }
    }
}
