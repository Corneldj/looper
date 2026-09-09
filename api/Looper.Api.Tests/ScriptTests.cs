using System.Net;
using System.Net.Http.Json;
using Looper.Api.Domain;
using Looper.Api.Features.Architect;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Looper.Api.Tests;

// ============================================================================
// Script resources: runnable Python/Bash kept as a resource, materialized to disk,
// run by the agent on demand or by the harness before/after every iteration.
// ============================================================================

public class ScriptModuleTests
{
    private static readonly Guid Id = Guid.NewGuid();

    private static ResourceModuleContext Context(string json, string name = "Nightly Report", string description = "") =>
        new(json, null, null, Id, name, description);

    [Fact]
    public void Contribute_exposes_the_path_the_directory_and_run_instructions()
    {
        var contribution = new ScriptModule().Contribute(Context(
            """{"language":"python","code":"print(1)","trigger":"before","args":"--verbose"}""",
            description: "Summarises yesterday's tickets."));

        var path = ScriptResources.ScriptPath(Id, "Nightly Report", ScriptResources.Parse("python", "print(1)", null, null, null, null));
        Assert.Equal(path, contribution.EnvironmentVariables["LOOPER_SCRIPT_NIGHTLY_REPORT"]);
        Assert.Contains(Path.GetDirectoryName(path)!, contribution.AdditionalDirectories);

        var section = Assert.Single(contribution.PromptSections);
        Assert.Contains("SCRIPT 'Nightly Report' (python)", section);
        Assert.Contains("Purpose: Summarises yesterday's tickets.", section);
        Assert.Contains("python3 \"$LOOPER_SCRIPT_NIGHTLY_REPORT\" --verbose", section);
        Assert.Contains("SCRIPT OUTPUT section", section);
        Assert.Contains("do not edit it", section);
    }

    [Fact]
    public void The_trigger_changes_what_the_agent_is_told()
    {
        var before = new ScriptModule().Contribute(Context("""{"code":"x","trigger":"before"}""")).PromptSections[0];
        var after = new ScriptModule().Contribute(Context("""{"code":"x","trigger":"after","language":"bash"}""")).PromptSections[0];

        Assert.Contains("SCRIPT OUTPUT section", before);
        Assert.Contains("as a gate", after);
        Assert.Contains("bash \"$LOOPER_SCRIPT_NIGHTLY_REPORT\"", after);
    }

    [Fact]
    public void Without_a_resource_identity_or_code_nothing_is_contributed()
    {
        Assert.Empty(new ScriptModule().Contribute(new ResourceModuleContext("""{"code":"print(1)"}""")).PromptSections);
        Assert.Empty(new ScriptModule().Contribute(Context("{}")).PromptSections);
    }

    [Fact]
    public void PrepareRun_materializes_the_script_and_prunes_stale_projections()
    {
        var id = Guid.NewGuid();
        var module = new ScriptModule();
        try
        {
            module.PrepareRun(new ResourceModuleContext("""{"language":"python","code":"print('a')"}""", null, null, id, "First Name", null));
            var first = Path.Combine(ScriptResources.ScriptDirectory(id), "first-name.py");
            Assert.True(File.Exists(first));
            Assert.Equal("print('a')\n", File.ReadAllText(first));
            Assert.True(File.GetUnixFileMode(first).HasFlag(UnixFileMode.UserExecute));

            // Rename + language switch: the old projection goes away, the new one appears.
            module.PrepareRun(new ResourceModuleContext("""{"language":"bash","code":"echo b"}""", null, null, id, "Second", null));
            Assert.False(File.Exists(first));
            Assert.Equal("echo b\n", File.ReadAllText(Path.Combine(ScriptResources.ScriptDirectory(id), "second.sh")));

            // No identity: nothing is written anywhere.
            module.PrepareRun(new ResourceModuleContext("""{"code":"print(1)"}"""));
        }
        finally
        {
            ScriptResources.RemoveMaterialized(id);
        }
        Assert.False(Directory.Exists(ScriptResources.ScriptDirectory(id)));
    }

    [Fact]
    public void Names_map_deterministically_to_slugs_and_env_vars()
    {
        Assert.Equal("nightly-report", ScriptResources.Slug("  Nightly  Report!! "));
        Assert.Equal("script", ScriptResources.Slug("###"));
        Assert.Equal("LOOPER_SCRIPT_NIGHTLY_REPORT", ScriptResources.EnvVarName("Nightly Report"));
    }

    [Fact]
    public void Commands_quote_the_path_and_append_user_args()
    {
        var python = ScriptResources.Parse("python", "x", null, "--n 3", null, null);
        Assert.Equal("python3 '/tmp/it'\\''s here/a.py' --n 3", ScriptResources.BuildCommand(python, "/tmp/it's here/a.py", "python3"));

        var bash = ScriptResources.Parse("bash", "x", null, "", null, null);
        Assert.Equal("/bin/bash '/tmp/a.sh'", ScriptResources.BuildCommand(bash, "/tmp/a.sh", "python3"));
    }

    [Fact]
    public void Parse_normalizes_the_trigger_and_the_timeout()
    {
        var config = ScriptResources.Parse("PYTHON", "x", "AFTER", " -v ", 12.6, "  ");
        Assert.Equal(ScriptLanguage.Python, config.Language);
        Assert.Equal(ScriptModule.TriggerAfter, config.Trigger);
        Assert.Equal("-v", config.Args);
        Assert.Equal(13, config.TimeoutSeconds);
        Assert.Null(config.WorkingDirectory);
        // The retired on-demand mode, and anything unknown, runs before — deterministic by construction.
        Assert.Equal(ScriptModule.TriggerBefore, ScriptResources.Parse(null, "x", "agent", null, 0, null).Trigger);
        Assert.Equal(ScriptModule.TriggerBefore, ScriptResources.Parse(null, "x", "whenever", null, 0, null).Trigger);
    }

    [Fact]
    public void The_architect_toolset_documents_scripts()
    {
        var prompt = BuildWorkflowHandler.BuildArchitectPrompt("x", "{}", "http://localhost:5210");
        Assert.Contains("customTypeKey \"Script\"", prompt);
        Assert.Contains("\"trigger\":\"before|after\"", prompt);
        Assert.Contains("customTypeKey \"EventRaiser\"", prompt);
    }

    [Fact]
    public void Script_output_sits_after_the_memory_preamble_in_the_prompt()
    {
        var agent = new LoopAgent { Prompt = "Do the thing." };
        var prompt = ClaudeCliExecutor.BuildPrompt(agent, [], [],
            memoryContext: ["MEMORY CONTEXT — facts"], scriptOutputs: "SCRIPT OUTPUT — tickets");

        Assert.StartsWith("Do the thing.", prompt);
        Assert.True(prompt.IndexOf("MEMORY CONTEXT", StringComparison.Ordinal) < prompt.IndexOf("SCRIPT OUTPUT", StringComparison.Ordinal));
    }
}

public class ScriptRunnerTests
{
    private static ScriptRunner Runner(int timeoutSeconds = 30) =>
        new(Options.Create(new LooperOptions { TestingActionTimeoutSeconds = timeoutSeconds }));

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"looper-script-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task Runs_python_with_the_environment_and_reports_the_exit_code()
    {
        var id = Guid.NewGuid();
        var cwd = TempDir();
        try
        {
            var config = ScriptResources.Parse("python", "import os, sys\nprint('run', os.environ['LOOPER_RUN_ID'])\nsys.exit(3)", null, null, null, null);
            var result = await Runner().RunAsync(id, "probe", config, cwd,
                new Dictionary<string, string> { ["LOOPER_RUN_ID"] = "run-123" }, CancellationToken.None);

            Assert.Equal(3, result.ExitCode);
            Assert.False(result.Passed);
            Assert.Contains("run run-123", result.Output);
            Assert.Contains("probe.py", result.Command);
        }
        finally
        {
            ScriptResources.RemoveMaterialized(id);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task A_hung_script_is_killed_at_its_timeout()
    {
        var id = Guid.NewGuid();
        var cwd = TempDir();
        try
        {
            var config = ScriptResources.Parse("python", "import time\ntime.sleep(30)", null, null, 1, null);
            var result = await Runner().RunAsync(id, "sleeper", config, cwd, null, CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("Timed out", result.Output);
            Assert.True(result.DurationMs < 10_000);
        }
        finally
        {
            ScriptResources.RemoveMaterialized(id);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public async Task A_stage_runs_only_its_scripts_and_sees_the_agent_credentials()
    {
        var cwd = TempDir();
        var before = new Resource
        {
            Name = "Fetch", Type = ResourceType.Custom, CustomTypeKey = "Script",
            ConfigJson = """{"language":"python","code":"import os\nprint('token=' + os.environ.get('TOKEN_X', 'missing'))","trigger":"before"}"""
        };
        var after = new Resource
        {
            Name = "Verify", Type = ResourceType.Custom, CustomTypeKey = "Script",
            ConfigJson = """{"language":"bash","code":"exit 2","trigger":"after"}"""
        };
        var credential = new Resource
        {
            Name = "Token", Type = ResourceType.PatToken,
            ConfigJson = """{"envVar":"TOKEN_X","value":"s3cret"}"""
        };
        var agent = new LoopAgent { Name = "Loop", WorkingDirectory = cwd };
        var logs = new List<string>();
        RunLogWriter log = (level, message) => { logs.Add($"{level}: {message}"); return Task.CompletedTask; };

        try
        {
            var beforeResults = await Runner().RunStageAsync(ScriptModule.TriggerBefore, agent, [before, after, credential], Guid.NewGuid(), log, CancellationToken.None);
            var only = Assert.Single(beforeResults);
            Assert.Equal("Fetch", only.Name);
            Assert.True(only.Passed);
            Assert.Contains("token=s3cret", only.Output);

            var afterResults = await Runner().RunStageAsync(ScriptModule.TriggerAfter, agent, [before, after, credential], Guid.NewGuid(), log, CancellationToken.None);
            var gate = Assert.Single(afterResults);
            Assert.Equal(2, gate.ExitCode);
            Assert.Contains(logs, l => l.StartsWith("error:") && l.Contains("'Verify' failed (exit 2)"));

            Assert.Empty(await Runner().RunStageAsync("never", agent, [before, after, credential], Guid.NewGuid(), log, CancellationToken.None));
        }
        finally
        {
            ScriptResources.RemoveMaterialized(before.Id);
            ScriptResources.RemoveMaterialized(after.Id);
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void Before_run_output_becomes_one_prompt_section_including_failures()
    {
        Assert.Null(ScriptRunner.BuildPromptSection([]));

        var section = ScriptRunner.BuildPromptSection(
        [
            new TestingActionResult("tickets", "python3 t.py", 0, true, 10, "TICKETS: 42"),
            new TestingActionResult("weather", "python3 w.py", 2, false, 10, "")
        ])!;

        Assert.StartsWith("SCRIPT OUTPUT", section);
        Assert.Contains("--- tickets ---\nTICKETS: 42", section);
        Assert.Contains("--- weather (FAILED, exit 2) ---\n(no output)", section);
    }
}

/// <summary>
/// The harness stages end to end, through the real coordinator and the real CLI executor,
/// with a fake `claude` that records its arguments and answers like the CLI would.
/// </summary>
public sealed class ScriptHarnessTests : IDisposable
{
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly string _dir;
    private readonly string _argsFile;
    private readonly string _fakeClaude;

    public ScriptHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"looper-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        // A file database: the run executes on background threads while the test polls, and a
        // single shared in-memory connection is not safe for that — separate connections are.
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "harness.db")}").Options;
        using (var db = new LooperDbContext(_options)) db.Database.EnsureCreated();

        _argsFile = Path.Combine(_dir, "claude-args.txt");
        _fakeClaude = Path.Combine(_dir, "claude");
        File.WriteAllText(_fakeClaude,
            $"#!/bin/bash\nprintf '%s\\n' \"$@\" > '{_argsFile}'\n" +
            "echo '{\"result\":\"done\",\"total_cost_usd\":0.01,\"is_error\":false,\"num_turns\":1,\"duration_ms\":5,\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}'\n");
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
        var looperOptions = Options.Create(new LooperOptions
        {
            MaxConcurrentRuns = 2, RunTimeoutMinutes = 5, ClaudeCommand = _fakeClaude, TestingActionTimeoutSeconds = 20
        });
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new ScriptModule());
        return new AgentRunCoordinator(
            new Factory(_options),
            new ClaudeCliExecutor(looperOptions, new ClaudeAuthProvider(new Factory(_options)), registry,
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
    }

    private async Task<(Guid AgentId, List<Guid> ResourceIds)> SeedAgent(params Resource[] scripts)
    {
        await using var db = new LooperDbContext(_options);
        var agent = new LoopAgent
        {
            Name = "Real loop", Prompt = "Triage the tickets.", Model = "claude-opus-5",
            DryRun = false, MaxTurns = 5, WorkingDirectory = _dir
        };
        agent.Resources.AddRange(scripts);
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        return (agent.Id, scripts.Select(s => s.Id).ToList());
    }

    private static Resource Script(string name, string trigger, string code) => new()
    {
        Name = name, Type = ResourceType.Custom, CustomTypeKey = "Script",
        ConfigJson = System.Text.Json.JsonSerializer.Serialize(new { language = "python", code, trigger })
    };

    private async Task<AgentRun> WaitForCompletion(Guid runId)
    {
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
    public async Task Before_run_output_reaches_the_prompt_and_after_run_scripts_join_the_gate()
    {
        var (agentId, ids) = await SeedAgent(
            Script("Tickets", "before", "print('TICKETS: 42 open')"),
            Script("Verify", "after", "print('all good')"));
        try
        {
            var runId = await CreateCoordinator().TriggerRunAsync(agentId, RunTrigger.Manual);
            var run = await WaitForCompletion(runId!.Value);

            Assert.Equal(RunStatus.Succeeded, run.Status);
            Assert.True(run.TestsPassed);
            Assert.Contains("\"name\":\"Tickets\"", run.TestResultsJson);
            Assert.Contains("\"name\":\"Verify\"", run.TestResultsJson);

            var args = await File.ReadAllTextAsync(_argsFile);
            Assert.Contains("SCRIPT OUTPUT", args);
            Assert.Contains("TICKETS: 42 open", args);
            Assert.Contains("SCRIPT 'Verify' (python)", args);            // the gate is announced to the model
            Assert.Contains("LOOPER_SCRIPT_VERIFY", args);
        }
        finally
        {
            foreach (var id in ids) ScriptResources.RemoveMaterialized(id);
        }
    }

    [Fact]
    public async Task A_failing_before_run_script_aborts_the_iteration_before_the_cli_starts()
    {
        var (agentId, ids) = await SeedAgent(Script("Gatekeeper", "before", "import sys\nprint('upstream down')\nsys.exit(1)"));
        try
        {
            var runId = await CreateCoordinator().TriggerRunAsync(agentId, RunTrigger.Manual);
            var run = await WaitForCompletion(runId!.Value);

            Assert.Equal(RunStatus.Failed, run.Status);
            Assert.Contains("not started", run.ErrorMessage);
            Assert.False(run.TestsPassed);
            Assert.Contains("upstream down", run.TestResultsJson);
            Assert.Equal(0m, run.CostUsd);
            Assert.False(File.Exists(_argsFile));   // no tokens were spent
        }
        finally
        {
            foreach (var id in ids) ScriptResources.RemoveMaterialized(id);
        }
    }

    [Fact]
    public async Task A_failing_after_run_script_fails_the_gate()
    {
        var (agentId, ids) = await SeedAgent(Script("Verify", "after", "import sys\nprint('3 checks failed')\nsys.exit(2)"));
        try
        {
            var runId = await CreateCoordinator().TriggerRunAsync(agentId, RunTrigger.Manual);
            var run = await WaitForCompletion(runId!.Value);

            Assert.Equal(RunStatus.Failed, run.Status);     // a failed gate is a failed run — no green with a footnote
            Assert.False(run.TestsPassed);
            Assert.Contains("Gate failed: 'Verify'", run.ErrorMessage);
            Assert.Contains("3 checks failed", run.TestResultsJson);
            Assert.True(File.Exists(_argsFile));
        }
        finally
        {
            foreach (var id in ids) ScriptResources.RemoveMaterialized(id);
        }
    }
}

public class ScriptsApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record IdResponse(Guid Id);
    private sealed record RunResponse(string Command, int ExitCode, bool Passed, long DurationMs, string Output);

    [Fact]
    public async Task Script_is_a_built_in_type_with_a_code_field()
    {
        var types = await _client.GetFromJsonAsync<List<Dictionary<string, System.Text.Json.JsonElement>>>("/api/resource-types", TestJson.Options);
        var script = Assert.Single(types!, t => t["typeKey"].GetString() == "Script");
        Assert.True(script["builtIn"].GetBoolean());
        var keys = script["fields"].EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.Contains("code", keys);
        Assert.Contains("language", keys);
        Assert.Contains("trigger", keys);
    }

    [Fact]
    public async Task Unsaved_code_runs_straight_from_the_editor()
    {
        var response = await _client.PostAsJsonAsync("/api/scripts/run", new
        {
            language = "python", code = "print('hi from looper')"
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<RunResponse>(TestJson.Options);
        Assert.True(result!.Passed);
        Assert.Contains("hi from looper", result.Output);
        Assert.Contains("scratch.py", result.Command);
    }

    [Fact]
    public async Task A_saved_script_lives_on_disk_runs_by_id_and_is_removed_with_the_resource()
    {
        var create = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "Ticket Count",
            type = "Custom",
            customTypeKey = "Script",
            description = "Counts tickets.",
            configJson = """{"language":"python","code":"import sys\nprint('tickets:', sys.argv[1:])","trigger":"before","args":"--all"}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var id = (await create.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;

        var path = Path.Combine(ScriptResources.ScriptDirectory(id), "ticket-count.py");
        Assert.True(File.Exists(path));

        var run = await _client.PostAsJsonAsync("/api/scripts/run", new { resourceId = id }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var result = await run.Content.ReadFromJsonAsync<RunResponse>(TestJson.Options);
        Assert.True(result!.Passed);
        Assert.Contains("tickets: ['--all']", result.Output);

        // Per-call overrides: different args, same saved code.
        var overridden = await _client.PostAsJsonAsync("/api/scripts/run", new { resourceId = id, args = "--mine" }, TestJson.Options);
        Assert.Contains("['--mine']", (await overridden.Content.ReadFromJsonAsync<RunResponse>(TestJson.Options))!.Output);

        // Editing the resource re-materializes the file.
        var update = await _client.PutAsJsonAsync($"/api/resources/{id}", new
        {
            name = "Ticket Count", description = "", configJson = """{"language":"python","code":"print('v2')","trigger":"before"}"""
        }, TestJson.Options);
        update.EnsureSuccessStatusCode();
        Assert.Equal("print('v2')\n", await File.ReadAllTextAsync(path));

        var delete = await _client.DeleteAsync($"/api/resources/{id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.False(Directory.Exists(ScriptResources.ScriptDirectory(id)));
    }

    [Fact]
    public async Task Bad_run_requests_are_rejected()
    {
        var empty = await _client.PostAsJsonAsync("/api/scripts/run", new { }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var rule = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "Not a script", type = "Rule", description = "", configJson = """{"text":"x"}"""
        }, TestJson.Options);
        var ruleId = (await rule.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
        try
        {
            var wrongType = await _client.PostAsJsonAsync("/api/scripts/run", new { resourceId = ruleId }, TestJson.Options);
            Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

            var missing = await _client.PostAsJsonAsync("/api/scripts/run", new { resourceId = Guid.NewGuid() }, TestJson.Options);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            await _client.DeleteAsync($"/api/resources/{ruleId}");
        }
    }
}

/// <summary>Boots the API with a fake `claude` that edits the script file in its cwd, like the real CLI would.</summary>
public sealed class FakeClaudeApiFactory : LooperApiFactory
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"looper-fake-claude-{Guid.NewGuid():N}");
    public string EditingClaude => Path.Combine(Dir, "claude-edit");
    public string IdleClaude => Path.Combine(Dir, "claude-idle");

    /// <summary>Which fake the API launches; switched per test before the request.</summary>
    public string ActiveClaude => Path.Combine(Dir, "claude");

    public FakeClaudeApiFactory()
    {
        Directory.CreateDirectory(Dir);
        WriteExecutable(EditingClaude,
            "#!/bin/bash\nf=$(ls *.py *.sh 2>/dev/null | head -1)\n" +
            "printf '%s\\n' \"$@\" > \"$(dirname \"$0\")/assist-args.txt\"\n" +
            "echo 'print(\"hello from claude\")' >> \"$f\"\n" +
            "echo '{\"result\":\"Added a greeting.\",\"total_cost_usd\":0.02,\"is_error\":false}'\n");
        WriteExecutable(IdleClaude, "#!/bin/bash\necho '{\"result\":\"I did nothing.\",\"total_cost_usd\":0.01,\"is_error\":false}'\n");
        Use(EditingClaude);
    }

    public void Use(string fake)
    {
        File.Copy(fake, ActiveClaude, overwrite: true);
        File.SetUnixFileMode(ActiveClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Looper:ClaudeCommand", ActiveClaude);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

public class ScriptAssistTests(FakeClaudeApiFactory factory) : IClassFixture<FakeClaudeApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record AssistResponse(string Code, string Summary, decimal CostUsd);

    [Fact]
    public async Task The_file_claude_edited_comes_back_as_the_proposal()
    {
        factory.Use(factory.EditingClaude);
        var response = await _client.PostAsJsonAsync("/api/scripts/assist", new
        {
            name = "Greeter", description = "Says hi.", language = "python", code = "print(1)\n",
            instruction = "Add a greeting line at the end.", allowRun = true
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<AssistResponse>(TestJson.Options);
        Assert.Equal("print(1)\nprint(\"hello from claude\")\n", result!.Code);
        Assert.Equal("Added a greeting.", result.Summary);
        Assert.Equal(0.02m, result.CostUsd);

        // The CLI was driven the way the agent runner drives it: edits auto-accepted, interpreter allowed.
        var args = await File.ReadAllTextAsync(Path.Combine(factory.Dir, "assist-args.txt"));
        Assert.Contains("acceptEdits", args);
        Assert.Contains("Bash(python3:*)", args);
        Assert.Contains("greeter.py", args);
        Assert.Contains("Add a greeting line at the end.", args);
        Assert.Contains("What the script is for: Says hi.", args);

        // The scratch workspace is gone.
        Assert.False(Directory.Exists(Path.Combine(ScriptResources.ScriptsRoot, "_assist")) &&
                     Directory.EnumerateDirectories(Path.Combine(ScriptResources.ScriptsRoot, "_assist")).Any());
    }

    [Fact]
    public async Task A_session_that_wrote_nothing_is_reported_not_accepted()
    {
        factory.Use(factory.IdleClaude);
        var response = await _client.PostAsJsonAsync("/api/scripts/assist", new
        {
            name = "Empty", language = "python", code = "", instruction = "Write a script that does X.", allowRun = false
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("without writing", body);
        Assert.Contains("I did nothing.", body);
    }

    [Fact]
    public async Task An_instruction_is_required()
    {
        var response = await _client.PostAsJsonAsync("/api/scripts/assist", new
        {
            name = "x", language = "python", code = "", instruction = "hi", allowRun = false
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
