using System.Diagnostics;
using Looper.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>How the Claude Code CLI authenticates when Looper launches it.</summary>
public enum ClaudeAuthMode
{
    /// <summary>The Claude Code login on this machine (run `claude` once and sign in). The default.</summary>
    Subscription,

    /// <summary>An Anthropic API key stored in Looper's settings; usage is billed to that key.</summary>
    ApiKey
}

public sealed record ClaudeAuthSettings(ClaudeAuthMode Mode, string? ApiKey, DateTime? UpdatedAtUtc)
{
    public static readonly ClaudeAuthSettings Default = new(ClaudeAuthMode.Subscription, null, null);

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>The last four characters of the stored key, for display ("…a1b2"). Never the key.</summary>
    public string? ApiKeyHint => HasApiKey ? ClaudeAuth.Hint(ApiKey!) : null;
}

/// <summary>Thrown when the settings cannot be honoured — the launch must not proceed on a guess.</summary>
public sealed class ClaudeAuthException(string message) : Exception(message);

/// <summary>
/// The one place that decides which credentials a spawned `claude` process sees. The CLI resolves
/// credentials in a fixed order and an <c>ANTHROPIC_API_KEY</c> in the environment shadows the
/// subscription login, so subscription mode strips the variable from the child environment and
/// API-key mode sets it — every launch site applies this, none of them guesses.
/// </summary>
public static class ClaudeAuth
{
    public const string ApiKeyVariable = "ANTHROPIC_API_KEY";

    /// <summary>Anthropic Console keys start with this; anything else is almost certainly a paste mistake.</summary>
    public const string ApiKeyPrefix = "sk-ant-";

    public static string Hint(string apiKey) => "…" + apiKey[^Math.Min(4, apiKey.Length)..];

    /// <summary>Returns null when the key looks like an Anthropic API key, otherwise the reason it does not.</summary>
    public static string? ValidateApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return "Enter an API key.";
        if (apiKey.Any(char.IsWhiteSpace)) return "The API key must not contain spaces or line breaks.";
        if (!apiKey.StartsWith(ApiKeyPrefix, StringComparison.Ordinal))
        {
            return $"That does not look like an Anthropic API key — keys from console.anthropic.com start with '{ApiKeyPrefix}'.";
        }
        if (apiKey.Length < 20) return "The API key is too short.";
        return null;
    }

    /// <summary>
    /// Applies the settings to a process about to be launched and returns a one-line description
    /// for the run log. Fails closed: API-key mode without a stored key is an error, not a fallback.
    /// </summary>
    public static string Apply(ProcessStartInfo startInfo, ClaudeAuthSettings settings)
    {
        switch (settings.Mode)
        {
            case ClaudeAuthMode.ApiKey:
                if (!settings.HasApiKey)
                {
                    throw new ClaudeAuthException(
                        "Settings select an API key for Claude, but no key is stored. Paste one in Settings or switch back to the Claude Code subscription.");
                }
                startInfo.Environment[ApiKeyVariable] = settings.ApiKey!.Trim();
                return $"API key ({settings.ApiKeyHint})";

            case ClaudeAuthMode.Subscription:
                // An inherited key would silently bill the API instead of the subscription the settings promise.
                startInfo.Environment.Remove(ApiKeyVariable);
                return "Claude Code subscription";

            default:
                throw new ClaudeAuthException($"Unknown Claude auth mode '{settings.Mode}'.");
        }
    }
}

/// <summary>Reads the stored auth settings fresh for every launch — no cache to go stale after a save.</summary>
public sealed class ClaudeAuthProvider(IDbContextFactory<LooperDbContext> dbFactory)
{
    public async Task<ClaudeAuthSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ReadAsync(db, cancellationToken);
    }

    public static async Task<ClaudeAuthSettings> ReadAsync(LooperDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.ClaudeAuthMode || s.Key == AppSettingKeys.ClaudeApiKey)
            .ToListAsync(cancellationToken);
        var modeRow = rows.FirstOrDefault(r => r.Key == AppSettingKeys.ClaudeAuthMode);
        var keyRow = rows.FirstOrDefault(r => r.Key == AppSettingKeys.ClaudeApiKey);
        var mode = modeRow is not null && Enum.TryParse<ClaudeAuthMode>(modeRow.Value, ignoreCase: true, out var parsed)
            ? parsed
            : ClaudeAuthMode.Subscription;
        var updated = rows.Count == 0 ? (DateTime?)null : rows.Max(r => r.UpdatedAtUtc);
        return new ClaudeAuthSettings(mode, string.IsNullOrWhiteSpace(keyRow?.Value) ? null : keyRow!.Value, updated);
    }

    /// <summary>Applies the stored settings to a launch; see <see cref="ClaudeAuth.Apply"/>.</summary>
    public async Task<string> ApplyAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken) =>
        ClaudeAuth.Apply(startInfo, await GetAsync(cancellationToken));
}
