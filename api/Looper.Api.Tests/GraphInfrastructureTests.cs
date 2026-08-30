using System.Diagnostics;
using System.Text.Json;
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
// The decoupled memory layer: role-split protocols, the contribution inbox,
// harness-side preambles, and the maintenance janitor that wakes curators.
// ============================================================================

public class GraphRoleTests
{
    private static ResourceModuleContext For(string json, string agentName) =>
        new(json, Guid.NewGuid(), agentName);

    [Fact]
    public void Without_a_curator_the_graph_is_private_and_keeps_the_full_protocol()
    {
        var section = new ContinuousVectorMemoryGraphModule()
            .Contribute(For("""{"path":"/m"}""", "Solo Loop")).PromptSections[0];

        Assert.Contains("Your durable semantic memory", section);
        Assert.Contains("supersede", section);
        Assert.DoesNotContain("CURATOR", section);
    }

    [Fact]
    public void A_consumer_gets_query_plus_inbox_and_a_write_prohibition()
    {
        var section = new ContinuousVectorMemoryGraphModule()
            .Contribute(For("""{"path":"/m","curator":"Memory Curator"}""", "Coding Loop")).PromptSections[0];

        Assert.Contains("SHARED", section);
        Assert.Contains("Memory Curator", section);       // who owns it
        Assert.Contains("remember", section);              // the contribution path
        Assert.Contains("Do NOT add, supersede", section); // canonical writes forbidden
        Assert.DoesNotContain("inbox-merge", section);     // curation is not its job
    }

    [Fact]
    public void The_curator_gets_the_maintenance_protocol()
    {
        var section = new MemoryGraphModule()
            .Contribute(For("""{"path":"/m","curator":"Memory Curator"}""", "memory curator")) // name match is case-insensitive
            .PromptSections[0];

        Assert.Contains("YOU ARE THE CURATOR", section);
        Assert.Contains("inbox-merge", section);
        Assert.Contains("decay", section);
        Assert.Contains("health", section);
    }

    [Fact]
    public void A_read_only_knowledge_consumer_may_not_even_contribute()
    {
        var section = new KnowledgeGraphModule()
            .Contribute(For("""{"path":"/kg","curator":"Curator","readOnly":true}""", "Worker")).PromptSections[0];

        Assert.Contains("READ-ONLY for you", section);
        Assert.DoesNotContain("remember \"<text>\"", section);
    }

    [Fact]
    public void Shared_config_defaults_are_production_sane()
    {
        var config = GraphInfrastructure.SharedConfig(new ResourceModuleContext("{}"));

        Assert.False(config.HasCurator);
        Assert.Equal(6, config.EffectivePreambleK);
        Assert.Equal(5, config.EffectiveInboxThreshold);
        Assert.False(config.AutoLog);
    }

    [Fact]
    public void Curation_topics_slug_the_resource_name()
    {
        Assert.Equal("graph.team-memory.needs-curation", GraphInfrastructure.CurationTopic("Team Memory!"));
        Assert.True(EventDispatcher.IsValidTopic(GraphInfrastructure.CurationTopic("Ünusual  name")));
    }

    [Fact]
    public void Memory_preamble_query_carries_the_runs_intent()
    {
        var agent = new LoopAgent { Name = "Docs bot", Description = "keeps docs fresh", Prompt = new string('p', 400) };
        var query = GraphContextService.BuildQuery(agent, "prd.approved");

        Assert.StartsWith("Docs bot keeps docs fresh", query);
        Assert.Contains("prd.approved", query);
        Assert.True(query.Length < 600); // bounded — a preamble query, not a payload
    }
}

/// <summary>Drives the v2 toolkit + the C#/python inbox interop the way production does.</summary>
public sealed class GraphInboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-inbox-{Guid.NewGuid():N}");

    public GraphInboxTests()
    {
        new MemoryGraphModule().PrepareRun(
            new ResourceModuleContext($$"""{"path":"{{_dir.Replace("\\", "\\\\")}}"}"""));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool PythonAvailable => File.Exists("/usr/bin/python3");

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/python3",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _dir
        };
        startInfo.ArgumentList.Add(Path.Combine(_dir, "loopergraph.py"));
        foreach (var a in args) startInfo.ArgumentList.Add(a);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);
        return (process.ExitCode, output);
    }

    [Fact]
    public void Contributions_flow_remember_to_inbox_to_merge_and_archive_survives()
    {
        if (!PythonAvailable) return;

        Assert.Equal(0, Run("remember", "Pin redis to 7.x", "--kind", "lesson", "--agent", "worker").ExitCode);
        var harnessId = GraphInfrastructure.WriteInboxItem(_dir, "episode", "Run succeeded.", "worker", "run-1");

        Assert.Equal(2, GraphInfrastructure.PendingInboxCount(_dir));
        var listed = Run("inbox", "--json");
        Assert.Equal(0, listed.ExitCode);
        Assert.Contains("Pin redis", listed.Output);
        Assert.Contains(harnessId, listed.Output);       // the C# writer speaks the same format

        Assert.Equal(0, Run("inbox-merge", harnessId).ExitCode);
        Assert.Equal(1, GraphInfrastructure.PendingInboxCount(_dir));
        Assert.True(File.Exists(Path.Combine(_dir, "inbox", "archived", $"{harnessId}.json"))); // never deleted

        Assert.Equal(1, Run("inbox-merge", harnessId).ExitCode); // already archived — refused
    }

    [Fact]
    public void Health_reports_pending_inbox_competing_facts_and_stays_machine_readable()
    {
        if (!PythonAvailable) return;

        Assert.Equal(0, Run("add", "did", "iteration-1", "shipped-feature").ExitCode);
        Assert.Equal(0, Run("add", "did", "iteration-1", "broke-build").ExitCode);
        Run("remember", "note one"); Run("remember", "note two");

        var health = Run("health", "--json");
        Assert.Equal(0, health.ExitCode);
        using var document = JsonDocument.Parse(health.Output);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("inbox_pending").GetInt32());
        Assert.Equal(1, root.GetProperty("competing_count").GetInt32());
        Assert.Equal(0, root.GetProperty("problem_count").GetInt32());
        Assert.Equal(2, root.GetProperty("facts_current").GetInt32());
    }

    [Fact]
    public void Recall_logs_usage_and_decay_downweights_what_nobody_recalls()
    {
        if (!PythonAvailable) return;

        Assert.Equal(0, Run("add", "learned", "iteration-1", "always-pin-versions").ExitCode);
        Assert.Equal(0, Run("recall", "pin versions", "-k", "2").ExitCode);
        Assert.True(File.Exists(Path.Combine(_dir, "usage.jsonl"))); // telemetry appended

        // A future horizon makes everything unused: decay applies and respects the floor.
        Assert.Contains("decayed 1", Run("decay", "--days", "-1", "--factor", "0.1", "--floor", "0.25").Output);
        Assert.Contains("conf 0.25", Run("neighbors", "iteration-1").Output);
    }
}

public sealed class GraphContextServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-preamble-{Guid.NewGuid():N}");
    private static bool PythonAvailable => File.Exists("/usr/bin/python3");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task The_harness_retrieves_memory_before_the_run_and_labels_it_honestly()
    {
        if (!PythonAvailable) return;

        var configJson = $$"""{"path":"{{_dir.Replace("\\", "\\\\")}}","curator":"Curator"}""";
        new KnowledgeGraphModule().PrepareRun(new ResourceModuleContext(configJson));
        SeedFact("owned_by", "payments-api", "team-a");

        var service = new GraphContextService(
            Options.Create(new LooperOptions()), NullLogger<GraphContextService>.Instance);
        var agent = new LoopAgent { Name = "Worker", Description = "", Prompt = "Fix the payments api ownership docs" };
        var resources = new List<Resource>
        {
            new() { Name = "Team Memory", Type = ResourceType.Custom, CustomTypeKey = "KnowledgeGraph", ConfigJson = configJson }
        };

        var sections = await service.BuildPreambleSectionsAsync(agent, resources, null, (_, _) => Task.CompletedTask, default);

        var section = Assert.Single(sections);
        Assert.Contains("MEMORY CONTEXT from 'Team Memory'", section);
        Assert.Contains("payments-api", section);
        Assert.Contains("verify anything", section); // recall, not ground truth
    }

    [Fact]
    public async Task A_zero_preamble_setting_disables_retrieval()
    {
        if (!PythonAvailable) return;

        var configJson = $$"""{"path":"{{_dir.Replace("\\", "\\\\")}}","preambleK":0}""";
        new KnowledgeGraphModule().PrepareRun(new ResourceModuleContext(configJson));
        SeedFact("owned_by", "payments-api", "team-a");

        var service = new GraphContextService(
            Options.Create(new LooperOptions()), NullLogger<GraphContextService>.Instance);
        var agent = new LoopAgent { Name = "Worker", Prompt = "payments api" };
        var resources = new List<Resource>
        {
            new() { Name = "Team Memory", Type = ResourceType.Custom, CustomTypeKey = "KnowledgeGraph", ConfigJson = configJson }
        };

        Assert.Empty(await service.BuildPreambleSectionsAsync(agent, resources, null, (_, _) => Task.CompletedTask, default));
    }

    private void SeedFact(string etype, string src, string dst)
    {
        var startInfo = new ProcessStartInfo { FileName = "/usr/bin/python3", WorkingDirectory = _dir };
        startInfo.ArgumentList.Add(Path.Combine(_dir, "loopergraph.py"));
        foreach (var a in new[] { "add", etype, src, dst }) startInfo.ArgumentList.Add(a);
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(15_000);
        Assert.Equal(0, process.ExitCode);
    }
}

public sealed class GraphMaintenanceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-maint-{Guid.NewGuid():N}");

    public GraphMaintenanceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static bool PythonAvailable => File.Exists("/usr/bin/python3");

    private sealed class Factory(DbContextOptions<LooperDbContext> options) : IDbContextFactory<LooperDbContext>
    {
        public LooperDbContext CreateDbContext() => new(options);
    }

    private GraphMaintenanceService BuildService()
    {
        var looperOptions = Options.Create(new LooperOptions { MaxConcurrentRuns = 1, RunTimeoutMinutes = 1 });
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new MemoryGraphModule());
        var dispatcher = new EventDispatcher(NullLogger<EventDispatcher>.Instance);
        var coordinator = new AgentRunCoordinator(
            new Factory(_options),
            new ClaudeCliExecutor(looperOptions, registry,
                new GraphContextService(looperOptions, NullLogger<GraphContextService>.Instance),
                NullLogger<ClaudeCliExecutor>.Instance),
            new SimulatedAgentExecutor(),
            new TestingActionRunner(looperOptions),
            new ReviewRunner(looperOptions, NullLogger<ReviewRunner>.Instance),
            dispatcher,
            looperOptions,
            NullLogger<AgentRunCoordinator>.Instance);
        return new GraphMaintenanceService(new Factory(_options), registry, dispatcher, coordinator,
            looperOptions, NullLogger<GraphMaintenanceService>.Instance);
    }

    [Fact]
    public async Task A_backed_up_inbox_raises_needs_curation_once_until_it_changes()
    {
        if (!PythonAvailable) return;

        await using (var db = new LooperDbContext(_options))
        {
            db.Resources.Add(new Resource
            {
                Name = "Team Memory",
                Type = ResourceType.Custom,
                CustomTypeKey = "MemoryGraph",
                ConfigJson = $$"""{"path":"{{_dir.Replace("\\", "\\\\")}}","curator":"Curator","inboxThreshold":2}"""
            });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(_dir);
        GraphInfrastructure.WriteInboxItem(_dir, "note", "one", "a", null);
        GraphInfrastructure.WriteInboxItem(_dir, "note", "two", "a", null);

        var service = BuildService();
        Assert.Equal(1, await service.RunPassAsync(default));

        // The snapshot landed and carries the measurement.
        var snapshot = GraphMaintenanceService.ReadSnapshot(Path.Combine(_dir, GraphInfrastructure.HealthFileName));
        Assert.NotNull(snapshot);
        Assert.Equal("2|0|0", snapshot!.Signature);

        await using (var db = new LooperDbContext(_options))
        {
            var evt = Assert.Single(await db.Events.ToListAsync());
            Assert.Equal("graph.team-memory.needs-curation", evt.Topic);
            Assert.Equal(EventSource.Harness, evt.Source);
            Assert.Contains("2 inbox item(s)", evt.Payload);
        }

        // Unchanged backlog: no re-raise. A third item changes the signature: raise again.
        Assert.Equal(0, await service.RunPassAsync(default));
        GraphInfrastructure.WriteInboxItem(_dir, "note", "three", "a", null);
        Assert.Equal(1, await service.RunPassAsync(default));
    }

    [Fact]
    public async Task A_healthy_graph_below_threshold_stays_quiet()
    {
        if (!PythonAvailable) return;

        await using (var db = new LooperDbContext(_options))
        {
            db.Resources.Add(new Resource
            {
                Name = "Quiet Memory",
                Type = ResourceType.Custom,
                CustomTypeKey = "MemoryGraph",
                ConfigJson = $$"""{"path":"{{_dir.Replace("\\", "\\\\")}}"}"""
            });
            await db.SaveChangesAsync();
        }

        var service = BuildService();
        Assert.Equal(0, await service.RunPassAsync(default));
        await using (var check = new LooperDbContext(_options))
        {
            Assert.Empty(await check.Events.ToListAsync());
        }
        // But health was still measured and persisted.
        Assert.NotNull(GraphMaintenanceService.ReadSnapshot(Path.Combine(_dir, GraphInfrastructure.HealthFileName)));
    }
}
