using System.Diagnostics;
using System.Text.RegularExpressions;
using Looper.Api.Domain;
using Looper.Api.Infrastructure.Execution;

namespace Looper.Api.Infrastructure.Workspaces;

public sealed class WorkspaceProvisioningException(string message) : Exception(message);

/// <summary>
/// Creates and removes workspace directories for Dynamic Workspaces pools.
/// Provisioning modes: blank, git-clone (depth 1), copy-template. Every path it
/// touches must live under the pool's root — deletion outside it is refused.
/// </summary>
public sealed partial class WorkspaceProvisioner(ILogger<WorkspaceProvisioner> logger)
{
    public static string Slugify(string unit)
    {
        var slug = SlugPattern().Replace(unit.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length > 60) slug = slug[..60].Trim('-');
        return slug;
    }

    /// <summary>Provisions the directory and seeds WORKBRIEF.md. Returns the workspace path.</summary>
    public async Task<string> ProvisionAsync(
        WorkspacePoolConfig config, string unitSlug, string brief, string createdBy, CancellationToken cancellationToken)
    {
        var root = ResolveRoot(config);
        Directory.CreateDirectory(root);

        var path = Path.Combine(root, unitSlug);
        for (var suffix = 2; Directory.Exists(path); suffix++)
        {
            path = Path.Combine(root, $"{unitSlug}-{suffix}");
        }

        switch (config.Provisioning)
        {
            case "git-clone":
                if (string.IsNullOrWhiteSpace(config.Source))
                    throw new WorkspaceProvisioningException("The pool's provisioning is git-clone but no source repository is configured.");
                await GitClone(config.Source, path, cancellationToken);
                break;

            case "copy-template":
                if (string.IsNullOrWhiteSpace(config.Source) || !Directory.Exists(config.Source))
                    throw new WorkspaceProvisioningException("The pool's template folder does not exist: " + config.Source);
                CopyDirectory(config.Source, path);
                break;

            default:
                Directory.CreateDirectory(path);
                break;
        }

        await File.WriteAllTextAsync(Path.Combine(path, "WORKBRIEF.md"), $"""
            # Work brief: {unitSlug}

            Created {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC by {createdBy}.

            ## Context

            {(string.IsNullOrWhiteSpace(brief) ? "(no context was passed — infer the unit of work from the task)" : brief)}

            ## Handoff protocol

            This workspace is dedicated to ONE unit of work and persists across loop iterations.
            Each iteration: read this file first; before finishing, append a short handoff note
            below (what you did, what's next) so the next iteration starts oriented. When the
            unit is complete, mark the workspace done via the workspaces API — the janitor
            removes it after the pool's retention window.

            ## Handoff notes

            """, cancellationToken);

        logger.LogInformation("Provisioned workspace {Path} ({Mode})", path, config.Provisioning);
        return path;
    }

    /// <summary>Deletes a workspace directory — only when it truly lives under the pool root.</summary>
    public void RemoveDirectory(WorkspacePoolConfig config, string workspacePath)
    {
        var root = Path.GetFullPath(ResolveRoot(config));
        var full = Path.GetFullPath(workspacePath);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new WorkspaceProvisioningException(
                $"Refusing to delete '{workspacePath}': it is not inside the pool root '{root}'.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
            logger.LogInformation("Removed workspace directory {Path}", full);
        }
    }

    private static string ResolveRoot(WorkspacePoolConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.RootPath))
            throw new WorkspaceProvisioningException("The pool has no root path configured.");
        var root = Path.GetFullPath(config.RootPath.Trim());
        if (Path.GetPathRoot(root) == root)
            throw new WorkspaceProvisioningException("The pool root cannot be a filesystem root.");
        return root;
    }

    private static async Task GitClone(string source, string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in new[] { "clone", "--depth", "1", source, path }) startInfo.ArgumentList.Add(a);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));
        using var process = Process.Start(startInfo)
            ?? throw new WorkspaceProvisioningException("git could not be started.");
        var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        _ = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw new WorkspaceProvisioningException("git clone timed out after 3 minutes.");
        }

        if (process.ExitCode != 0)
        {
            var error = (await stderr).Trim();
            throw new WorkspaceProvisioningException(
                $"git clone failed: {(error.Length > 300 ? error[..300] + "…" : error)}");
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}
