namespace Looper.Api.Domain;

public enum PrStatus
{
    Open,
    Merged,
    Closed
}

/// <summary>
/// A pull request produced by an agent — the unit the delivery metrics are computed over.
/// Registered by the agent itself during a run (via the injected delivery protocol), logged
/// manually, or both; GitHub-hosted PRs are then kept fresh by the delivery sync service.
/// </summary>
public class AgentPullRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentId { get; set; }
    public LoopAgent Agent { get; set; } = null!;

    /// <summary>The run that opened it, when known.</summary>
    public Guid? RunId { get; set; }

    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string Repository { get; set; } = "";
    public int? Number { get; set; }

    public PrStatus Status { get; set; } = PrStatus.Open;
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? MergedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }

    public int Additions { get; set; }
    public int Deletions { get; set; }

    /// <summary>Review cycles that requested changes — the "slower to accept" signal.</summary>
    public int ReviewRounds { get; set; }
    public int ReviewComments { get; set; }

    /// <summary>Commits without a Claude co-author trailer — humans quietly fixing the work.</summary>
    public int HumanCommits { get; set; }

    /// <summary>Local clone used for the code-survival check.</summary>
    public string? RepoPath { get; set; }
    public string? MergeCommitSha { get; set; }

    /// <summary>Set once the survival window has elapsed and git blame has been consulted.</summary>
    public DateTime? SurvivalCheckedAtUtc { get; set; }
    public int? SurvivingAdditions { get; set; }

    public DateTime? LastSyncedAtUtc { get; set; }
    public string? SyncError { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Merged with zero change-request rounds and zero human commits; null until merged.</summary>
    public bool? FirstPass => Status == PrStatus.Merged ? ReviewRounds == 0 && HumanCommits == 0 : null;

    public double? SurvivalRate =>
        SurvivingAdditions is int surviving && Additions > 0
            ? Math.Min(1.0, surviving / (double)Additions)
            : null;
}
