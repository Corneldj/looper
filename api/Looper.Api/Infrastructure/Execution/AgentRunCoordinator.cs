using System.Collections.Concurrent;
using Looper.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// Owns the lifecycle of agent runs: enforces one active run per agent and a global concurrency cap,
/// persists run records and live logs, executes post-run testing actions, and supports cancellation.
/// </summary>
public sealed class AgentRunCoordinator(
    IDbContextFactory<LooperDbContext> dbFactory,
    ClaudeCliExecutor cliExecutor,
    SimulatedAgentExecutor simulatedExecutor,
    TestingActionRunner testingActionRunner,
    IOptions<LooperOptions> options,
    ILogger<AgentRunCoordinator> logger)
{
    private sealed class ActiveRun
    {
        public Guid RunId { get; init; }
        public CancellationTokenSource UserCancellation { get; } = new();
    }

    private readonly ConcurrentDictionary<Guid, ActiveRun> _activeByAgent = new();
    private readonly SemaphoreSlim _concurrencyGate = new(
        Math.Max(1, options.Value.MaxConcurrentRuns), Math.Max(1, options.Value.MaxConcurrentRuns));

    public bool IsRunning(Guid agentId) => _activeByAgent.ContainsKey(agentId);

    public IReadOnlyCollection<Guid> RunningAgentIds => [.. _activeByAgent.Keys];

    /// <summary>Starts a run for the agent unless one is already active. Returns the new run id, or null when busy.</summary>
    public async Task<Guid?> TriggerRunAsync(Guid agentId, RunTrigger trigger, CancellationToken cancellationToken = default)
    {
        var active = new ActiveRun { RunId = Guid.NewGuid() };
        if (!_activeByAgent.TryAdd(agentId, active)) return null;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var agent = await db.Agents.Include(a => a.Resources)
                .FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);

            if (agent is null)
            {
                _activeByAgent.TryRemove(agentId, out _);
                return null;
            }

            var run = new AgentRun
            {
                Id = active.RunId,
                AgentId = agentId,
                Trigger = trigger,
                Status = RunStatus.Running,
                StartedAtUtc = DateTime.UtcNow,
                Model = agent.Model
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            // Detach the agent graph from the scoped context; execution runs on its own contexts.
            var resources = agent.Resources.ToList();
            _ = Task.Run(() => ExecuteAsync(agent, resources, active), CancellationToken.None);
            return active.RunId;
        }
        catch
        {
            _activeByAgent.TryRemove(agentId, out _);
            throw;
        }
    }

    /// <summary>Requests cancellation of the agent's active run, if any.</summary>
    public bool CancelActiveRun(Guid agentId)
    {
        if (!_activeByAgent.TryGetValue(agentId, out var active)) return false;
        active.UserCancellation.Cancel();
        return true;
    }

    private async Task ExecuteAsync(LoopAgent agent, IReadOnlyList<Resource> resources, ActiveRun active)
    {
        var runId = active.RunId;
        RunLogWriter log = (level, message) => AppendLogAsync(runId, level, message);
        var gateAcquired = false;

        try
        {
            // Queue for an execution slot; only user cancellation applies while waiting —
            // the run-time budget must not tick down for runs stuck behind the concurrency cap.
            try
            {
                await _concurrencyGate.WaitAsync(active.UserCancellation.Token);
                gateAcquired = true;
            }
            catch (OperationCanceledException)
            {
                await FinalizeCancelledAsync(runId, agent.Id, timedOut: false, log);
                return;
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(options.Value.RunTimeoutMinutes));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                timeoutCts.Token, active.UserCancellation.Token);

            var executor = agent.DryRun ? (IAgentExecutor)simulatedExecutor : cliExecutor;
            var context = new AgentExecutionContext(agent, resources, runId);

            AgentExecutionOutcome outcome;
            try
            {
                outcome = await executor.ExecuteAsync(context, log, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                await FinalizeCancelledAsync(runId, agent.Id, timeoutCts.IsCancellationRequested, log);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Run {RunId} for agent {AgentName} crashed", runId, agent.Name);
                await log("error", $"Run crashed: {ex.Message}");
                outcome = new AgentExecutionOutcome(false, null, ex.Message, 0, 0, 0, 0, 0, 0, 0);
            }

            (string ResultsJson, bool AllPassed)? testResults = null;
            if (outcome.Success && !agent.DryRun)
            {
                try
                {
                    testResults = await testingActionRunner.RunAllAsync(agent, resources, log, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    await FinalizeCancelledAsync(runId, agent.Id, timeoutCts.IsCancellationRequested, log);
                    return;
                }
            }
            else if (outcome.Success && agent.DryRun && resources.Any(r => r.Type == ResourceType.TestingAction))
            {
                await log("info", "[dry run] Testing actions skipped.");
            }

            await FinalizeAsync(runId, agent.Id, outcome, testResults);
        }
        catch (Exception ex)
        {
            // Last resort: never leave the run stuck in 'Running' or the agent wedged.
            logger.LogError(ex, "Run {RunId} for agent {AgentName} failed to finalize", runId, agent.Name);
            await TryMarkFailedAsync(runId, agent.Id, $"Internal error while finalizing the run: {ex.Message}");
        }
        finally
        {
            if (gateAcquired) _concurrencyGate.Release();
            _activeByAgent.TryRemove(agent.Id, out _);
        }
    }

    private async Task FinalizeAsync(Guid runId, Guid agentId, AgentExecutionOutcome outcome,
        (string ResultsJson, bool AllPassed)? testResults)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.Runs.FindAsync(runId);
        if (run is null) return;

        run.CompletedAtUtc = DateTime.UtcNow;
        run.Status = outcome.Success ? RunStatus.Succeeded : RunStatus.Failed;
        run.CostUsd = outcome.CostUsd;
        run.InputTokens = outcome.InputTokens;
        run.OutputTokens = outcome.OutputTokens;
        run.CacheReadTokens = outcome.CacheReadTokens;
        run.CacheCreationTokens = outcome.CacheCreationTokens;
        run.NumTurns = outcome.NumTurns;
        run.DurationMs = outcome.DurationMs > 0
            ? outcome.DurationMs
            : (long)(run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds;
        run.ResultText = outcome.ResultText;
        run.ErrorMessage = outcome.ErrorMessage;
        if (testResults is { } tests)
        {
            run.TestResultsJson = tests.ResultsJson;
            run.TestsPassed = tests.AllPassed;
        }

        await db.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.LastRunAtUtc, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task FinalizeCancelledAsync(Guid runId, Guid agentId, bool timedOut, RunLogWriter log)
    {
        await log(timedOut ? "error" : "warn", timedOut
            ? $"Run exceeded the {options.Value.RunTimeoutMinutes} minute limit and was terminated."
            : "Run was cancelled.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var run = await db.Runs.FindAsync(runId);
        if (run is null) return;

        run.CompletedAtUtc = DateTime.UtcNow;
        run.Status = timedOut ? RunStatus.TimedOut : RunStatus.Cancelled;
        run.DurationMs = (long)(run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds;

        await db.Agents.Where(a => a.Id == agentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.LastRunAtUtc, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task TryMarkFailedAsync(Guid runId, Guid agentId, string error)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var run = await db.Runs.FindAsync(runId);
            if (run is null || run.Status != RunStatus.Running) return;

            run.CompletedAtUtc = DateTime.UtcNow;
            run.Status = RunStatus.Failed;
            run.ErrorMessage = error;
            run.DurationMs = (long)(run.CompletedAtUtc.Value - run.StartedAtUtc).TotalMilliseconds;

            await db.Agents.Where(a => a.Id == agentId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.LastRunAtUtc, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not mark run {RunId} as failed", runId);
        }
    }

    private async Task AppendLogAsync(Guid runId, string level, string message)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            db.RunLogs.Add(new RunLogEntry { RunId = runId, Level = level, Message = message });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist log line for run {RunId}", runId);
        }
    }
}
