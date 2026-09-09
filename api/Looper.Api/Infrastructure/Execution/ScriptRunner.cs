using System.Text;
using Looper.Api.Domain;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// Executes Script resources: the harness stages (before an iteration → output into the prompt;
/// after an iteration → gate) and manual runs from the UI. Scripts run with the agent's
/// credential environment plus the LOOPER_* variables, so a script can do anything the agent's
/// curl protocol can — deterministically, for free.
/// </summary>
public sealed class ScriptRunner(IOptions<LooperOptions> options)
{
    /// <summary>Output kept for before-run scripts — it becomes prompt context, so it's allowed to be longer.</summary>
    public const int BeforeOutputCap = 6000;

    public string PublicUrl => options.Value.PublicUrl.TrimEnd('/');

    /// <summary>Runs every attached script of one trigger stage, in resource order.</summary>
    public async Task<IReadOnlyList<TestingActionResult>> RunStageAsync(
        string trigger, LoopAgent agent, IReadOnlyList<Resource> resources, Guid runId,
        RunLogWriter log, CancellationToken cancellationToken)
    {
        var scripts = ScriptResources.Scripts(resources, trigger, includeEmpty: true);
        if (scripts.Count == 0) return [];

        var (defaultWorkingDirectory, _) = AgentWorkspace.Resolve(agent, resources);
        var environment = AgentWorkspace.ResolveEnvironment(resources);
        environment["LOOPER_API_URL"] = options.Value.PublicUrl.TrimEnd('/');
        environment["LOOPER_RUN_ID"] = runId.ToString();
        environment["LOOPER_AGENT_ID"] = agent.Id.ToString();

        var stageLabel = trigger == ScriptModule.TriggerBefore ? "before-run" : "after-run";
        var results = new List<TestingActionResult>();
        foreach (var (resource, config) in scripts)
        {
            if (config.Code.Length == 0)
            {
                await log("error", $"Script '{resource.Name}' has no code — failing closed.");
                results.Add(new TestingActionResult(resource.Name, "", -1, false, 0, "The script has no code — a stage that cannot run fails closed."));
                continue;
            }
            await log("info", $"Running {stageLabel} script '{resource.Name}'.");
            var result = await RunAsync(resource.Id, resource.Name, config, defaultWorkingDirectory, environment,
                cancellationToken, trigger == ScriptModule.TriggerBefore ? BeforeOutputCap : ShellCommandRunner.DefaultOutputCap);
            results.Add(result);
            await log(result.Passed ? "info" : "error",
                $"Script '{resource.Name}' {(result.Passed ? "finished" : $"failed (exit {result.ExitCode})")} in {result.DurationMs}ms");
        }
        return results;
    }

    /// <summary>Materializes and runs one script. The working directory falls back to the given default.</summary>
    public Task<TestingActionResult> RunAsync(
        Guid resourceId, string name, ScriptConfig config, string defaultWorkingDirectory,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken,
        int outputCap = ShellCommandRunner.DefaultOutputCap) =>
        RunScriptAsync(options.Value, resourceId, name, config, defaultWorkingDirectory, environment, cancellationToken, outputCap);

    /// <summary>The one way a script is run, whoever asks: a stage, a manual run, or a Check that points at it.</summary>
    public static Task<TestingActionResult> RunScriptAsync(
        LooperOptions options, Guid resourceId, string name, ScriptConfig config, string defaultWorkingDirectory,
        IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken,
        int outputCap = ShellCommandRunner.DefaultOutputCap)
    {
        var path = ScriptResources.Materialize(resourceId, name, config);
        var command = ScriptResources.BuildCommand(config, path, options.PythonCommand);
        var timeout = TimeSpan.FromSeconds(config.TimeoutSeconds ?? options.TestingActionTimeoutSeconds);
        var workingDirectory = config.WorkingDirectory ?? defaultWorkingDirectory;
        Directory.CreateDirectory(workingDirectory);
        return ShellCommandRunner.RunAsync(name, command, workingDirectory, timeout, environment, cancellationToken, outputCap);
    }

    /// <summary>
    /// The prompt section carrying before-run script output to the agent. Null when nothing ran.
    /// Failures are included too — the agent should know an input it expected is missing.
    /// </summary>
    public static string? BuildPromptSection(IReadOnlyList<TestingActionResult> beforeResults)
    {
        if (beforeResults.Count == 0) return null;
        var builder = new StringBuilder(
            "SCRIPT OUTPUT — Looper ran these scripts just before this iteration; treat their output as fresh input for your task:");
        foreach (var result in beforeResults)
        {
            builder.AppendLine().Append("--- ").Append(result.Name)
                .Append(result.Passed ? "" : $" (FAILED, exit {result.ExitCode})").Append(" ---").AppendLine();
            builder.Append(string.IsNullOrWhiteSpace(result.Output) ? "(no output)" : result.Output);
        }
        return builder.ToString();
    }
}
