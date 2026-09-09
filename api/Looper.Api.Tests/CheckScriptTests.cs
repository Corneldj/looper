using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Features.Workflows;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Looper.Api.Tests;

// ============================================================================
// A Check can run one of the workflow's Script resources instead of a plain
// command — the same script, run the same way, judged by its exit code.
// ============================================================================

public sealed class CheckRunsScriptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-check-{Guid.NewGuid():N}");
    private readonly List<string> _log = [];

    public CheckRunsScriptTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private Task Log(string level, string message) { _log.Add($"{level}: {message}"); return Task.CompletedTask; }

    private static Resource Script(string name, string code, string? workingDirectory = null) => new()
    {
        Name = name, Type = ResourceType.Custom, CustomTypeKey = "Script",
        ConfigJson = JsonSerializer.Serialize(new { language = "python", code, trigger = "after", workingDirectory })
    };

    private static Resource Check(string name, Guid scriptId, string? workingDirectory = null, int? timeoutSeconds = null) => new()
    {
        Name = name, Type = ResourceType.TestingAction,
        ConfigJson = JsonSerializer.Serialize(new { scriptResourceId = scriptId, workingDirectory, timeoutSeconds })
    };

    private TestingActionRunner Runner() => new(Options.Create(new LooperOptions { TestingActionTimeoutSeconds = 20 }));

    [Fact]
    public async Task A_check_runs_the_workflow_script_it_points_at_and_reports_under_its_own_name()
    {
        var passing = Script("Verify report", "import os\nprint('cwd', os.getcwd())\nprint('LOOPER_AGENT_ID' in os.environ)");
        var failing = Script("Broken verify", "import sys\nsys.exit(3)");
        var agent = new LoopAgent { Name = "Loop", Prompt = "p", WorkingDirectory = _dir };
        var attached = new List<Resource> { Check("Report check", passing.Id), Check("Strict check", failing.Id, workingDirectory: _dir) };
        try
        {
            // The scripts are NOT attached to the agent — a check may run any script of the workflow.
            var results = await Runner().RunAsync(agent, attached, Log, CancellationToken.None, scriptCatalog: [passing, failing], runId: Guid.NewGuid());

            Assert.Equal(2, results.Count);
            var ok = results[0];
            Assert.Equal("Report check", ok.Name);
            Assert.True(ok.Passed);
            Assert.Contains($"cwd {_dir}", ok.Output);       // the agent's working directory, since neither check nor script set one
            Assert.Contains("True", ok.Output);              // the environment an after-run script gets
            Assert.Contains("verify-report.py", ok.Command); // the materialized script, not a copy

            var bad = results[1];
            Assert.Equal("Strict check", bad.Name);
            Assert.False(bad.Passed);
            Assert.Equal(3, bad.ExitCode);
            Assert.Contains(_log, l => l.Contains("Check 'Strict check' (script 'Broken verify') failed (exit 3)"));
        }
        finally
        {
            Modules.BuiltIn.ScriptResources.RemoveMaterialized(passing.Id);
            Modules.BuiltIn.ScriptResources.RemoveMaterialized(failing.Id);
        }
    }

    [Fact]
    public async Task A_check_whose_script_is_gone_or_empty_fails_closed()
    {
        var empty = Script("Nothing", "");
        var agent = new LoopAgent { Name = "Loop", Prompt = "p", WorkingDirectory = _dir };
        var attached = new List<Resource> { Check("Gone check", Guid.NewGuid()), Check("Empty check", empty.Id) };

        var results = await Runner().RunAsync(agent, attached, Log, CancellationToken.None, scriptCatalog: [empty]);

        Assert.All(results, r => Assert.False(r.Passed));
        Assert.Contains("no longer exists", results[0].Output);
        Assert.Contains("has no code", results[1].Output);
        Assert.Contains(_log, l => l.StartsWith("error:") && l.Contains("Gone check"));
    }
}

public class CheckScriptPackagingTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> PostId(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body, TestJson.Options);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestJson.Options)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_script_reference_travels_as_a_ref_and_comes_back_as_the_new_id()
    {
        var workflowId = await PostId("/api/workflows", new { name = "Checked loop", description = "" });
        var script = await PostId("/api/resources", new { workflowId, name = "Verify", type = "Custom", customTypeKey = "Script", description = "", configJson = """{"language":"python","code":"print(1)","trigger":"after"}""" });
        var check = await PostId("/api/resources", new { workflowId, name = "Verify check", type = "TestingAction", description = "", configJson = $$"""{"scriptResourceId":"{{script}}","timeoutSeconds":30}""" });

        var package = await _client.GetFromJsonAsync<WorkflowPackage>($"/api/workflows/{workflowId}/export?format=json", TestJson.Options);
        var packagedCheck = package!.Resources.Single(r => r.Name == "Verify check");
        var packagedScript = package.Resources.Single(r => r.Name == "Verify");
        using (var config = JsonDocument.Parse(packagedCheck.ConfigJson))
        {
            Assert.Equal(packagedScript.Ref, config.RootElement.GetProperty("scriptResourceRef").GetString());
            Assert.False(config.RootElement.TryGetProperty("scriptResourceId", out _));
            Assert.Equal(30, config.RootElement.GetProperty("timeoutSeconds").GetInt32());
        }

        var import = await _client.PostAsJsonAsync("/api/workflows/import", new { package, name = "Checked copy" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, import.StatusCode);
        var result = await import.Content.ReadFromJsonAsync<WorkflowImportResultDto>(TestJson.Options);
        var resources = await _client.GetFromJsonAsync<List<JsonElement>>($"/api/resources?workflowId={result!.Workflow.Id}", TestJson.Options);
        var newScript = resources!.Single(r => r.GetProperty("name").GetString() == "Verify").GetProperty("id").GetGuid();
        var newCheck = JsonDocument.Parse(resources.Single(r => r.GetProperty("name").GetString() == "Verify check").GetProperty("configJson").GetString()!);
        Assert.Equal(newScript, newCheck.RootElement.GetProperty("scriptResourceId").GetGuid());
        Assert.NotEqual(script, newScript);
        Assert.False(newCheck.RootElement.TryGetProperty("scriptResourceRef", out _));

        // A package whose check points at a script that is not in it is refused.
        var dangling = package with { Resources = package.Resources.Where(r => r.Ref != packagedScript.Ref).ToList(), Agents = [] };
        var refused = await _client.PostAsJsonAsync("/api/workflows/import", new { package = dangling }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("script ref that is not in the package", await refused.Content.ReadAsStringAsync());

        await _client.DeleteAsync($"/api/resources/{check}");
    }
}
