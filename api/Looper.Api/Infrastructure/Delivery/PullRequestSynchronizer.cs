using Looper.Api.Domain;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Delivery;

/// <summary>
/// Brings one PR record up to date: GitHub state via gh (when the URL is a GitHub PR and gh is
/// authenticated), then the code-survival measurement once the window after merge has elapsed
/// and a local clone is known. Failures land in SyncError; they never throw.
/// </summary>
public sealed class PullRequestSynchronizer(
    GitHubPrInspector inspector,
    CodeSurvivalCalculator survival,
    IOptions<LooperOptions> options,
    ILogger<PullRequestSynchronizer> logger)
{
    public async Task SyncAsync(AgentPullRequest pr, CancellationToken cancellationToken)
    {
        pr.SyncError = null;

        if (pr.Url is not null && GitHubPrInspector.ParseUrl(pr.Url) is not null
            && await inspector.IsAvailableAsync(cancellationToken))
        {
            try
            {
                var snapshot = await inspector.FetchAsync(pr.Url, cancellationToken);
                if (snapshot is not null) Apply(pr, snapshot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pr.SyncError = ex.Message;
                logger.LogWarning(ex, "GitHub sync failed for PR {Url}", pr.Url);
            }
        }

        await CheckSurvivalAsync(pr, cancellationToken);
        pr.LastSyncedAtUtc = DateTime.UtcNow;
        pr.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void Apply(AgentPullRequest pr, PrSnapshot snapshot)
    {
        pr.Status = snapshot.State switch
        {
            "merged" => PrStatus.Merged,
            "closed" => PrStatus.Closed,
            _ => PrStatus.Open
        };
        if (string.IsNullOrWhiteSpace(pr.Title) || pr.Title != snapshot.Title) pr.Title = snapshot.Title;
        pr.Number = snapshot.Number;
        pr.Additions = snapshot.Additions;
        pr.Deletions = snapshot.Deletions;
        pr.MergedAtUtc = snapshot.MergedAtUtc;
        pr.ClosedAtUtc = snapshot.ClosedAtUtc;
        pr.MergeCommitSha = snapshot.MergeCommitSha ?? pr.MergeCommitSha;
        pr.ReviewRounds = snapshot.ReviewRounds;
        pr.ReviewComments = snapshot.ReviewComments;
        pr.HumanCommits = snapshot.HumanCommits;
    }

    private async Task CheckSurvivalAsync(AgentPullRequest pr, CancellationToken cancellationToken)
    {
        var matured = pr is { Status: PrStatus.Merged, SurvivalCheckedAtUtc: null, MergedAtUtc: not null }
            && pr.MergedAtUtc.Value.AddDays(options.Value.SurvivalWindowDays) <= DateTime.UtcNow;
        if (!matured || pr.RepoPath is null || pr.MergeCommitSha is null) return;

        var result = await survival.ComputeAsync(pr.RepoPath, pr.MergeCommitSha, cancellationToken);
        if (result.Error is not null)
        {
            pr.SyncError = result.Error;
            return;
        }

        pr.SurvivalCheckedAtUtc = DateTime.UtcNow;
        pr.SurvivingAdditions = result.SurvivingAdditions;
        if (pr.Additions == 0) pr.Additions = result.Additions;
    }
}
