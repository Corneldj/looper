namespace Looper.Api.Domain;

public enum EffortLevel
{
    Low,
    Medium,
    High,
    XHigh,
    Max
}

/// <summary>An agent definition that runs on a recurring loop via the Claude Agent SDK (headless CLI).</summary>
public class LoopAgent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>The task prompt executed on every loop iteration.</summary>
    public string Prompt { get; set; } = "";

    public string Model { get; set; } = "claude-opus-5";
    public EffortLevel Effort { get; set; } = EffortLevel.High;

    public int IntervalMinutes { get; set; } = 60;
    public bool Enabled { get; set; }

    /// <summary>
    /// Autonomy is a dial, not a switch: 1 proposes only, 2 executes sandboxed with diff approval,
    /// 3 executes autonomously with after-the-fact review, 4 fully autonomous with sampled audits.
    /// Promote on first-pass success and escalation evidence; demote as readily.
    /// </summary>
    public int AutonomyLevel { get; set; } = 3;

    /// <summary>When true, runs use the simulated executor: no tokens are spent.</summary>
    public bool DryRun { get; set; }

    public int MaxTurns { get; set; } = 25;

    /// <summary>Per-run spending cap passed to the CLI as --max-budget-usd. Null = uncapped.</summary>
    public decimal? MaxBudgetUsd { get; set; }

    /// <summary>Overrides the working directory; otherwise the first FileLocation resource is used.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Comma-separated tool allowlist passed as --allowedTools.</summary>
    public string? AllowedTools { get; set; }

    /// <summary>When true the run is fully autonomous (--dangerously-skip-permissions equivalent).</summary>
    public bool BypassPermissions { get; set; } = true;

    public DateTime? NextRunAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<Resource> Resources { get; set; } = [];
    public List<AgentRun> Runs { get; set; } = [];
}
