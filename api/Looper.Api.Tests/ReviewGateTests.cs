using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;

namespace Looper.Api.Tests;

public class ReviewVerdictParsingTests
{
    [Fact]
    public void Parses_a_clean_pass_verdict()
    {
        var verdict = ReviewRunner.ParseVerdict("""{"verdict":"pass","summary":"Looks solid."}""");

        Assert.True(verdict.Pass);
        Assert.False(verdict.Inconclusive);
        Assert.Equal("Looks solid.", verdict.Summary);
        Assert.Null(verdict.FixInstructions);
    }

    [Fact]
    public void Extracts_the_verdict_from_surrounding_prose()
    {
        var text = "I inspected the changes. The tests are missing.\n\n" +
                   """{"verdict":"fail","summary":"No tests for the new endpoint.","fixInstructions":"Add coverage for the 404 path."}""" +
                   "\nThat is my assessment.";

        var verdict = ReviewRunner.ParseVerdict(text);

        Assert.False(verdict.Pass);
        Assert.False(verdict.Inconclusive);
        Assert.Equal("Add coverage for the 404 path.", verdict.FixInstructions);
    }

    [Fact]
    public void The_last_verdict_object_wins_when_several_appear()
    {
        var text = """{"verdict":"fail","summary":"draft thinking"} … final answer: {"verdict":"pass","summary":"All rubric points hold."}""";

        var verdict = ReviewRunner.ParseVerdict(text);

        Assert.True(verdict.Pass);
        Assert.Equal("All rubric points hold.", verdict.Summary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I think it's fine, ship it.")]
    [InlineData("""{"unrelated":"json"}""")]
    public void No_parseable_verdict_fails_closed_as_inconclusive(string? text)
    {
        var verdict = ReviewRunner.ParseVerdict(text);

        Assert.False(verdict.Pass);       // a gate that shrugs is not a gate
        Assert.True(verdict.Inconclusive);
    }

    [Fact]
    public void Reviewer_config_defaults_are_sane()
    {
        var config = ResourceConfig.Parse<ReviewerConfig>(new Resource { ConfigJson = "{}" });

        Assert.Equal(2, config.MaxFixRounds);
        Assert.False(config.EscalateOnFail);
        Assert.Null(config.Model);
    }
}

public sealed class ReviewGateCoordinatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;

    public ReviewGateCoordinatorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(DbContextOptions<LooperDbContext> options) : IDbContextFactory<LooperDbContext>
    {
        public LooperDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Dry_run_with_a_reviewer_records_a_simulated_passing_review()
    {
        Guid agentId;
        await using (var db = new LooperDbContext(_options))
        {
            var reviewer = new Resource
            {
                Name = "Quality bar",
                Type = ResourceType.Reviewer,
                ConfigJson = """{"rubric":"All changes are tested.","maxFixRounds":2}"""
            };
            var agent = new LoopAgent
            {
                Name = "Reviewed agent", Prompt = "Do the thing.", Model = "claude-opus-5",
                DryRun = true, MaxTurns = 5, Resources = { reviewer },
            };
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
        }

        var looperOptions = Options.Create(new LooperOptions { MaxConcurrentRuns = 2, RunTimeoutMinutes = 5 });
        var coordinator = new AgentRunCoordinator(
            new Factory(_options),
            new ClaudeCliExecutor(looperOptions, new ClaudeAuthProvider(new Factory(_options)), new Looper.Api.Modules.ResourceModuleRegistry(
                NullLogger<Looper.Api.Modules.ResourceModuleRegistry>.Instance),
                new GraphContextService(looperOptions, NullLogger<GraphContextService>.Instance),
                NullLogger<ClaudeCliExecutor>.Instance),
            new SimulatedAgentExecutor(),
            new TestingActionRunner(looperOptions),
            new ScriptRunner(looperOptions),
            new MetricRecorder(new Factory(_options), NullLogger<MetricRecorder>.Instance),
            new ReviewRunner(looperOptions, new ClaudeAuthProvider(new Factory(_options)), NullLogger<ReviewRunner>.Instance),
            new EventDispatcher(NullLogger<EventDispatcher>.Instance),
            looperOptions,
            NullLogger<AgentRunCoordinator>.Instance);

        var runId = await coordinator.TriggerRunAsync(agentId, RunTrigger.Manual);
        Assert.NotNull(runId);

        AgentRun? run = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = new LooperDbContext(_options);
            run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
            if (run is not null && run.Status != RunStatus.Running) break;
            await Task.Delay(200);
        }

        Assert.NotNull(run);
        if (run!.Status == RunStatus.Succeeded)
        {
            Assert.True(run.ReviewPassed);
            Assert.Equal(0, run.ReviewRounds);
            Assert.NotNull(run.ReviewJson);
            Assert.Contains("Quality bar", run.ReviewJson);

            await using var db = new LooperDbContext(_options);
            var logs = await db.RunLogs.Where(l => l.RunId == run.Id).Select(l => l.Message).ToListAsync();
            Assert.Contains(logs, m => m.Contains("[dry run] Review 'Quality bar' simulated: PASS."));
        }
        else
        {
            // The simulator fails ~8% of runs; a failed worker must leave the gate untouched.
            Assert.Null(run.ReviewPassed);
        }
    }
}

public class ReviewerResourceApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record TypeResponse(string TypeKey, string Icon, bool BuiltIn);
    private sealed record ResourceResponse(Guid Id, string Type, string ConfigJson);

    [Fact]
    public async Task Reviewer_is_a_built_in_type_and_its_resources_round_trip()
    {
        var types = await _client.GetFromJsonAsync<List<TypeResponse>>("/api/resource-types", TestJson.Options);
        var reviewer = Assert.Single(types!, t => t.TypeKey == "Reviewer");
        Assert.True(reviewer.BuiltIn);
        Assert.Equal("🧐", reviewer.Icon);

        var create = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "Definition of done",
            type = "Reviewer",
            description = "",
            configJson = """{"rubric":"Tests exist and pass; no dead code.","maxFixRounds":1,"escalateOnFail":true}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var resource = await create.Content.ReadFromJsonAsync<ResourceResponse>(TestJson.Options);
        Assert.Equal("Reviewer", resource!.Type);
        Assert.Contains("no dead code", resource.ConfigJson); // rubric is not a secret — never masked

        (await _client.DeleteAsync($"/api/resources/{resource.Id}")).EnsureSuccessStatusCode();
    }
}
