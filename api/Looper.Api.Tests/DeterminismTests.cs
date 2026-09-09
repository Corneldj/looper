using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Features.Architect;
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
// Fail closed, never silently. Every way a run could look green without the
// work being done is turned into an honest failure the user can see and fix.
// ============================================================================

public class ResultInterpretationTests
{
    private static AgentExecutionOutcome Interpret(string json, int exitCode = 0) =>
        ClaudeCliExecutor.InterpretResult(JsonDocument.Parse(json).RootElement, exitCode, 5);

    [Fact]
    public void A_clean_result_is_a_success()
    {
        var outcome = Interpret("""{"result":"Shipped the fix.","subtype":"success","is_error":false,"num_turns":4,"total_cost_usd":0.2}""");
        Assert.True(outcome.Success);
        Assert.Equal("Shipped the fix.", outcome.ResultText);
        Assert.Equal(0.2m, outcome.CostUsd);
    }

    [Fact]
    public void Hitting_the_turn_cap_is_a_failure_that_says_so()
    {
        var outcome = Interpret("""{"result":"Still working on the migration…","subtype":"error_max_turns","is_error":false,"num_turns":25}""");
        Assert.False(outcome.Success);
        Assert.Contains("turn limit after 25 turns", outcome.ErrorMessage);
        Assert.Contains("Still working on the migration", outcome.ErrorMessage);
    }

    [Fact]
    public void An_unknown_error_subtype_and_the_budget_cap_fail_too()
    {
        Assert.Contains("budget cap", Interpret("""{"result":"","subtype":"error_max_budget_usd","is_error":false}""").ErrorMessage);
        Assert.Contains("error_during_execution", Interpret("""{"result":"x","subtype":"error_during_execution","is_error":false}""").ErrorMessage);
    }

    [Fact]
    public void A_run_that_reports_nothing_is_not_a_silent_success()
    {
        var outcome = Interpret("""{"result":"   ","subtype":"success","is_error":false,"num_turns":3}""");
        Assert.False(outcome.Success);
        Assert.Contains("without reporting anything", outcome.ErrorMessage);
    }

    [Fact]
    public void Exit_codes_and_the_error_flag_still_fail_with_the_message_when_there_is_one()
    {
        Assert.Equal("Run failed (exit code 2).", Interpret("""{"result":""}""", exitCode: 2).ErrorMessage);
        Assert.Equal("Permission denied.", Interpret("""{"result":"Permission denied.","is_error":true}""").ErrorMessage);
    }
}

public class ArchitectDeterminismPromptTests
{
    private static string Prompt() => BuildWorkflowHandler.BuildArchitectPrompt("x", "{}", "http://looper.test:5210");

    [Fact]
    public void The_architect_can_test_scripts_inspect_workflows_and_verify_its_wiring()
    {
        var prompt = Prompt();
        Assert.Contains("curl -s -X POST http://looper.test:5210/api/scripts/run", prompt);
        Assert.Contains("http://looper.test:5210/api/workflows", prompt);
        Assert.Contains("/api/architecture/map?workflowId=", prompt);
        Assert.Contains("/api/events/topics", prompt);
        Assert.Contains("/api/agents/<agent id>/resources/<resource id>", prompt);
    }

    [Theory]
    [InlineData("customTypeKey \"Script\"")]
    [InlineData("customTypeKey \"Metric\"")]
    [InlineData("customTypeKey \"EventRaiser\"")]
    [InlineData("customTypeKey \"Specification\"")]
    [InlineData("- UserAction:")]
    public void Every_resource_type_added_this_year_is_documented(string marker)
    {
        Assert.Contains(marker, Prompt());
    }

    [Fact]
    public void The_architect_is_told_to_build_to_the_fail_closed_rules()
    {
        var prompt = Prompt();
        Assert.Contains("DETERMINISM", prompt);
        Assert.Contains("only when the model finished AND every gate passed", prompt);
        Assert.Contains("clean context", prompt);
    }
}

/// <summary>Harness-level guards, through the real coordinator and a fake CLI that always answers "done".</summary>
public sealed class FailClosedHarnessTests : IDisposable
{
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly string _dir;
    private readonly string _argsFile;
    private readonly string _fakeClaude;

    public FailClosedHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"looper-failclosed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "harness.db")}").Options;
        using (var db = new LooperDbContext(_options)) db.Database.EnsureCreated();
        _argsFile = Path.Combine(_dir, "claude-args.txt");
        _fakeClaude = Path.Combine(_dir, "claude");
        File.WriteAllText(_fakeClaude,
            $"#!/bin/bash\nprintf '%s\\n' \"$@\" > '{_argsFile}'\n" +
            "echo '{\"result\":\"done\",\"subtype\":\"success\",\"total_cost_usd\":0.01,\"is_error\":false,\"num_turns\":1,\"duration_ms\":5}'\n");
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

    private AgentRunCoordinator CreateCoordinator()
    {
        var looperOptions = Options.Create(new LooperOptions { MaxConcurrentRuns = 2, RunTimeoutMinutes = 5, ClaudeCommand = _fakeClaude, TestingActionTimeoutSeconds = 20 });
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new ScriptModule());
        return new AgentRunCoordinator(
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
    }

    private async Task<AgentRun> RunToCompletion(params Resource[] resources)
    {
        Guid agentId;
        await using (var db = new LooperDbContext(_options))
        {
            var agent = new LoopAgent { Name = "Guarded loop", Prompt = "Work.", Model = "claude-opus-5", DryRun = false, MaxTurns = 5, WorkingDirectory = _dir };
            agent.Resources.AddRange(resources);
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
        }
        var runId = (await CreateCoordinator().TriggerRunAsync(agentId, RunTrigger.Manual))!.Value;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = new LooperDbContext(_options);
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
            if (run is not null && run.Status != RunStatus.Running) return run;
            await Task.Delay(200);
        }
        throw new TimeoutException("run did not complete");
    }

    [Fact]
    public async Task A_failed_gate_makes_the_run_a_failure_not_a_success_with_a_footnote()
    {
        var run = await RunToCompletion(new Resource
        {
            Name = "Checks", Type = ResourceType.TestingAction, ConfigJson = """{"command":"echo '2 checks failed'; exit 3"}"""
        });

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.False(run.TestsPassed);
        Assert.Contains("Gate failed: 'Checks'", run.ErrorMessage);
        Assert.True(File.Exists(_argsFile));   // the model did run — it is the verdict that is honest

        await using var db = new LooperDbContext(_options);
        var completion = await db.Events.SingleAsync(e => e.SourceRunId == run.Id);
        Assert.Equal("agent.guarded-loop.failed", completion.Topic);   // chained loops see the truth too
    }

    [Fact]
    public async Task Gates_with_nothing_configured_fail_closed_instead_of_being_skipped()
    {
        var run = await RunToCompletion(
            new Resource { Name = "Empty action", Type = ResourceType.TestingAction, ConfigJson = """{"command":"   "}""" },
            new Resource { Name = "Empty script", Type = ResourceType.Custom, CustomTypeKey = "Script", ConfigJson = """{"language":"python","code":"","trigger":"after"}""" });

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("'Empty action'", run.ErrorMessage);
        Assert.Contains("'Empty script'", run.ErrorMessage);
        Assert.Contains("fails closed", run.TestResultsJson);
    }

    [Fact]
    public async Task A_resource_that_cannot_be_applied_stops_the_run_before_the_model_starts()
    {
        var run = await RunToCompletion(new Resource
        {
            Name = "Ghost tool", Type = ResourceType.Custom, CustomTypeKey = "NotInstalledType", ConfigJson = "{}"
        });

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("'Ghost tool'", run.ErrorMessage);
        Assert.Contains("not installed", run.ErrorMessage);
        Assert.False(File.Exists(_argsFile));  // no tokens were spent on a run missing its premises
        Assert.Equal(0m, run.CostUsd);
    }

    [Fact]
    public async Task A_reviewer_without_a_rubric_fails_closed()
    {
        var run = await RunToCompletion(new Resource
        {
            Name = "Vague reviewer", Type = ResourceType.Reviewer, ConfigJson = """{"rubric":"  ","maxFixRounds":1}"""
        });

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("'Vague reviewer' has no rubric", run.ErrorMessage);
        Assert.False(run.ReviewPassed);
    }
}

public class MetricLineSurvivalTests
{
    [Fact]
    public async Task Metric_lines_survive_output_truncation()
    {
        var runner = new ScriptRunner(Options.Create(new LooperOptions { TestingActionTimeoutSeconds = 30 }));
        var id = Guid.NewGuid();
        var cwd = Path.Combine(Path.GetTempPath(), $"looper-metric-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cwd);
        try
        {
            // The measurement is printed first, followed by far more noise than the output cap keeps.
            var config = ScriptResources.Parse("python", "print('@metric sign-ups=12 from the list')\nprint('x' * 9000)", "after", null, null, null);
            var result = await runner.RunAsync(id, "chatty", config, cwd, null, CancellationToken.None);

            Assert.True(result.Output.Length < 9000);
            Assert.Contains("@metric sign-ups=12 from the list", result.Output);
            var parsed = Assert.Single(MetricRecorder.ParseScriptOutput(result.Output));
            Assert.Equal(12, parsed.Value);
        }
        finally
        {
            ScriptResources.RemoveMaterialized(id);
            Directory.Delete(cwd, recursive: true);
        }
    }
}
