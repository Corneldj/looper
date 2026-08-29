namespace Looper.Api.Domain;

public enum EventSource
{
    Agent,
    Harness,
    User
}

public enum DeliveryStatus
{
    Pending,
    Delivered,
    Skipped
}

/// <summary>
/// A named event on the bus. Topics are deterministic dotted keys (e.g. "prd.approved",
/// "agent.docs-gardener.succeeded"); payloads are free text handed to listeners verbatim.
/// The harness raises agent.&lt;name-slug&gt;.succeeded/failed automatically after every run,
/// so agents can chain off each other without any model cooperation.
/// </summary>
public class LooperEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Topic { get; set; } = "";
    public string Payload { get; set; } = "";

    public EventSource Source { get; set; }
    public Guid? SourceAgentId { get; set; }
    public Guid? SourceRunId { get; set; }

    /// <summary>
    /// Chain depth: 0 for user/scheduled origins; harness events from an event-triggered run
    /// carry the triggering depth + 1. The dispatcher stops matching past the cap — the
    /// deterministic brake against event cycles (A→B→A…).
    /// </summary>
    public int ChainDepth { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>One matched listener for one event: the auditable unit of event delivery.</summary>
public class EventDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EventId { get; set; }
    public LooperEvent Event { get; set; } = null!;

    public Guid AgentId { get; set; }
    public LoopAgent Agent { get; set; } = null!;

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;

    /// <summary>The run the event was handed to, once delivered.</summary>
    public Guid? RunId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAtUtc { get; set; }
}
