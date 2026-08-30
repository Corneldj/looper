using System.Diagnostics;
using Looper.Api.Domain;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// The harness side of decoupled memory: before a real run, recall the most relevant facts
/// from every attached memory graph and inject them as a MEMORY CONTEXT preamble. Retrieval
/// is deterministic (the toolkit's lexical recall) and free — no agent turns or tokens are
/// spent asking the model to remember to look things up.
/// </summary>
public sealed class GraphContextService(IOptions<LooperOptions> options, ILogger<GraphContextService> logger)
{
    private const int TimeoutMs = 10_000;

    public async Task<IReadOnlyList<string>> BuildPreambleSectionsAsync(
        LoopAgent agent, IReadOnlyList<Resource> resources, string? triggerEvents,
        RunLogWriter log, CancellationToken cancellationToken)
    {
        var sections = new List<string>();
        foreach (var resource in resources.Where(GraphInfrastructure.IsMemoryGraph))
        {
            var context = new ResourceModuleContext(resource.ConfigJson, agent.Id, agent.Name);
            var config = GraphInfrastructure.SharedConfig(context);
            var path = context.GetString("path");
            var k = config.EffectivePreambleK;
            if (k <= 0 || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            if (!File.Exists(Path.Combine(path, GraphWorkspace.ToolFileName))) continue;

            var query = BuildQuery(agent, triggerEvents);
            try
            {
                var recalled = await RunRecallAsync(path, query, k, cancellationToken);
                if (string.IsNullOrWhiteSpace(recalled) || recalled.StartsWith("nothing recalled", StringComparison.Ordinal))
                {
                    continue;
                }

                sections.Add(
                    $"MEMORY CONTEXT from '{resource.Name}' — retrieved deterministically by the harness before this " +
                    "run (top matches for this iteration's task). Treat it as recall, not ground truth: verify anything " +
                    $"load-bearing with the graph tool at {path}.\n{recalled.Trim()}");
                await log("info", $"Memory preamble injected from '{resource.Name}' ({k} facts requested).");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await log("warn", $"Memory preamble from '{resource.Name}' timed out; run continues without it.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Memory preamble failed for graph {Resource}", resource.Name);
                await log("warn", $"Memory preamble from '{resource.Name}' failed ({ex.Message}); run continues without it.");
            }
        }

        return sections;
    }

    /// <summary>The recall query is the run's intent: who is running, what the loop is for, what triggered it.</summary>
    internal static string BuildQuery(LoopAgent agent, string? triggerEvents)
    {
        var prompt = agent.Prompt.Length > 300 ? agent.Prompt[..300] : agent.Prompt;
        var events = triggerEvents is null ? "" : " " + (triggerEvents.Length > 200 ? triggerEvents[..200] : triggerEvents);
        return $"{agent.Name} {agent.Description} {prompt}{events}".Trim();
    }

    private async Task<string> RunRecallAsync(string path, string query, int k, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.PythonCommand,
            WorkingDirectory = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Combine(path, GraphWorkspace.ToolFileName));
        startInfo.ArgumentList.Add("--dir");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("recall");
        startInfo.ArgumentList.Add(query);
        startInfo.ArgumentList.Add("-k");
        startInfo.ArgumentList.Add(k.ToString());

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"could not start {options.Value.PythonCommand}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeoutMs);
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return process.ExitCode == 0 ? output : "";
    }
}
