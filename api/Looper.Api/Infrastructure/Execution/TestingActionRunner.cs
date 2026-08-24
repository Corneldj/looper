using System.Diagnostics;
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

    public async Task<(string ResultsJson, bool AllPassed)?> RunAllAsync(
        LoopAgent agent, IReadOnlyList<Resource> resources, RunLogWriter log, CancellationToken cancellationToken)
    {
        var actions = resources.Where(r => r.Type == ResourceType.TestingAction).ToList();
        if (actions.Count == 0) return null;

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
            var result = await RunOneAsync(action.Name, config, defaultWorkingDirectory, cancellationToken);
            results.Add(result);
            await log(result.Passed ? "info" : "error",
                $"Testing action '{action.Name}' {(result.Passed ? "passed" : $"failed (exit {result.ExitCode})")} in {result.DurationMs}ms");
        }

        if (results.Count == 0) return null;
        return (JsonSerializer.Serialize(results, JsonOptions), results.All(r => r.Passed));
    }

    private async Task<TestingActionResult> RunOneAsync(
        string name, TestingActionConfig config, string defaultWorkingDirectory, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(config.TimeoutSeconds ?? options.Value.TestingActionTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-lc", config.Command },
            WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                ? defaultWorkingDirectory
                : config.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            stopwatch.Stop();

            var output = Tail((await stdoutTask) + Environment.NewLine + (await stderrTask), 4000);
            return new TestingActionResult(name, config.Command, process.ExitCode, process.ExitCode == 0,
                stopwatch.ElapsedMilliseconds, output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Run-level cancel/timeout: kill the test process tree and let the coordinator
            // finalize the run as Cancelled/TimedOut instead of recording a bogus result.
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new TestingActionResult(name, config.Command, -1, false, stopwatch.ElapsedMilliseconds,
                $"Timed out after {timeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new TestingActionResult(name, config.Command, -1, false, stopwatch.ElapsedMilliseconds,
                $"Failed to run: {ex.Message}");
        }
    }

    private static string Tail(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : "…" + trimmed[^max..];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already gone.
        }
    }
}
