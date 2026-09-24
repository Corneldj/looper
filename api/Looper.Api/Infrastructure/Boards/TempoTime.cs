using System.Globalization;
using System.Text.RegularExpressions;

namespace Looper.Api.Infrastructure.Boards;

/// <summary>
/// Time-booking arithmetic, the way Ticket Filler books a timesheet: blocks sit on the 5-minute
/// grid, last at least 5 minutes, and are carved around worklogs already in the timesheet rather
/// than double-booked. All times are local wall-clock times — Tempo's own frame.
/// </summary>
public static partial class TempoTime
{
    public static readonly TimeSpan Grid = TimeSpan.FromMinutes(5);

    public static DateTime RoundToGrid(DateTime time) => new(Round(time.Ticks), time.Kind);

    public static TimeSpan RoundToGrid(TimeSpan span) => TimeSpan.FromTicks(Round(span.Ticks));

    private static long Round(long ticks) =>
        (long)Math.Round(ticks / (double)Grid.Ticks, MidpointRounding.AwayFromZero) * Grid.Ticks;

    /// <summary>A run's span as one bookable block: start and length each on the grid, at least one grid step long.</summary>
    public static (DateTime Start, DateTime End) Block(DateTime start, DateTime end)
    {
        var from = RoundToGrid(start);
        var length = RoundToGrid(end - start);
        if (length < Grid) length = Grid;
        return (from, from + length);
    }

    /// <summary>
    /// What is left of a block once existing worklogs are taken out: a block that runs into one is
    /// shortened, one that spans past one is split around it, and pieces under 5 minutes are dropped.
    /// Edges that meet a worklog keep its exact boundary; lengths are whole minutes.
    /// </summary>
    public static IReadOnlyList<(DateTime Start, DateTime End)> AroundBusy(
        (DateTime Start, DateTime End) block, IEnumerable<(DateTime Start, DateTime End)> busy)
    {
        var pieces = new List<(DateTime Start, DateTime End)> { block };
        foreach (var (busyStart, busyEnd) in busy.Where(b => b.End > b.Start).OrderBy(b => b.Start))
        {
            var next = new List<(DateTime Start, DateTime End)>();
            foreach (var (start, end) in pieces)
            {
                if (busyEnd <= start || busyStart >= end)
                {
                    next.Add((start, end));
                    continue;
                }
                if (busyStart > start) next.Add((start, busyStart));
                if (busyEnd < end) next.Add((busyEnd, end));
            }
            pieces = next;
        }

        return pieces
            .Select(p => (p.Start, End: p.Start.AddMinutes(Math.Floor((p.End - p.Start).TotalMinutes))))
            .Where(p => p.End - p.Start >= Grid)
            .ToList();
    }

    /// <summary>"yyyy-MM-dd HH:mm:ss.000" local time, as Tempo expects.</summary>
    public static string FormatStarted(DateTime local) =>
        local.ToString("yyyy-MM-dd HH:mm:ss.000", CultureInfo.InvariantCulture);

    /// <summary>The words a worklog description opens with, per activity — Ticket Filler's wording where it has one.</summary>
    public static string Lead(string activity) => activity switch
    {
        "Developing" => "Development work on",
        "Analysis" => "Analysis and setup for",
        "Test" => "Testing and review of",
        "CodeReview" => "Code review for",
        "Admin" => "Admin and cleanup for",
        "Meeting" => "Meeting about",
        "Support" => "Support for",
        _ => "Work on"
    };

    /// <summary>Worklog descriptions stay simple: letters, digits and basic punctuation, at most 250 characters.</summary>
    public static string Comment(string text)
    {
        var cleaned = WhitespaceRegex().Replace(UnsafeRegex().Replace(text, " "), " ").Trim();
        return cleaned.Length > 250 ? cleaned[..250].TrimEnd() : cleaned;
    }

    [GeneratedRegex(@"[^A-Za-z0-9 .,\-]")]
    private static partial Regex UnsafeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
