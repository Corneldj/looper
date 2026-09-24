namespace Looper.Api.Domain;

/// <summary>
/// One application-wide setting, stored as a key/value row so new settings need no schema
/// change. Keys live in <see cref="AppSettingKeys"/>; typed access goes through a feature slice.
/// </summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public static class AppSettingKeys
{
    /// <summary>"Subscription" (the Claude Code login on this machine) or "ApiKey".</summary>
    public const string ClaudeAuthMode = "claude.authMode";

    /// <summary>The Anthropic API key used when the mode is ApiKey. Never returned by the API.</summary>
    public const string ClaudeApiKey = "claude.apiKey";

    /// <summary>Wall-clock limit per run in minutes; "0" means no limit. Absent: Looper:RunTimeoutMinutes from appsettings.</summary>
    public const string RunTimeoutMinutes = "run.timeoutMinutes";

    /// <summary>USD cap applied to runs whose agent sets no budget of its own. Absent: no default cap.</summary>
    public const string DefaultMaxBudgetUsd = "run.defaultMaxBudgetUsd";
}
