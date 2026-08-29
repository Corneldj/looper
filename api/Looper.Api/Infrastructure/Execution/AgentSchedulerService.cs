using Looper.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>Fires enabled agents whose next-run time has arrived. The loop cadence lives on each agent.</summary>
public sealed class AgentSchedulerService(
    IDbContextFactory<LooperDbContext> dbFactory,
    AgentRunCoordinator coordinator,
    EventDispatcher eventDispatcher,
    IOptions<LooperOptions> options,
    ILogger<AgentSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent scheduler started (poll every {Seconds}s)", options.Value.SchedulerPollSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(2, options.Value.SchedulerPollSeconds)));
        while (await WaitForNextTick(timer, stoppingToken))
        {
            try
            {
                await RunDueAgentsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler tick failed");
            }
        }
    }

    private async Task RunDueAgentsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        // The scheduler serves ONLY schedule-triggered agents; event-triggered ones are
        // started exclusively by the event pump below. One or the other — never both.
        var dueAgents = await db.Agents
            .Where(a => a.Enabled && a.TriggerMode == TriggerMode.Scheduled
                && (a.NextRunAtUtc == null || a.NextRunAtUtc <= now))
            .Select(a => new { a.Id, a.Name, a.IntervalMinutes })
            .ToListAsync(cancellationToken);

        foreach (var agent in dueAgents)
        {
            if (coordinator.IsRunning(agent.Id)) continue;

            // Parked on a human: an open blocking User Action Request holds the loop, without failing it.
            if (await Features.UserActions.UserActionGate.IsBlockedAsync(db, agent.Id, cancellationToken))
            {
                continue;
            }

            var runId = await coordinator.TriggerRunAsync(agent.Id, RunTrigger.Scheduled, cancellationToken);
            if (runId is null) continue;

            logger.LogInformation("Scheduled run {RunId} started for agent {AgentName}", runId, agent.Name);
            await db.Agents.Where(a => a.Id == agent.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    a => a.NextRunAtUtc, now.AddMinutes(Math.Max(1, agent.IntervalMinutes))), cancellationToken);
        }

        // Event pump: hand pending event deliveries to their listeners (also retries agents
        // that were busy or user-action-parked when the event arrived).
        await eventDispatcher.PumpAsync(db, coordinator, cancellationToken);
    }

    private static async Task<bool> WaitForNextTick(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
