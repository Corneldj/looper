using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

public sealed record TestingActionResult(
    string Name, string Command, int ExitCode, bool Passed, long DurationMs, string Output);

/// <summary>
/// Runs Check (TestingAction) resources after a loop iteration completes, e.g. a test suite or lint
/// gate. A check either runs a plain shell command or one of the workflow's Script resources —
/// the same script, materialized and run the same way, whether a stage or a check asks for it.
/// </summary>
public sealed class TestingActionRunner(IOptions<LooperOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<TestingActionResult> results) =>
        JsonSerializer.Serialize(results, JsonOptions);

    public static IReadOnlyList<TestingActionResult> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<TestingActionResult>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task<(string ResultsJson, bool AllPassed)?> RunAllAsync(
        LoopAgent agent, IReadOnlyList<Resource> resources, RunLogWriter log, CancellationToken cancellationToken)
    {
        var results = await RunAsync(agent, resources, log, cancellationToken);
        if (results.Count == 0) return null;
        return (Serialize(results), results.All(r => r.Passed));
    }

    /// <summary>
    /// Runs every configured check in resource order and returns the raw results. Script-backed
    /// checks find their script in <paramref name="scriptCatalog"/> (the workflow's scripts; the
    /// attached resources when null) and get the same environment an after-run script gets.
    /// </summary>
    public async Task<IReadOnlyList<TestingActionResult>> RunAsync(
        LoopAgent agent, IReadOnlyList<Resource> resources, RunLogWriter log, CancellationToken cancellationToken,
        IReadOnlyList<Resource>? scriptCatalog = null, Guid? runId = null)
    {
        var actions = resources.Where(r => r.Type == ResourceType.TestingAction).ToList();
        if (actions.Count == 0) return [];

        var (defaultWorkingDirectory, _) = AgentWorkspace.Resolve(agent, resources);
        var results = new List<TestingActionResult>();

        foreach (var action in actions)
        {
            var config = ResourceConfig.Parse<TestingActionConfig>(action);
            if (config.ScriptResourceId is { } scriptId)
            {
                results.Add(await RunScriptCheckAsync(action, config, scriptId, scriptCatalog ?? resources, agent, resources,
                    defaultWorkingDirectory, runId, log, cancellationToken));
                continue;
            }
            if (string.IsNullOrWhiteSpace(config.Command))
            {
                // A gate with nothing to run cannot pass anything: fail closed instead of skipping.
                await log("error", $"Testing action '{action.Name}' has no command configured — failing closed.");
                results.Add(new TestingActionResult(action.Name, "", -1, false, 0, "No command configured — a gate that cannot run fails closed."));
                continue;
            }

            await log("info", $"Running testing action '{action.Name}': {config.Command}");
            var timeout = TimeSpan.FromSeconds(config.TimeoutSeconds ?? options.Value.TestingActionTimeoutSeconds);
            var result = await ShellCommandRunner.RunAsync(
                action.Name, config.Command,
                string.IsNullOrWhiteSpace(config.WorkingDirectory) ? defaultWorkingDirectory : config.WorkingDirectory,
                timeout, environment: null, cancellationToken);
            results.Add(result);
            await log(result.Passed ? "info" : "error",
                $"Testing action '{action.Name}' {(result.Passed ? "passed" : $"failed (exit {result.ExitCode})")} in {result.DurationMs}ms");
        }

        return results;
    }

    /// <summary>A check that runs a Script resource. A script that is gone, not a script, or empty fails the check closed.</summary>
    private async Task<TestingActionResult> RunScriptCheckAsync(Resource action, TestingActionConfig config, Guid scriptId,
        IReadOnlyList<Resource> scriptCatalog, LoopAgent agent, IReadOnlyList<Resource> resources, string defaultWorkingDirectory,
        Guid? runId, RunLogWriter log, CancellationToken cancellationToken)
    {
        var script = scriptCatalog.FirstOrDefault(r => r.Id == scriptId);
        if (script is null || !ScriptResources.IsScript(script))
        {
            await log("error", $"Check '{action.Name}' points at a script that no longer exists ({scriptId}) — failing closed.");
            return new TestingActionResult(action.Name, "", -1, false, 0,
                "The script this check runs no longer exists — pick another script or a command. A gate that cannot run fails closed.");
        }
        var scriptConfig = ScriptResources.Parse(new Modules.ResourceModuleContext(script.ConfigJson));
        if (scriptConfig.Code.Length == 0)
        {
            await log("error", $"Check '{action.Name}' runs script '{script.Name}', which has no code — failing closed.");
            return new TestingActionResult(action.Name, "", -1, false, 0, $"Script '{script.Name}' has no code — a gate that cannot run fails closed.");
        }

        // The check's own settings win over the script's; the script's win over the agent's defaults.
        var effective = scriptConfig with
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? scriptConfig.WorkingDirectory : config.WorkingDirectory,
            TimeoutSeconds = config.TimeoutSeconds ?? scriptConfig.TimeoutSeconds
        };
        var environment = AgentWorkspace.ResolveEnvironment(resources);
        environment["LOOPER_API_URL"] = options.Value.PublicUrl.TrimEnd('/');
        environment["LOOPER_AGENT_ID"] = agent.Id.ToString();
        if (runId is { } id) environment["LOOPER_RUN_ID"] = id.ToString();

        await log("info", $"Running check '{action.Name}' as script '{script.Name}'.");
        var result = await ScriptRunner.RunScriptAsync(options.Value, script.Id, script.Name, effective, defaultWorkingDirectory,
            environment, cancellationToken);
        // Reported under the check's name: it is the check that passed or failed, by way of the script.
        result = result with { Name = action.Name };
        await log(result.Passed ? "info" : "error",
            $"Check '{action.Name}' (script '{script.Name}') {(result.Passed ? "passed" : $"failed (exit {result.ExitCode})")} in {result.DurationMs}ms");
        return result;
    }
}
