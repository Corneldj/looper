using Looper.Api.Domain;

namespace Looper.Api.Infrastructure.Execution;

public sealed record AgentExecutionContext(
    LoopAgent Agent,
    IReadOnlyList<Resource> Resources,
    Guid RunId);

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
