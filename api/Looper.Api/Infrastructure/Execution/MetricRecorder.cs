using System.Globalization;
using System.Text.RegularExpressions;
using Looper.Api.Domain;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Infrastructure.Execution;

public sealed record ScriptMetricLine(string Metric, double Value, string? Note);

/// <summary>
/// Turns script output into metric values: any line `@metric name=value [note]` printed by a
/// Script resource (before or after an iteration) is recorded against the matching Metric —
/// the deterministic, token-free way to measure an outcome.
/// </summary>
public sealed partial class MetricRecorder(IDbContextFactory<LooperDbContext> dbFactory, ILogger<MetricRecorder> logger)
{
    public static IReadOnlyList<ScriptMetricLine> ParseScriptOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var lines = new List<ScriptMetricLine>();
        foreach (Match match in MetricLine().Matches(output))
        {
            if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value))
            {
                continue;
            }
            var note = match.Groups["note"].Value.Trim();
            lines.Add(new ScriptMetricLine(match.Groups["name"].Value.Trim(), value, note.Length == 0 ? null : note));
        }
        return lines;
    }

    /// <summary>Records every `@metric` line in the given script results. Unknown names are logged, never fatal.</summary>
    public async Task RecordFromScriptsAsync(LoopAgent agent, IReadOnlyList<Resource> resources, Guid runId,
        IReadOnlyList<TestingActionResult> results, RunLogWriter log)
    {
        var lines = results
            .SelectMany(r => ParseScriptOutput(r.Output).Select(line => (Script: r.Name, Line: line)))
            .ToList();
        if (lines.Count == 0) return;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var attached = resources.Where(MetricResources.IsMetric).ToList();
            List<Resource>? all = null;

            foreach (var (script, line) in lines)
            {
                // Attached metrics first (what the agent was given), then anything else defined.
                var metric = MetricResources.Match(attached, line.Metric);
                if (metric is null)
                {
                    all ??= await db.Resources.AsNoTracking()
                        .Where(r => r.Type == ResourceType.Custom && r.CustomTypeKey == MetricModule.TypeKey_)
                        .ToListAsync();
                    metric = MetricResources.Match(all, line.Metric);
                }
                if (metric is null)
                {
                    await log("warn", $"Script '{script}' reported '@metric {line.Metric}' but no Metric resource matches; ignored.");
                    continue;
                }

                db.MetricValues.Add(new MetricValue
                {
                    ResourceId = metric.Id,
                    AgentId = agent.Id,
                    RunId = runId,
                    Value = line.Value,
                    Note = line.Note ?? $"from script '{script}'",
                    Source = MetricSource.Script
                });
                await log("info", $"Metric '{metric.Name}' = {line.Value.ToString(CultureInfo.InvariantCulture)} recorded from script '{script}'.");
            }
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not record script metrics for run {RunId}", runId);
            await log("warn", $"Recording script metrics failed: {ex.Message}");
        }
    }

    [GeneratedRegex(@"^[ \t]*@metric[ \t]+(?<name>[^=\r\n]+?)[ \t]*=[ \t]*(?<value>[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)(?:[ \t]+(?<note>[^\r\n]*))?[ \t]*$",
        RegexOptions.Multiline)]
    private static partial Regex MetricLine();
}
