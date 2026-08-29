using System.Text.RegularExpressions;
using Looper.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// The deterministic event bus. Raising writes an event row and fans it out into one
/// EventDelivery row per matched listener; the pump (called after a raise and on every
/// scheduler tick) hands pending deliveries to their agents as event-triggered runs,
/// batching everything pending for an agent into one run. Matching is exact-or-prefix
/// only, and a chain-depth cap brakes cycles.
/// </summary>
public sealed partial class EventDispatcher(ILogger<EventDispatcher> logger)
{
    public const int MaxChainDepth = 5;

    // ---------- topics ----------

    /// <summary>Topics: dotted lowercase segments (letters, digits, dashes). No wildcards when raising.</summary>
    public static bool IsValidTopic(string topic) => TopicPattern().IsMatch(topic);

    /// <summary>Listen patterns: a valid topic, or a topic prefix ending in ".*".</summary>
    public static bool IsValidPattern(string pattern) =>
        pattern.EndsWith(".*", StringComparison.Ordinal)
            ? IsValidTopic(pattern[..^2])
            : IsValidTopic(pattern);

    /// <summary>Deterministic match: exact, or prefix when the pattern ends in ".*".</summary>
    public static bool Matches(string pattern, string topic) =>
        pattern.EndsWith(".*", StringComparison.Ordinal)
            ? topic.StartsWith(pattern[..^1], StringComparison.Ordinal) // keeps the trailing dot
            : string.Equals(pattern, topic, StringComparison.Ordinal);

    public static IReadOnlyList<string> ParsePatterns(string? triggerTopics) =>
        (triggerTopics ?? "")
            .Split(['\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>agent.&lt;name-slug&gt;.&lt;succeeded|failed&gt; — the stable key other loops chain off.</summary>
    public static string CompletionTopic(string agentName, bool succeeded)
    {
        var slug = SlugPattern().Replace(agentName.Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0) slug = "agent";
        return $"agent.{slug}.{(succeeded ? "succeeded" : "failed")}";
    }

    // ---------- raise ----------

    /// <summary>Writes the event and its deliveries. Does not start runs — the pump does.</summary>
    public async Task<LooperEvent> RaiseAsync(
        LooperDbContext db,
        string topic,
        string payload,
        EventSource source,
        Guid? sourceAgentId,
        Guid? sourceRunId,
        int chainDepth,
        CancellationToken cancellationToken)
    {
        var evt = new LooperEvent
        {
            Topic = topic,
            Payload = payload,
            Source = source,
            SourceAgentId = sourceAgentId,
            SourceRunId = sourceRunId,
            ChainDepth = chainDepth,
        };
        db.Events.Add(evt);

        if (chainDepth >= MaxChainDepth)
        {
            logger.LogWarning("Event {Topic} at chain depth {Depth} — not matched to listeners (cycle brake)",
                topic, chainDepth);
        }
        else
        {
            var listeners = await db.Agents.AsNoTracking()
                .Where(a => a.Enabled && a.TriggerMode == TriggerMode.Event && a.TriggerTopics != null)
                .Where(a => sourceAgentId == null || a.Id != sourceAgentId) // never trigger yourself
                .Select(a => new { a.Id, a.TriggerTopics })
                .ToListAsync(cancellationToken);

            foreach (var listener in listeners)
            {
                if (ParsePatterns(listener.TriggerTopics).Any(p => Matches(p, topic)))
                {
                    db.EventDeliveries.Add(new EventDelivery { EventId = evt.Id, AgentId = listener.Id });
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return evt;
    }

    // ---------- pump ----------

    /// <summary>
    /// Starts runs for agents with pending deliveries: all pending events for an agent are
    /// batched into one event-triggered run. Skips busy, disabled, or user-action-parked
    /// agents — their deliveries stay pending for the next pump.
    /// </summary>
    public async Task PumpAsync(LooperDbContext db, AgentRunCoordinator coordinator, CancellationToken cancellationToken)
    {
        var pendingByAgent = await db.EventDeliveries
            .Where(d => d.Status == DeliveryStatus.Pending)
            .Include(d => d.Event)
            .GroupBy(d => d.AgentId)
            .ToListAsync(cancellationToken);

        foreach (var group in pendingByAgent)
        {
            var agentId = group.Key;
            if (coordinator.IsRunning(agentId)) continue;

            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
            if (agent is null || !agent.Enabled || agent.TriggerMode != TriggerMode.Event)
            {
                // No longer a listener — these deliveries will never be wanted.
                foreach (var delivery in group) delivery.Status = DeliveryStatus.Skipped;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            if (await Features.UserActions.UserActionGate.IsBlockedAsync(db, agentId, cancellationToken)) continue;

            var deliveries = group.OrderBy(d => d.Event.CreatedAtUtc).ToList();
            var depth = deliveries.Max(d => d.Event.ChainDepth) + 1;
            var context = string.Join("\n\n", deliveries.Select(d =>
                $"[{d.Event.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC] {d.Event.Topic}"
                + (string.IsNullOrWhiteSpace(d.Event.Payload) ? "" : $"\n{d.Event.Payload}")));

            var runId = await coordinator.TriggerRunAsync(
                agentId, RunTrigger.Event, cancellationToken, eventContext: context, eventDepth: depth);
            if (runId is null) continue; // raced busy — stays pending

            foreach (var delivery in deliveries)
            {
                delivery.Status = DeliveryStatus.Delivered;
                delivery.DeliveredAtUtc = DateTime.UtcNow;
                delivery.RunId = runId;
            }
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Event run {RunId} started for agent {AgentId} with {Count} event(s)",
                runId, agentId, deliveries.Count);
        }
    }

    [GeneratedRegex(@"^[a-z0-9-]+(\.[a-z0-9-]+)*$")]
    private static partial Regex TopicPattern();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugPattern();
}
