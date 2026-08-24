using Looper.Api.Domain;

namespace Looper.Api.Features.Dashboard;

public sealed record DashboardSummaryDto(
    decimal TotalCostUsd,
    int TotalRuns,
    double SuccessRate,
    int ActiveAgents,
    int TotalAgents,
    long AvgDurationMs,
    long TotalInputTokens,
    long TotalOutputTokens,
    double? CostTrendPct,
    double? RunsTrendPct);

public sealed record CostSeriesPointDto(
    string Date,
    decimal CostUsd,
    int Runs,
    int Failures);

public sealed record AgentBreakdownDto(
    Guid AgentId,
    string Name,
    string Model,
    bool Enabled,
    decimal CostUsd,
    int Runs,
    double SuccessRate,
    long AvgDurationMs,
    decimal AvgCostPerRunUsd);

public sealed record ModelUsageDto(
    string Model,
    decimal CostUsd,
    int Runs,
    long InputTokens,
    long OutputTokens);

public sealed record RecentFailureDto(
    Guid RunId,
    Guid AgentId,
    string AgentName,
    DateTime StartedAtUtc,
    RunStatus Status,
    string? ErrorMessage);

/// <summary>Shared time-window semantics for the dashboard queries.</summary>
internal static class DashboardWindow
{
    /// <summary>Clamps <paramref name="days"/> to 1..90 and returns the window [UtcNow - days, UtcNow).</summary>
    public static (int Days, DateTime FromUtc, DateTime ToUtc) Resolve(int days)
    {
        var clamped = Math.Clamp(days, 1, 90);
        var toUtc = DateTime.UtcNow;
        return (clamped, toUtc.AddDays(-clamped), toUtc);
    }
}
