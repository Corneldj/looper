using Looper.Api.Domain;

namespace Looper.Api.Features.Delivery;

public sealed record PullRequestDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    Guid? RunId,
    string Title,
    string? Url,
    string Repository,
    int? Number,
    PrStatus Status,
    DateTime OpenedAtUtc,
    DateTime? MergedAtUtc,
    DateTime? ClosedAtUtc,
    int Additions,
    int Deletions,
    int ReviewRounds,
    int ReviewComments,
    int HumanCommits,
    bool? FirstPass,
    string? RepoPath,
    string? MergeCommitSha,
    double? SurvivalRate,
    DateTime? SurvivalCheckedAtUtc,
    DateTime? LastSyncedAtUtc,
    string? SyncError);

public static class PullRequestMapper
{
    public static PullRequestDto ToDto(this AgentPullRequest pr, string agentName) => new(
        pr.Id, pr.AgentId, agentName, pr.RunId, pr.Title, pr.Url, pr.Repository, pr.Number,
        pr.Status, pr.OpenedAtUtc, pr.MergedAtUtc, pr.ClosedAtUtc,
        pr.Additions, pr.Deletions, pr.ReviewRounds, pr.ReviewComments, pr.HumanCommits,
        pr.FirstPass, pr.RepoPath, pr.MergeCommitSha,
        pr.SurvivalRate, pr.SurvivalCheckedAtUtc, pr.LastSyncedAtUtc, pr.SyncError);
}

// ---------- The five metrics that measure value that stuck ----------

public sealed record AgentDeliveryRowDto(
    Guid AgentId,
    string Name,
    int AutonomyLevel,
    int MergedPrs,
    decimal CostUsd,
    decimal? CostPerMergedPrUsd,
    double? FirstPassRate,
    double? EscalationRate,
    int CompletedRuns,
    string? Recommendation);   // promote | demote | hold | null (insufficient data)

public sealed record DeliveryMetricsDto(
    int WindowDays,
    decimal TotalCostUsd,       // real (non-dry) run spend in the window
    int MergedPrs,
    int OpenPrs,
    int ClosedPrs,              // closed without merging
    decimal? CostPerMergedPrUsd,
    double? FirstPassRate,      // merged with 0 change-request rounds and 0 human commits
    double? CodeSurvivalRate,   // added lines still blame-attributed to the PR after the window
    int SurvivalCheckedPrs,
    double? ReviewChurnPer100Lines, // (change-request rounds + review comments) per 100 changed lines
    double? EscalationRate,     // escalated real runs / completed real runs
    int EscalatedRuns,
    int CompletedRuns,
    IReadOnlyList<AgentDeliveryRowDto> Agents);
