namespace Looper.Api.Domain;

public enum MetricSource
{
    /// <summary>Reported by an agent mid-run through the injected protocol.</summary>
    Agent,

    /// <summary>Parsed from a Script resource's output (an `@metric name=value` line).</summary>
    Script,

    /// <summary>Entered by a person on the dashboard.</summary>
    Manual,

    /// <summary>Posted to the API by anything else (a cron job, a webhook relay, …).</summary>
    Api
}

/// <summary>
/// One measurement of a user-defined Metric resource. Metrics are the generic outcome layer:
/// a marketing loop reports sign-ups, a support loop reports resolution time, a coding loop
/// may still report merged PRs — the dashboard treats them all the same way.
/// </summary>
public class MetricValue
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The Metric resource this measurement belongs to.</summary>
    public Guid ResourceId { get; set; }
    public Resource Resource { get; set; } = null!;

    /// <summary>The agent that produced it, when known; kept after the agent is deleted (set null).</summary>
    public Guid? AgentId { get; set; }

    public Guid? RunId { get; set; }

    public double Value { get; set; }

    /// <summary>What was measured and how — the provenance a number needs to be trusted.</summary>
    public string? Note { get; set; }

    public MetricSource Source { get; set; }

    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}
