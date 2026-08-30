namespace Looper.Api.Infrastructure;

public class LooperOptions
{
    public const string SectionName = "Looper";

    /// <summary>Executable used for real runs; must be on PATH or an absolute path.</summary>
    public string ClaudeCommand { get; set; } = "claude";

    /// <summary>Shell command the "Install for me" button runs — the official Claude Code installer.</summary>
    public string ClaudeInstallCommand { get; set; } = "curl -fsSL https://claude.ai/install.sh | bash";

    /// <summary>Hard wall-clock limit per run.</summary>
    public int RunTimeoutMinutes { get; set; } = 30;

    /// <summary>Base URL agents use to report PRs and escalations back to Looper during a run.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5210";

    /// <summary>How often the delivery sync refreshes GitHub PR state and runs survival checks.</summary>
    public int DeliverySyncMinutes { get; set; } = 15;

    /// <summary>Days after merge before code survival is measured.</summary>
    public int SurvivalWindowDays { get; set; } = 14;

    /// <summary>Maximum number of agent loops executing at the same time.</summary>
    public int MaxConcurrentRuns { get; set; } = 2;

    /// <summary>How often the scheduler checks for due agents.</summary>
    public int SchedulerPollSeconds { get; set; } = 10;

    /// <summary>Per-testing-action time limit.</summary>
    public int TestingActionTimeoutSeconds { get; set; } = 120;

    /// <summary>Interpreter used for the graph toolkit (memory preambles, health reports).</summary>
    public string PythonCommand { get; set; } = "python3";

    /// <summary>How often graph health is measured and needs-curation events are raised.</summary>
    public int GraphMaintenanceMinutes { get; set; } = 30;
}
