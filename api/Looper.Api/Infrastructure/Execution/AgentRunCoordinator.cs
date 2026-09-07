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
    ScriptRunner scriptRunner,
    ReviewRunner reviewRunner,
    EventDispatcher eventDispatcher,
    IOptions<LooperOptions> options,
    ILogger<AgentRunCoordinator> logger)
{
    private sealed record ReviewRound(int Round, string Reviewer, string Verdict, string Summary, string? FixInstructions, decimal CostUsd);

    private sealed record ReviewInfo(bool? Passed, int FixRounds, string? Json, string? EscalationReason);

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
    public async Task<Guid?> TriggerRunAsync(Guid agentId, RunTrigger trigger, CancellationToken cancellationToken = default,
        string? eventContext = null, int eventDepth = 0)
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
                Model = agent.Model,
                DryRun = agent.DryRun,
                EventDepth = eventDepth
            };
            db.Runs.Add(run);
            await db.SaveChangesAsync(cancellationToken);

            // Resolved user-action responses are delivered exactly once, on the next real run.
            string? userResponses = null;
            if (!agent.DryRun)
            {
                var undelivered = await db.UserActionRequests
                    .Where(r => r.AgentId == agentId && r.Status == UserActionStatus.Resolved
                        && r.Response != null && r.ResponseDeliveredAtUtc == null)
                    .OrderBy(r => r.ResolvedAtUtc)
                    .ToListAsync(cancellationToken);
                if (undelivered.Count > 0)
                {
                    userResponses = string.Join("\n\n", undelivered.Select(r =>
                        $"Your request \"{r.Title}\" — the user responded:\n{r.Response}"));
                    foreach (var r in undelivered) r.ResponseDeliveredAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            // Detach the agent graph from the scoped context; execution runs on its own contexts.
            var resources = agent.Resources.ToList();
            _ = Task.Run(() => ExecuteAsync(agent, resources, active, userResponses, eventContext, eventDepth), CancellationToken.None);
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

    private async Task ExecuteAsync(LoopAgent agent, IReadOnlyList<Resource> resources, ActiveRun active,
        string? userResponses = null, string? eventContext = null, int eventDepth = 0)
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

            // Before-run scripts: deterministic input gathering ahead of the model. Their output
            // becomes prompt context; a failure fails the run before any tokens are spent.
            IReadOnlyList<TestingActionResult> beforeResults = [];
            if (!agent.DryRun)
            {
                try
                {
                    beforeResults = await scriptRunner.RunStageAsync(
                        Modules.BuiltIn.ScriptModule.TriggerBefore, agent, resources, runId, log, linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    await FinalizeCancelledAsync(runId, agent.Id, timeoutCts.IsCancellationRequested, log);
                    return;
                }
                if (beforeResults.Any(r => !r.Passed))
                {
                    var failedNames = string.Join(", ", beforeResults.Where(r => !r.Passed).Select(r => $"'{r.Name}'"));
                    await log("error", $"Before-run script(s) {failedNames} failed; the iteration was not started.");
                    var aborted = new AgentExecutionOutcome(false, null,
                        $"Before-run script(s) {failedNames} failed; the iteration was not started.", 0, 0, 0, 0, 0, 0, 0);
                    await FinalizeAsync(runId, agent.Id, aborted, (TestingActionRunner.Serialize(beforeResults), false),
                        new ReviewInfo(null, 0, null, null));
                    await RaiseCompletionEventAsync(agent, runId, false, eventDepth);
                    return;
                }
            }
            else if (Modules.BuiltIn.ScriptResources.Scripts(resources).Count > 0)
            {
                await log("info", "[dry run] Scripts skipped.");
            }

            var context = new AgentExecutionContext(agent, resources, runId,
                UserResponses: userResponses, TriggerEvents: eventContext,
                ScriptOutputs: ScriptRunner.BuildPromptSection(beforeResults));

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
                    testResults = await RunPostRunGatesAsync(agent, resources, runId, beforeResults, log, linkedCts.Token);
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

            // The review gate: completion is not acceptance. Independent reviewers judge the
            // work; failures loop fix instructions back through the worker until they pass
            // or the fix budget runs out.
            ReviewInfo review;
            try
            {
                (outcome, testResults, review) = await RunReviewGateAsync(
                    agent, resources, runId, executor, outcome, testResults, beforeResults, log, linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                await FinalizeCancelledAsync(runId, agent.Id, timeoutCts.IsCancellationRequested, log);
                return;
            }

            await FinalizeAsync(runId, agent.Id, outcome, testResults, review);
            await RaiseCompletionEventAsync(agent, runId, outcome.Success, eventDepth);
            if (!agent.DryRun) LogOutcomeToGraphInboxes(agent, resources, runId, outcome, review, log);
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

    /// <summary>
    /// The post-run gate: testing actions, then after-run scripts, all in one verdict. Before-run
    /// results are carried along so the run record shows every script that executed. Null when
    /// nothing at all ran.
    /// </summary>
    private async Task<(string ResultsJson, bool AllPassed)?> RunPostRunGatesAsync(
        LoopAgent agent, IReadOnlyList<Resource> resources, Guid runId,
        IReadOnlyList<TestingActionResult> beforeResults, RunLogWriter log, CancellationToken cancellationToken)
    {
        var results = new List<TestingActionResult>(beforeResults);
        results.AddRange(await testingActionRunner.RunAsync(agent, resources, log, cancellationToken));
        results.AddRange(await scriptRunner.RunStageAsync(
            Modules.BuiltIn.ScriptModule.TriggerAfter, agent, resources, runId, log, cancellationToken));
        if (results.Count == 0) return null;
        return (TestingActionRunner.Serialize(results), results.All(r => r.Passed));
    }

    /// <summary>
    /// Runs every attached Reviewer against the completed work. On failure, the worker gets the
    /// combined fix instructions and runs again (testing actions re-gate the revision), up to the
    /// largest MaxFixRounds among reviewers. Cost, tokens and turns accumulate onto the outcome.
    /// </summary>
    private async Task<(AgentExecutionOutcome Outcome, (string, bool)? TestResults, ReviewInfo Review)> RunReviewGateAsync(
        LoopAgent agent,
        IReadOnlyList<Resource> resources,
        Guid runId,
        IAgentExecutor executor,
        AgentExecutionOutcome outcome,
        (string ResultsJson, bool AllPassed)? testResults,
        IReadOnlyList<TestingActionResult> beforeResults,
        RunLogWriter log,
        CancellationToken cancellationToken)
    {
        var reviewers = resources
            .Where(r => r.Type == ResourceType.Reviewer)
            .Select(r => (Resource: r, Config: ResourceConfig.Parse<ReviewerConfig>(r)))
            .Where(r => !string.IsNullOrWhiteSpace(r.Config.Rubric))
            .ToList();

        var none = new ReviewInfo(null, 0, null, null);
        if (reviewers.Count == 0 || !outcome.Success) return (outcome, testResults, none);
        if (testResults is { AllPassed: false }) return (outcome, testResults, none); // testing gate already failed

        if (agent.DryRun)
        {
            var simulated = reviewers.Select(r => new ReviewRound(0, r.Resource.Name, "pass",
                "[dry run] Review simulated — no tokens spent.", null, 0)).ToList();
            foreach (var r in reviewers)
            {
                await log("info", $"[dry run] Review '{r.Resource.Name}' simulated: PASS.");
            }
            return (outcome, testResults, new ReviewInfo(true, 0, SerializeRounds(simulated), null));
        }

        var (workingDirectory, additionalDirectories) = AgentWorkspace.Resolve(agent, resources);
        var maxFixRounds = Math.Max(0, reviewers.Max(r => r.Config.MaxFixRounds));
        var rounds = new List<ReviewRound>();
        var fixRoundsUsed = 0;

        for (var round = 0; ; round++)
        {
            var failures = new List<(string Reviewer, ReviewVerdict Verdict)>();
            var reviewCost = 0m;
            foreach (var (resource, config) in reviewers)
            {
                var verdict = await reviewRunner.ReviewAsync(
                    agent, resource, config, workingDirectory, additionalDirectories,
                    outcome.ResultText, log, cancellationToken);
                reviewCost += verdict.CostUsd;
                rounds.Add(new ReviewRound(round, resource.Name,
                    verdict.Inconclusive ? "inconclusive" : verdict.Pass ? "pass" : "fail",
                    verdict.Summary, verdict.FixInstructions, verdict.CostUsd));
                if (!verdict.Pass) failures.Add((resource.Name, verdict));
            }
            outcome = outcome with { CostUsd = outcome.CostUsd + reviewCost };

            if (failures.Count == 0)
            {
                if (round > 0) await log("info", $"Review passed after {round} fix round(s).");
                return (outcome, testResults, new ReviewInfo(true, fixRoundsUsed, SerializeRounds(rounds), null));
            }

            if (round >= maxFixRounds)
            {
                var summary = string.Join(" | ", failures.Select(f => $"{f.Reviewer}: {f.Verdict.Summary}"));
                await log("error", $"Review failed after {fixRoundsUsed} fix round(s); giving up.");
                var escalate = reviewers.Any(r => r.Config.EscalateOnFail)
                    ? $"Review failed after {fixRoundsUsed} fix round(s): {Truncate(summary, 800)}"
                    : null;
                var failed = outcome with
                {
                    Success = false,
                    ErrorMessage = Truncate($"Failed review after {fixRoundsUsed} fix round(s). {summary}", 4000),
                };
                return (failed, testResults, new ReviewInfo(false, fixRoundsUsed, SerializeRounds(rounds), escalate));
            }

            // Fix round: hand the combined instructions back to the worker.
            fixRoundsUsed++;
            var instructions = string.Join("\n\n", failures.Select(f =>
                $"From reviewer '{f.Reviewer}':\n{f.Verdict.FixInstructions ?? f.Verdict.Summary}"));
            await log("info", $"Fix round {fixRoundsUsed}/{maxFixRounds}: re-running the worker with the reviewer's instructions.");

            var fixOutcome = await executor.ExecuteAsync(
                new AgentExecutionContext(agent, resources, runId, instructions), log, cancellationToken);
            outcome = new AgentExecutionOutcome(
                fixOutcome.Success,
                fixOutcome.ResultText ?? outcome.ResultText,
                fixOutcome.ErrorMessage,
                outcome.CostUsd + fixOutcome.CostUsd,
                outcome.InputTokens + fixOutcome.InputTokens,
                outcome.OutputTokens + fixOutcome.OutputTokens,
                outcome.CacheReadTokens + fixOutcome.CacheReadTokens,
                outcome.CacheCreationTokens + fixOutcome.CacheCreationTokens,
                outcome.NumTurns + fixOutcome.NumTurns,
                outcome.DurationMs + fixOutcome.DurationMs);

            if (!fixOutcome.Success)
            {
                await log("error", "The fix round itself failed; review cannot pass.");
                return (outcome, testResults,
                    new ReviewInfo(false, fixRoundsUsed, SerializeRounds(rounds),
                        reviewers.Any(r => r.Config.EscalateOnFail) ? "Review fix round failed to execute." : null));
            }

            // A revision can break what the testing gate had already accepted — re-gate it.
            testResults = await RunPostRunGatesAsync(agent, resources, runId, beforeResults, log, cancellationToken);
            if (testResults is { AllPassed: false })
            {
                await log("error", "Testing actions failed on the revised work; review cannot pass.");
                return (outcome, testResults,
                    new ReviewInfo(false, fixRoundsUsed, SerializeRounds(rounds),
                        reviewers.Any(r => r.Config.EscalateOnFail) ? "Review fix round broke the testing gate." : null));
            }
        }
    }

    private static string SerializeRounds(List<ReviewRound> rounds) =>
        System.Text.Json.JsonSerializer.Serialize(rounds, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    /// <summary>The deterministic backbone of the event system: every finished run announces itself.</summary>
    /// <summary>
    /// Episodic memory without spending agent tokens: the harness drops each real run's outcome
    /// into every attached memory graph that opted in (autoLog). The graph's curator folds these
    /// into canonical episodes and facts on its next curation pass. Best-effort, never fatal.
    /// </summary>
    private void LogOutcomeToGraphInboxes(LoopAgent agent, IReadOnlyList<Resource> resources,
        Guid runId, AgentExecutionOutcome outcome, ReviewInfo review, RunLogWriter log)
    {
        foreach (var (resource, path, _) in Modules.BuiltIn.GraphInfrastructure.AutoLogTargets(resources))
        {
            try
            {
                var summary = outcome.ResultText ?? outcome.ErrorMessage ?? "";
                if (summary.Length > 400) summary = summary[..400] + "…";
                var reviewNote = review.Passed switch
                {
                    true => $" Review: passed after {review.FixRounds} fix round(s).",
                    false => " Review: FAILED.",
                    null => ""
                };
                var text = $"Run {(outcome.Success ? "succeeded" : "FAILED")} " +
                           $"({outcome.NumTurns} turns, {outcome.DurationMs / 1000}s, ${outcome.CostUsd:0.####})." +
                           $"{reviewNote} {summary}".TrimEnd();
                Modules.BuiltIn.GraphInfrastructure.WriteInboxItem(path, "episode", text, agent.Name, runId.ToString());
                _ = log("info", $"Run outcome logged to '{resource.Name}' inbox.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not log run outcome to graph inbox at {Path}", path);
            }
        }
    }

    private async Task RaiseCompletionEventAsync(LoopAgent agent, Guid runId, bool succeeded, int eventDepth)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await eventDispatcher.RaiseAsync(
                db,
                EventDispatcher.CompletionTopic(agent.Name, succeeded),
                $"Agent '{agent.Name}' run {(succeeded ? "succeeded" : "failed")}. Run id: {runId}."
                    + (agent.DryRun ? " (dry run)" : ""),
                EventSource.Harness, agent.Id, runId, eventDepth, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not raise completion event for run {RunId}", runId);
        }
    }

    private async Task FinalizeAsync(Guid runId, Guid agentId, AgentExecutionOutcome outcome,
        (string ResultsJson, bool AllPassed)? testResults, ReviewInfo review)
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
        run.ReviewPassed = review.Passed;
        run.ReviewRounds = review.FixRounds;
        run.ReviewJson = review.Json;
        if (review.EscalationReason is not null)
        {
            run.Escalated = true;
            run.EscalationReason = review.EscalationReason;
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
