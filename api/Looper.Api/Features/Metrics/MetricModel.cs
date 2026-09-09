using Looper.Api.Domain;
using Looper.Api.Modules.BuiltIn;

namespace Looper.Api.Features.Metrics;

public sealed record MetricValueDto(
    Guid Id,
    Guid ResourceId,
    Guid? AgentId,
    string? AgentName,
    Guid? RunId,
    double Value,
    string? Note,
    MetricSource Source,
    DateTime RecordedAtUtc);

public sealed record MetricPointDto(string Date, double Value);

/// <summary>A metric as the dashboard shows it: definition, current reading, trend, sparkline, who reports it.</summary>
public sealed record MetricSummaryDto(
    Guid ResourceId,
    string Name,
    string Description,
    string Unit,
    MetricAggregation Aggregation,
    MetricDirection Direction,
    double? Target,
    double? Latest,
    DateTime? LatestAtUtc,
    /// <summary>The window's reading per the aggregation (latest overall / sum / average). Null with no data.</summary>
    double? Current,
    /// <summary>The same reading for the preceding window, for the trend. Null without a baseline.</summary>
    double? Previous,
    double? TrendPct,
    int CountInWindow,
    int TotalCount,
    IReadOnlyList<MetricPointDto> Series,
    IReadOnlyList<string> Agents);

public static class MetricMapper
{
    public static MetricValueDto ToDto(this MetricValue value, string? agentName) => new(
        value.Id, value.ResourceId, value.AgentId, agentName, value.RunId, value.Value, value.Note, value.Source, value.RecordedAtUtc);
}

/// <summary>
/// The pure arithmetic behind a metric summary. Values arrive in chronological order; the
/// window is (now - days, now] and the baseline is the window before it.
/// </summary>
public static class MetricMath
{
    public readonly record struct Sample(double Value, DateTime AtUtc);

    public sealed record Summary(
        double? Latest,
        DateTime? LatestAtUtc,
        double? Current,
        double? Previous,
        double? TrendPct,
        int CountInWindow,
        IReadOnlyList<MetricPointDto> Series);

    public static Summary Summarize(MetricConfig config, IReadOnlyList<Sample> ordered, DateTime nowUtc, int days)
    {
        var since = nowUtc.AddDays(-days);
        var baselineSince = since.AddDays(-days);

        var window = ordered.Where(s => s.AtUtc >= since).ToList();
        var baseline = ordered.Where(s => s.AtUtc >= baselineSince && s.AtUtc < since).ToList();
        var last = ordered.Count > 0 ? ordered[^1] : (Sample?)null;

        double? current, previous;
        switch (config.Aggregation)
        {
            case MetricAggregation.Sum:
                current = window.Count > 0 ? window.Sum(s => s.Value) : null;
                previous = baseline.Count > 0 ? baseline.Sum(s => s.Value) : null;
                break;
            case MetricAggregation.Average:
                current = window.Count > 0 ? window.Average(s => s.Value) : null;
                previous = baseline.Count > 0 ? baseline.Average(s => s.Value) : null;
                break;
            default:
                // A gauge: the newest reading is the truth regardless of window; the baseline is
                // where the gauge stood when the window opened.
                current = last?.Value;
                var before = ordered.LastOrDefault(s => s.AtUtc < since);
                previous = window.Count > 0 && before.AtUtc != default ? before.Value : null;
                break;
        }

        double? trend = current is { } c && previous is { } p && p != 0 ? (c - p) / Math.Abs(p) * 100 : null;

        var series = window
            .GroupBy(s => s.AtUtc.ToString("yyyy-MM-dd"))
            .OrderBy(g => g.Key)
            .Select(g => new MetricPointDto(g.Key, config.Aggregation switch
            {
                MetricAggregation.Sum => g.Sum(s => s.Value),
                MetricAggregation.Average => g.Average(s => s.Value),
                _ => g.Last().Value
            }))
            .ToList();

        return new Summary(last?.Value, last?.AtUtc, current, previous, trend, window.Count, series);
    }
}
