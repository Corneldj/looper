using Looper.Api.Domain;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Infrastructure.Workspaces;

/// <summary>
/// Removes done workspaces once their pool's retention window has elapsed. Runs hourly;
/// each removal is best-effort and logged, never fatal.
/// </summary>
public sealed class WorkspaceJanitorService(
    IDbContextFactory<LooperDbContext> dbFactory,
    WorkspaceProvisioner provisioner,
    ILogger<WorkspaceJanitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        try
        {
            do
            {
                try
                {
                    var cleaned = await CleanupDueAsync(DateTime.UtcNow, stoppingToken);
                    if (cleaned > 0) logger.LogInformation("Workspace janitor cleaned {Count} workspace(s)", cleaned);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Workspace janitor pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>One janitor pass; separated from the timer so tests can drive the clock.</summary>
    public async Task<int> CleanupDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var done = await db.Workspaces
            .Include(w => w.Resource)
            .Where(w => w.Status == WorkspaceStatus.Done && w.DoneAtUtc != null)
            .ToListAsync(cancellationToken);

        var cleaned = 0;
        foreach (var workspace in done)
        {
            var config = ResourceConfig.Parse<WorkspacePoolConfig>(workspace.Resource);
            var retention = Math.Max(0, config.RetentionDays);
            if (workspace.DoneAtUtc!.Value.AddDays(retention) > nowUtc) continue;

            try
            {
                provisioner.RemoveDirectory(config, workspace.Path);
                workspace.Status = WorkspaceStatus.Cleaned;
                workspace.CleanedAtUtc = nowUtc;
                cleaned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not clean workspace {Path}", workspace.Path);
            }
        }

        if (cleaned > 0) await db.SaveChangesAsync(cancellationToken);
        return cleaned;
    }
}
