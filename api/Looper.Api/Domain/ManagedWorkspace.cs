namespace Looper.Api.Domain;

public enum WorkspaceStatus
{
    Active,
    Done,

    /// <summary>The directory has been removed; the record stays as history.</summary>
    Cleaned
}

/// <summary>
/// A dedicated workspace provisioned for one unit of work from a Dynamic Workspaces pool.
/// Agents claim one per unit (idempotently, by slug), work in it across iterations with a
/// WORKBRIEF.md carrying the context, mark it done, and the janitor cleans it up after the
/// pool's retention window.
/// </summary>
public class ManagedWorkspace
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The WorkspacePool resource this belongs to.</summary>
    public Guid ResourceId { get; set; }
    public Resource Resource { get; set; } = null!;

    /// <summary>Creator, when known.</summary>
    public Guid? AgentId { get; set; }
    public Guid? RunId { get; set; }

    /// <summary>Slugged unit-of-work name; unique per pool among non-cleaned workspaces.</summary>
    public string Unit { get; set; } = "";

    public string Path { get; set; } = "";
    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Active;

    /// <summary>The context brief the workspace was seeded with.</summary>
    public string ContextBrief { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DoneAtUtc { get; set; }
    public DateTime? CleanedAtUtc { get; set; }
}
