using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

public class BuiltInGraphModulesTests
{
    private static ResourceModuleContext Config(string json) => new(json);

    [Fact]
    public void Vector_memory_contributes_env_dir_and_tool_protocol()
    {
        var contribution = new ContinuousVectorMemoryGraphModule()
            .Contribute(Config("""{"path":"/data/vectors","collection":"team"}"""));

        Assert.Equal("/data/vectors", contribution.EnvironmentVariables["LOOPER_VECTOR_MEMORY_PATH"]);
        Assert.Equal("team", contribution.EnvironmentVariables["LOOPER_VECTOR_MEMORY_COLLECTION"]);
        Assert.Contains("/data/vectors", contribution.AdditionalDirectories);
        var prompt = Assert.Single(contribution.PromptSections);
        Assert.Contains("loopergraph.py", prompt);   // the read path is a tool, not a folder
        Assert.Contains("recall", prompt);
        Assert.Contains("supersede", prompt);        // invalidate-never-delete protocol
        Assert.Empty(contribution.McpServers);
    }

    [Fact]
    public void Vector_memory_connects_the_mcp_server_when_a_url_is_given()
    {
        var contribution = new ContinuousVectorMemoryGraphModule()
            .Contribute(Config("""{"path":"/data/vectors","mcpUrl":"http://localhost:8123/mcp"}"""));

        var (name, spec) = Assert.Single(contribution.McpServers);
        Assert.Equal("vector-memory", name);
        Assert.Equal("http", spec.Transport);
        Assert.Equal("http://localhost:8123/mcp", spec.Url);
        Assert.Contains("MCP", contribution.PromptSections[0]);
    }

    [Fact]
    public void Knowledge_graph_flips_between_read_only_and_maintenance_instructions()
    {
        var module = new KnowledgeGraphModule();

        var writable = module.Contribute(Config("""{"path":"/kg"}"""));
        Assert.Contains("ontology.json", writable.PromptSections[0]);
        Assert.Contains("supersede", writable.PromptSections[0]);

        var readOnly = module.Contribute(Config("""{"path":"/kg","readOnly":true}"""));
        Assert.Contains("READ-ONLY", readOnly.PromptSections[0]);
        Assert.DoesNotContain("supersede", readOnly.PromptSections[0]);
    }

    [Fact]
    public void Memory_graph_mentions_the_episode_cap_only_when_set()
    {
        var module = new MemoryGraphModule();

        Assert.DoesNotContain("Soft cap",
            module.Contribute(Config("""{"path":"/episodes"}""")).PromptSections[0]);
        Assert.Contains("around 50 episodes",
            module.Contribute(Config("""{"path":"/episodes","maxEpisodes":50}""")).PromptSections[0]);
    }

    [Fact]
    public void Execution_graph_adds_the_strict_rule_only_when_enabled()
    {
        var module = new ExecutionGraphModule();

        var relaxed = module.Contribute(Config("""{"path":"/exec"}"""));
        Assert.Contains("exec-next", relaxed.PromptSections[0]);
        Assert.DoesNotContain("Strict ordering", relaxed.PromptSections[0]);

        Assert.Contains("Strict ordering",
            module.Contribute(Config("""{"path":"/exec","strictOrdering":true}""")).PromptSections[0]);
    }

    [Fact]
    public void All_graph_modules_degrade_to_an_empty_contribution_without_a_path()
    {
        IResourceTypeModule[] modules =
            [new ContinuousVectorMemoryGraphModule(), new KnowledgeGraphModule(), new MemoryGraphModule(), new ExecutionGraphModule()];

        foreach (var module in modules)
        {
            var contribution = module.Contribute(Config("{}"));
            Assert.Empty(contribution.EnvironmentVariables);
            Assert.Empty(contribution.PromptSections);
            Assert.Empty(contribution.AdditionalDirectories);
            module.PrepareRun(Config("{}")); // must be a no-op, not a crash
        }
    }

    [Fact]
    public void A_dll_cannot_shadow_a_built_in_type()
    {
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new KnowledgeGraphModule());
        Assert.True(registry.IsBuiltIn("knowledgegraph")); // case-insensitive

        var impostor = ResourceModuleCompilerTests.SampleModuleSource
            .Replace("\"SlackWebhook\"", "\"KnowledgeGraph\"");
        var compiled = new ResourceModuleCompiler().Compile(impostor, "Looper.Module.Impostor");
        Assert.True(compiled.Success, string.Join("\n", compiled.Errors));

        var dllPath = Path.Combine(registry.ModulesDirectory, $"impostor-{Guid.NewGuid():N}.dll");
        Directory.CreateDirectory(registry.ModulesDirectory);
        File.WriteAllBytes(dllPath, compiled.Assembly!);
        try
        {
            Assert.Throws<InvalidOperationException>(() => registry.LoadFromFile(dllPath));
            Assert.True(registry.TryGet("KnowledgeGraph", out var survivor));
            Assert.IsType<KnowledgeGraphModule>(survivor);
        }
        finally
        {
            try { File.Delete(dllPath); } catch (IOException) { }
        }
    }
}

public sealed class GraphWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"looper-graph-ws-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ResourceModuleContext ConfigFor(string sub) =>
        new($$"""{"path":"{{Path.Combine(_root, sub).Replace("\\", "\\\\")}}"}""");

    [Fact]
    public void Knowledge_prepare_seeds_tool_ontology_and_readme()
    {
        new KnowledgeGraphModule().PrepareRun(ConfigFor("kg"));
        var dir = Path.Combine(_root, "kg");

        Assert.True(File.Exists(Path.Combine(dir, "loopergraph.py")));
        Assert.Contains("loopergraph v1", File.ReadAllText(Path.Combine(dir, "loopergraph.py")));
        var ontology = File.ReadAllText(Path.Combine(dir, "ontology.json"));
        Assert.Contains("depends_on", ontology);
        Assert.Contains("owned_by", ontology);
        Assert.Contains("Invalidate, never delete", File.ReadAllText(Path.Combine(dir, "README.md")));
    }

    [Fact]
    public void Execution_prepare_seeds_tool_readme_and_empty_graph()
    {
        new ExecutionGraphModule().PrepareRun(ConfigFor("exec"));
        var dir = Path.Combine(_root, "exec");

        Assert.True(File.Exists(Path.Combine(dir, "loopergraph.py")));
        Assert.False(File.Exists(Path.Combine(dir, "ontology.json"))); // execution graphs have no edge ontology
        Assert.Contains("\"nodes\": []", File.ReadAllText(Path.Combine(dir, "execution.json")));
        Assert.Contains("exec-next", File.ReadAllText(Path.Combine(dir, "README.md")));
    }

    [Fact]
    public void Prepare_is_idempotent_but_upgrades_an_outdated_tool()
    {
        var module = new MemoryGraphModule();
        var context = ConfigFor("mem");
        module.PrepareRun(context);
        var dir = Path.Combine(_root, "mem");

        // User/agent-owned files survive re-preparation.
        File.WriteAllText(Path.Combine(dir, "README.md"), "customized");
        File.WriteAllText(Path.Combine(dir, "ontology.json"), """{"edge_types":{"my_own":"kept"}}""");
        module.PrepareRun(context);
        Assert.Equal("customized", File.ReadAllText(Path.Combine(dir, "README.md")));
        Assert.Contains("my_own", File.ReadAllText(Path.Combine(dir, "ontology.json")));

        // An outdated toolkit is replaced by the shipped version.
        File.WriteAllText(Path.Combine(dir, "loopergraph.py"), "#!/usr/bin/env python3\n# loopergraph v0\n");
        module.PrepareRun(context);
        Assert.Contains("loopergraph v1", File.ReadAllText(Path.Combine(dir, "loopergraph.py")));
    }
}

/// <summary>Drives the seeded toolkit exactly as an agent would — the lessons' acceptance drills.</summary>
public sealed class LoopergraphToolTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-graphtool-{Guid.NewGuid():N}");

    public LoopergraphToolTests()
    {
        new KnowledgeGraphModule().PrepareRun(
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
    public void Ontology_gate_rejects_undeclared_edge_types()
    {
        if (!PythonAvailable) return;

        var ok = Run("add", "depends_on", "payments-api", "auth-lib");
        Assert.Equal(0, ok.ExitCode);

        var rejected = Run("add", "is_associated_with", "a", "b");
        Assert.Equal(1, rejected.ExitCode);
        Assert.Contains("not in the ontology", rejected.Output);
    }

    [Fact]
    public void Supersede_preserves_history_and_as_of_queries_stay_correct()
    {
        if (!PythonAvailable) return;

        Assert.Equal(0, Run("add", "owned_by", "payments-api", "team-a", "--from", "2026-01-01").ExitCode);
        Assert.Equal(0, Run("supersede", "owned_by", "payments-api", "team-b").ExitCode);

        Assert.Contains("team-b", Run("neighbors", "payments-api").Output);           // now
        Assert.Contains("team-a", Run("neighbors", "payments-api", "--at", "2026-02-01").Output); // as-of
        var history = Run("history", "payments-api").Output;
        Assert.Contains("team-a", history);                                            // never deleted
        Assert.Contains("team-b", history);

        // The vector path honours validity too — the naive-design killer from Lesson 3.
        Assert.Contains("team-b", Run("recall", "who owns payments", "-k", "3").Output);
        Assert.Contains("team-a", Run("recall", "who owns payments", "--at", "2026-02-01", "-k", "3").Output);
    }

    [Fact]
    public void Execution_graph_enforces_dependencies_and_survives_as_a_checkpoint()
    {
        if (!PythonAvailable) return;

        Assert.Equal(0, Run("exec-add", "design", "Design the schema").ExitCode);
        Assert.Equal(0, Run("exec-add", "build", "Build it", "--after", "design").ExitCode);
        Assert.Equal(1, Run("exec-add", "ship", "Ship", "--after", "nope").ExitCode);     // unknown dep rejected
        Assert.Equal(1, Run("exec-start", "build").ExitCode);                              // deps not done

        Assert.Equal(0, Run("exec-start", "design").ExitCode);
        Assert.Equal(0, Run("exec-done", "design", "--note", "schema written").ExitCode);
        Assert.Contains("ready: build", Run("exec-next").Output);
        Assert.Contains("schema written", Run("exec-status").Output);                      // notes persist on disk
        Assert.Equal(0, Run("exec-validate").ExitCode);
    }
}
