namespace Looper.Api.Domain;

public enum UserActionStatus
{
    Open,
    Resolved
}

/// <summary>
/// A request the agent raises for the human: something only the user can do or decide before
/// the loop should continue. Deliberately not a failure — the run that raises it still
/// succeeds; the schedule simply parks until the request is resolved, and the user's
/// response is handed to the next iteration as context.
/// </summary>
public class UserActionRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AgentId { get; set; }
    public LoopAgent Agent { get; set; } = null!;

    /// <summary>The run that raised it, when known.</summary>
    public Guid? RunId { get; set; }

    public string Title { get; set; } = "";
    public string Details { get; set; } = "";

    public UserActionStatus Status { get; set; } = UserActionStatus.Open;

    /// <summary>Whether this request parks the agent's schedule until resolved.</summary>
    public bool Blocking { get; set; } = true;

    /// <summary>The user's answer, passed to the next run after resolution.</summary>
    public string? Response { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAtUtc { get; set; }

    /// <summary>Set once the response has been injected into a real run's prompt.</summary>
    public DateTime? ResponseDeliveredAtUtc { get; set; }
}
