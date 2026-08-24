namespace Looper.Api.Domain;

public enum RunStatus
{
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public enum RunTrigger
{
    Scheduled,
    Manual
}

/// <summary>One loop iteration of an agent, with full cost/usage accounting.</summary>
public class AgentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AgentId { get; set; }
    public LoopAgent? Agent { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Running;
    public RunTrigger Trigger { get; set; }

    /// <summary>Model the run executed with, captured at trigger time (agents can be reconfigured later).</summary>
    public string Model { get; set; } = "";

    public decimal CostUsd { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public int NumTurns { get; set; }
    public long DurationMs { get; set; }

    public string? ResultText { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Outcome of TestingAction resources executed after the loop, as JSON array.</summary>
    public string? TestResultsJson { get; set; }
    public bool? TestsPassed { get; set; }

    public List<RunLogEntry> Logs { get; set; } = [];
}

public class RunLogEntry
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public AgentRun? Run { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "info";
    public string Message { get; set; } = "";
}
