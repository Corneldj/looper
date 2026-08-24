using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

public sealed record ClaudeCliStatus(
    bool Available,
    string? Version,
    string Command,
    string? Error,
    DateTime CheckedAtUtc);

/// <summary>
/// Probes whether the Claude Code CLI is runnable (claude --version) and can run the
/// official installer. A successful probe is cached until an explicit refresh; failures
/// are re-probed after a short window so a fresh install is picked up quickly.
/// </summary>
public sealed class ClaudeCliStatusService(IOptions<LooperOptions> options, ILogger<ClaudeCliStatusService> logger)
{
    private static readonly TimeSpan FailureCacheWindow = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeCliStatus? _cached;

    public async Task<ClaudeCliStatus> GetStatusAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && IsFresh(_cached)) return _cached!;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && IsFresh(_cached)) return _cached!;
            _cached = await ProbeAsync(cancellationToken);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs the configured installer, then re-probes. Returns the tail of the installer output.</summary>
    public async Task<(bool Success, string Output, ClaudeCliStatus Status)> InstallAsync(CancellationToken cancellationToken)
    {
        var installCommand = options.Value.ClaudeInstallCommand;
        logger.LogInformation("Running Claude Code installer: {Command}", installCommand);

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-lc", installCommand },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

        string output;
        bool installerSucceeded;
        using (var process = new Process { StartInfo = startInfo })
        {
            try
            {
                process.Start();
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                output = Tail((await stdoutTask) + Environment.NewLine + (await stderrTask), 6000);
                installerSucceeded = process.ExitCode == 0;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return (false, "The installer timed out after 5 minutes.", await GetStatusAsync(refresh: true, cancellationToken));
            }
            catch (Exception ex)
            {
                return (false, $"Could not start the installer: {ex.Message}",
                    await GetStatusAsync(refresh: true, cancellationToken));
            }
        }

        var status = await GetStatusAsync(refresh: true, cancellationToken);
        return (installerSucceeded && status.Available, output, status);
    }

    private static bool IsFresh(ClaudeCliStatus? status) =>
        status is not null && (status.Available || DateTime.UtcNow - status.CheckedAtUtc < FailureCacheWindow);

    private async Task<ClaudeCliStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var command = options.Value.ClaudeCommand;
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            ArgumentList = { "--version" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                return new ClaudeCliStatus(false, null, command,
                    $"'{command} --version' exited with code {process.ExitCode}. {Tail(stderr, 300)}".Trim(),
                    DateTime.UtcNow);
            }

            var version = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return new ClaudeCliStatus(true, version, command, null, DateTime.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ClaudeCliStatus(false, null, command,
                $"'{command} --version' did not respond within 15 seconds.", DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // Typically: No such file or directory — the CLI is simply not installed / not on PATH.
            return new ClaudeCliStatus(false, null, command, ex.Message, DateTime.UtcNow);
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
