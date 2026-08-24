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

    /// <summary>Maximum number of agent loops executing at the same time.</summary>
    public int MaxConcurrentRuns { get; set; } = 2;

    /// <summary>How often the scheduler checks for due agents.</summary>
    public int SchedulerPollSeconds { get; set; } = 10;

    /// <summary>Per-testing-action time limit.</summary>
    public int TestingActionTimeoutSeconds { get; set; } = 120;
}
