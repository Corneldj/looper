using System.Globalization;
using Looper.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// The limits every run starts under: a wall-clock cap and a default spend cap for agents that set
/// none of their own. Both live in Settings and apply to the next run without a restart; appsettings
/// supplies the fallback. Zero minutes means no time limit.
/// </summary>
public sealed record RunLimitSettings(int TimeoutMinutes, decimal? DefaultMaxBudgetUsd, DateTime? UpdatedAtUtc)
{
    public bool HasTimeout => TimeoutMinutes > 0;

    public TimeSpan? Timeout => HasTimeout ? TimeSpan.FromMinutes(TimeoutMinutes) : null;

    /// <summary>One line for the run log, so every run records the limits it actually ran under.</summary>
    public string Describe() =>
        $"time={(HasTimeout ? $"{TimeoutMinutes} min" : "no limit")}, " +
        $"default budget={(DefaultMaxBudgetUsd is { } budget ? $"${budget:0.##}" : "none")}";
}

public static class RunLimits
{
    /// <summary>
    /// The longest delay a <see cref="CancellationTokenSource"/> accepts (0xFFFFFFFE ms, ~49.7 days);
    /// one minute more throws at run start, so every path in clamps to this.
    /// </summary>
    public const int MaxTimeoutMinutes = 71582;

    public static int ClampTimeout(int minutes) => Math.Clamp(minutes, 0, MaxTimeoutMinutes);

    public static async Task<RunLimitSettings> ReadAsync(LooperDbContext db, LooperOptions options, CancellationToken cancellationToken)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.Key == AppSettingKeys.RunTimeoutMinutes || s.Key == AppSettingKeys.DefaultMaxBudgetUsd)
            .ToListAsync(cancellationToken);
        return FromRows(rows, options);
    }

    /// <summary>
    /// A stored value that does not parse is ignored in favour of appsettings — never turned into
    /// an instant timeout or a zero budget. Appsettings at or below zero means no limit.
    /// </summary>
    public static RunLimitSettings FromRows(IReadOnlyList<AppSetting> rows, LooperOptions options)
    {
        var timeoutRow = rows.FirstOrDefault(r => r.Key == AppSettingKeys.RunTimeoutMinutes);
        var budgetRow = rows.FirstOrDefault(r => r.Key == AppSettingKeys.DefaultMaxBudgetUsd);

        var timeout = timeoutRow is not null
                      && int.TryParse(timeoutRow.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var stored)
                      && stored >= 0
            ? stored
            : Math.Max(0, options.RunTimeoutMinutes);

        decimal? budget = budgetRow is not null
                          && decimal.TryParse(budgetRow.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                          && parsed > 0
            ? parsed
            : null;

        var updated = rows.Count == 0 ? (DateTime?)null : rows.Max(r => r.UpdatedAtUtc);
        return new RunLimitSettings(ClampTimeout(timeout), budget, updated);
    }
}
