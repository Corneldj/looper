using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Looper.Api.Domain;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Infrastructure.Boards;

/// <summary>A one-off prompt a run took: enough to put it back if the run never reached the model.</summary>
public sealed record TakenPrompt(Guid ResourceId, string Name, string Text);

/// <summary>What the board resources hand a run before it starts: its prompt sections, or the reason it must not start.</summary>
public sealed record BoardBriefing(IReadOnlyList<string> Sections, IReadOnlyList<TakenPrompt> Taken, string? Refusal)
{
    public static readonly BoardBriefing None = new([], [], null);
}

/// <summary>
/// The harness side of the board resources — Azure DevOps tickets, Jira time tracking and the
/// one-off prompt — run by the coordinator around every real run, never by the model. Before a
/// run it reads the selected tickets fresh and takes (clearing) any one-off prompt; a ticket that
/// cannot be read refuses the run before any tokens are spent. After a run it books the run's time
/// in Tempo and, where asked, clears the ticket selection. After-run trouble is logged, never
/// fatal: the run's verdict stands either way.
/// </summary>
public sealed class BoardHarness(
    IDbContextFactory<LooperDbContext> dbFactory,
    IHttpClientFactory httpClients,
    ILogger<BoardHarness> logger)
{
    /// <summary>Tempo keeps local wall-clock times: this machine's zone, as for Ticket Filler. Tests pin it.</summary>
    internal TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    private AzureDevOpsClient Azure() => new(httpClients.CreateClient(AzureDevOpsClient.HttpClientName));

    private JiraTempoClient Jira() => new(httpClients.CreateClient(JiraTempoClient.HttpClientName));

    // ---------- before the run ----------

    public async Task<BoardBriefing> PrepareAsync(IReadOnlyList<Resource> resources, RunLogWriter log, CancellationToken cancellationToken)
    {
        var sections = new List<string>();
        foreach (var resource in AzureDevOpsTicketsResources.Attached(resources))
        {
            var config = AzureDevOpsTicketsResources.Parse(resource);
            if (!config.HasConnection)
            {
                return Refuse($"'{resource.Name}' has no organization, project or personal access token — fill them in on the resource.");
            }
            if (config.TicketIds.Count == 0)
            {
                return Refuse($"'{resource.Name}' has no tickets selected — open it and tick the tickets to work on.");
            }

            IReadOnlyList<WorkItem?> items;
            try
            {
                items = await Azure().GetAsync(config.Connection, config.TicketIds, cancellationToken);
            }
            catch (AzureDevOpsException ex)
            {
                return Refuse($"The tickets of '{resource.Name}' could not be read: {ex.Message}");
            }

            var missing = config.TicketIds.Where((_, index) => items[index] is null).ToList();
            if (missing.Count > 0)
            {
                return Refuse($"'{resource.Name}': {Refs(missing)} could not be found — deleted, moved to another project, or not " +
                              "visible to the token. Update the selection.");
            }

            var found = items.OfType<WorkItem>().ToList();
            await log("info", $"Tickets from '{resource.Name}': " +
                              string.Join("; ", found.Select(i => $"#{i.Id} {i.Title} ({i.Type}, {i.State})")) + ".");
            sections.Add(AzureDevOpsTicketsResources.PromptSection(resource.Name, found));
        }

        // Taken last: a run refused above never started, so its one-off prompt stays for the next one.
        var taken = new List<TakenPrompt>();
        foreach (var resource in OneOffPromptResources.Attached(resources))
        {
            if (await TakeAsync(resource.Id, cancellationToken) is not { } text) continue;
            taken.Add(new TakenPrompt(resource.Id, resource.Name, text));
            await log("info", $"One-off prompt '{resource.Name}' taken for this run and cleared:\n{text}");
        }
        if (taken.Count > 0) sections.Add(OneOffPromptResources.PromptSection(taken.Select(t => (t.Name, t.Text)).ToList()));

        return new BoardBriefing(sections, taken, null);
    }

    /// <summary>
    /// A run that stopped before the model started (a resource that would not apply, a missing
    /// CLI) did not use its one-off prompts: they go back, unless the user already wrote new ones.
    /// </summary>
    public async Task ReturnPromptsAsync(BoardBriefing briefing, RunLogWriter log)
    {
        foreach (var prompt in briefing.Taken)
        {
            var restored = await RewriteAsync(prompt.ResourceId, stored =>
                OneOffPromptResources.Text(stored).Length == 0 ? WithValue(stored, "text", prompt.Text) : null, CancellationToken.None);
            await log("info", restored
                ? $"One-off prompt '{prompt.Name}' put back: the run stopped before the model started."
                : $"One-off prompt '{prompt.Name}' was not put back: it has been rewritten since this run took it.");
        }
    }

    /// <summary>A dry run reads nothing and books nothing: it says what a real run would do, and leaves one-off prompts waiting.</summary>
    public async Task RehearseAsync(IReadOnlyList<Resource> resources, RunLogWriter log)
    {
        foreach (var resource in AzureDevOpsTicketsResources.Attached(resources))
        {
            var ids = AzureDevOpsTicketsResources.Parse(resource).TicketIds;
            await log(ids.Count > 0 ? "info" : "warn", ids.Count > 0
                ? $"[dry run] Tickets not read: a real run would work on {Refs(ids)} from '{resource.Name}'."
                : $"[dry run] '{resource.Name}' has no tickets selected — a real run would be refused.");
        }
        foreach (var resource in OneOffPromptResources.Attached(resources).Where(r => OneOffPromptResources.Text(r.ConfigJson).Length > 0))
        {
            await log("info", $"[dry run] One-off prompt '{resource.Name}' kept for the next real run.");
        }
        if (JiraTimeTrackingResources.Attached(resources).Count > 0)
        {
            await log("info", "[dry run] No time booked to Tempo.");
        }
    }

    // ---------- after the run ----------

    /// <summary>Books the finished run's time and clears selections that asked for it. Reads the run as it was finalized.</summary>
    public async Task AfterRunAsync(LoopAgent agent, IReadOnlyList<Resource> resources, Guid runId, RunLogWriter log)
    {
        try
        {
            AgentRun? run;
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
            }
            if (run?.CompletedAtUtc is null) return;

            await BookTimeAsync(agent, resources, run, log);
            if (run.Status == RunStatus.Succeeded) await ClearSelectionsAsync(resources, log);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Board follow-up for run {RunId} failed", runId);
            await log("warn", $"Board follow-up failed: {ex.Message}");
        }
    }

    /// <summary>
    /// One worklog per Jira resource: the run's wall-clock span on the 5-minute grid, tagged with
    /// the first selected ticket, placed around worklogs already in the timesheet — Ticket Filler's
    /// rules. A run that never reached the model did no work worth booking.
    /// </summary>
    private async Task BookTimeAsync(LoopAgent agent, IReadOnlyList<Resource> resources, AgentRun run, RunLogWriter log)
    {
        var trackers = JiraTimeTrackingResources.Attached(resources)
            .Select(r => (Resource: r, Config: JiraTimeTrackingResources.Parse(r)))
            .Where(t => t.Config.Booking != TimeBooking.Never)
            .ToList();
        if (trackers.Count == 0) return;
        if (run.NumTurns == 0)
        {
            await log("info", "Tempo: nothing booked — the run stopped before the model started.");
            return;
        }

        var tickets = AzureDevOpsTicketsResources.SelectedIds(resources);
        var reference = tickets.Count > 0 ? tickets[0].ToString(CultureInfo.InvariantCulture) : null;
        var block = TempoTime.Block(ToLocal(run.StartedAtUtc), ToLocal(run.CompletedAtUtc!.Value));
        var subject = tickets.Count > 0 ? string.Join(' ', tickets) : agent.Name;

        foreach (var (resource, config) in trackers)
        {
            if (config.Booking == TimeBooking.SuccessfulRuns && run.Status != RunStatus.Succeeded)
            {
                await log("info", $"Tempo: nothing booked on '{resource.Name}' — it books successful runs only.");
                continue;
            }
            if (!config.HasConnection || config.IssueKey.Length == 0)
            {
                await log("warn", $"Tempo: nothing booked on '{resource.Name}' — it has no Jira address, token or issue.");
                continue;
            }

            try
            {
                var jira = Jira();
                var worker = await jira.GetWorkerKeyAsync(config.Connection, CancellationToken.None);
                var busy = await jira.SearchWorklogsAsync(config.Connection, worker,
                    DateOnly.FromDateTime(block.Start), DateOnly.FromDateTime(block.End), CancellationToken.None);
                var pieces = TempoTime.AroundBusy(block, busy);
                if (pieces.Count == 0)
                {
                    await log("warn", $"Tempo: nothing booked to {config.IssueKey} — {Span(block)} is already covered by your timesheet.");
                    continue;
                }

                var text = TempoTime.Comment($"{TempoTime.Lead(config.Activity)} {subject}");
                foreach (var piece in pieces)
                {
                    var seconds = (int)(piece.End - piece.Start).TotalSeconds;
                    await jira.CreateWorklogAsync(config.Connection, worker,
                        new TempoWorklog(config.IssueKey, config.Activity, reference, text, piece.Start, seconds), CancellationToken.None);
                    await log("info", $"Tempo: booked {config.Activity} {seconds / 60}m ({Span(piece)}) to {config.IssueLabel}" +
                                      (reference is null ? "." : $", DevOps reference #{reference}."));
                }
                if (pieces.Count > 1 || pieces[0] != block)
                {
                    await log("info", $"Tempo: {Span(block)} overlapped worklogs already in your timesheet; only the free time was booked.");
                }
            }
            catch (JiraException ex)
            {
                logger.LogWarning("Tempo booking for run {RunId} on {Resource} failed: {Message}", run.Id, resource.Name, ex.Message);
                await log("warn", $"Tempo: booking on '{resource.Name}' failed — {ex.Message} Book {Span(block)} by hand.");
            }
        }
    }

    /// <summary>Clears the selections that asked for it — but only the selection this run worked on; one the user has changed since is theirs.</summary>
    private async Task ClearSelectionsAsync(IReadOnlyList<Resource> resources, RunLogWriter log)
    {
        foreach (var resource in AzureDevOpsTicketsResources.Attached(resources))
        {
            var worked = AzureDevOpsTicketsResources.Parse(resource);
            if (!worked.ClearAfterSuccess || worked.TicketIds.Count == 0) continue;

            var cleared = await RewriteAsync(resource.Id, stored =>
                AzureDevOpsTicketsResources.ParseIds(new ResourceModuleContext(stored).GetString("ticketIds")).Ids.SequenceEqual(worked.TicketIds)
                    ? WithValue(stored, "ticketIds", "")
                    : null, CancellationToken.None);
            if (cleared)
            {
                await log("info", $"Selection on '{resource.Name}' cleared after the successful run ({Refs(worked.TicketIds)}).");
            }
        }
    }

    // ---------- config rewrites ----------

    /// <summary>Takes a one-off prompt's text and clears it in one step; null when there was nothing waiting.</summary>
    private async Task<string?> TakeAsync(Guid resourceId, CancellationToken cancellationToken)
    {
        string? text = null;
        var written = await RewriteAsync(resourceId, stored =>
        {
            text = OneOffPromptResources.Text(stored);
            return text.Length > 0 ? WithValue(stored, "text", "") : null;
        }, cancellationToken);
        return written ? text : null;
    }

    /// <summary>
    /// Rewrites a resource's config, but only while it still reads the way the rewrite saw it: a
    /// compare-and-swap on the stored JSON. A user saving the form, or a second run taking the same
    /// prompt at the same instant, wins or loses cleanly instead of being overwritten. True when
    /// this call's write landed; false when the rewrite declined or the resource is gone.
    /// </summary>
    private async Task<bool> RewriteAsync(Guid resourceId, Func<string, string?> rewrite, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var stored = await db.Resources.AsNoTracking()
                .Where(r => r.Id == resourceId)
                .Select(r => r.ConfigJson)
                .FirstOrDefaultAsync(cancellationToken);
            if (stored is null || rewrite(stored) is not { } next) return false;

            var written = await db.Resources
                .Where(r => r.Id == resourceId && r.ConfigJson == stored)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.ConfigJson, next)
                    .SetProperty(r => r.UpdatedAtUtc, DateTime.UtcNow), cancellationToken);
            if (written > 0) return true;
        }
        return false;
    }

    /// <summary>The same config with one key set; every other key, and the key's own spelling, kept as it was.</summary>
    internal static string WithValue(string configJson, string key, string value)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }
        var existing = root.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        root[existing ?? key] = value;
        return root.ToJsonString();
    }

    // ---------- formatting ----------

    private static BoardBriefing Refuse(string reason) => new([], [], reason);

    private DateTime ToLocal(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);

    private static string Refs(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => $"#{id}"));

    private static string Span((DateTime Start, DateTime End) span) =>
        $"{span.Start.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}–{span.End.ToString("HH:mm", CultureInfo.InvariantCulture)}";
}
