using Looper.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Delivery;

/// <summary>
/// Background refresh of delivery data: open GitHub PRs are re-checked for merge/close/review
/// activity, and merged PRs get their code-survival measurement once the window elapses.
/// </summary>
public sealed class DeliverySyncService(
    IDbContextFactory<LooperDbContext> dbFactory,
    PullRequestSynchronizer synchronizer,
    IOptions<LooperOptions> options,
    ILogger<DeliverySyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.DeliverySyncMinutes));
        using var timer = new PeriodicTimer(interval);

        try
        {
            do
            {
                try
                {
                    await SyncDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Delivery sync pass failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task SyncDueAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var survivalDeadline = DateTime.UtcNow.AddDays(-options.Value.SurvivalWindowDays);

        var due = await db.PullRequests
            .Where(pr =>
                (pr.Status == PrStatus.Open && pr.Url != null) ||
                (pr.Status == PrStatus.Merged && pr.SurvivalCheckedAtUtc == null
                    && pr.RepoPath != null && pr.MergeCommitSha != null
                    && pr.MergedAtUtc != null && pr.MergedAtUtc <= survivalDeadline))
            .OrderBy(pr => pr.LastSyncedAtUtc ?? DateTime.MinValue)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var pr in due)
        {
            await synchronizer.SyncAsync(pr, cancellationToken);
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Delivery sync refreshed {Count} PR(s)", due.Count);
        }
    }
}
