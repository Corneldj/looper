using System.Diagnostics;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// Runs one shell command with a wall-clock limit and captures its output. The single
/// process-execution path behind testing actions and scripts, so both gate the same way:
/// exit 0 passes, anything else (including a timeout) fails.
/// </summary>
public static class ShellCommandRunner
{
    public const int DefaultOutputCap = 4000;

    public static async Task<TestingActionResult> RunAsync(
        string name,
        string command,
        string workingDirectory,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken,
        int outputCap = DefaultOutputCap)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-lc", command },
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (environment is not null)
        {
            foreach (var (key, value) in environment) startInfo.Environment[key] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            stopwatch.Stop();

            var output = Tail((await stdoutTask) + Environment.NewLine + (await stderrTask), outputCap);
            return new TestingActionResult(name, command, process.ExitCode, process.ExitCode == 0,
                stopwatch.ElapsedMilliseconds, output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Run-level cancel/timeout: kill the process tree and let the caller finalize the
            // run as Cancelled/TimedOut instead of recording a bogus result.
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new TestingActionResult(name, command, -1, false, stopwatch.ElapsedMilliseconds,
                $"Timed out after {timeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex)
        {
            TryKill(process);
            return new TestingActionResult(name, command, -1, false, stopwatch.ElapsedMilliseconds,
                $"Failed to run: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the last <paramref name="max"/> characters — plus every `@metric` line from the part
    /// that was cut, so a chatty script never loses a measurement to truncation.
    /// </summary>
    private static string Tail(string value, int max)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= max) return trimmed;
        var head = trimmed[..^max];
        var kept = head.Split('\n')
            .Where(line => line.TrimStart().StartsWith("@metric", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();
        return (kept.Count > 0 ? string.Join('\n', kept) + "\n" : "") + "…" + trimmed[^max..];
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
