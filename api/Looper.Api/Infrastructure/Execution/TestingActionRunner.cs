using System.Text.Json;
using Looper.Api.Domain;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

public sealed record TestingActionResult(
    string Name, string Command, int ExitCode, bool Passed, long DurationMs, string Output);

/// <summary>Runs TestingAction resources after a loop iteration completes, e.g. a test suite or lint gate.</summary>
public sealed class TestingActionRunner(IOptions<LooperOptions> options)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(IEnumerable<TestingActionResult> results) =>
        JsonSerializer.Serialize(results, JsonOptions);

    public async Task<(string ResultsJson, bool AllPassed)?> RunAllAsync(
        LoopAgent agent, IReadOnlyList<Resource> resources, RunLogWriter log, CancellationToken cancellationToken)
    {
        var results = await RunAsync(agent, resources, log, cancellationToken);
        if (results.Count == 0) return null;
        return (Serialize(results), results.All(r => r.Passed));
    }

    /// <summary>Runs every configured testing action in resource order and returns the raw results.</summary>
    public async Task<IReadOnlyList<TestingActionResult>> RunAsync(
        LoopAgent agent, IReadOnlyList<Resource> resources, RunLogWriter log, CancellationToken cancellationToken)
    {
        var actions = resources.Where(r => r.Type == ResourceType.TestingAction).ToList();
        if (actions.Count == 0) return [];

        var (defaultWorkingDirectory, _) = AgentWorkspace.Resolve(agent, resources);
        var results = new List<TestingActionResult>();

        foreach (var action in actions)
        {
            var config = ResourceConfig.Parse<TestingActionConfig>(action);
            if (string.IsNullOrWhiteSpace(config.Command))
            {
                await log("warn", $"Testing action '{action.Name}' has no command configured; skipped.");
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
}
