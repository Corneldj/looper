using Looper.Api.Domain;

namespace Looper.Api.Infrastructure.Execution;

public sealed record AgentExecutionContext(
    LoopAgent Agent,
    IReadOnlyList<Resource> Resources,
    Guid RunId,
    /// <summary>Set on review fix rounds: the reviewer's instructions appended to the loop prompt.</summary>
    string? FixInstructions = null,
    /// <summary>For event-triggered runs: the triggering events (topic + payload), verbatim.</summary>
    string? TriggerEvents = null,
    /// <summary>Output of before-run Script resources, already formatted as a prompt section.</summary>
    string? ScriptOutputs = null,
    /// <summary>Settings' default run budget; applies only when the agent sets no MaxBudgetUsd of its own.</summary>
    decimal? DefaultMaxBudgetUsd = null,
    /// <summary>Board resources' prompt sections, gathered by the harness before the run: the selected tickets, one-off prompts.</summary>
    IReadOnlyList<string>? Briefing = null);

public sealed record AgentExecutionOutcome(
    bool Success,
    string? ResultText,
    string? ErrorMessage,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    int NumTurns,
    long DurationMs);

/// <summary>Writes a log line for the active run; level is "info" | "warn" | "error".</summary>
public delegate Task RunLogWriter(string level, string message);

public interface IAgentExecutor
{
    Task<AgentExecutionOutcome> ExecuteAsync(AgentExecutionContext context, RunLogWriter log, CancellationToken cancellationToken);
}
