using Looper.Api.Domain;
using Looper.Api.Features.Workflows;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Infrastructure;

/// <summary>
/// The "Event Listener" resource type was retired: an agent's own "on events" trigger is the
/// listener, so a separate resource only duplicated it. Anything that still carries one — a
/// database from before, a workflow package exported before — is converted the same way:
/// an event-mode agent absorbs the pattern into its trigger topics; a scheduled agent keeps its
/// schedule and the pattern is dropped with a warning (a schedule and a subscription were never
/// both allowed on one agent). The listener resource itself is removed.
/// </summary>
public static class LegacyEventListeners
{
    public const string TypeKey = "EventListener";

    public static bool IsListener(string? customTypeKey) =>
        string.Equals(customTypeKey, TypeKey, StringComparison.OrdinalIgnoreCase);

    public static string Pattern(string configJson) =>
        new Modules.ResourceModuleContext(configJson).GetString("topic")?.Trim() ?? "";

    /// <summary>Merges a pattern into an agent's topic list, keeping order and dropping duplicates.</summary>
    public static string MergeTopics(string? triggerTopics, string pattern)
    {
        var topics = EventDispatcher.ParsePatterns(triggerTopics).ToList();
        if (EventDispatcher.IsValidPattern(pattern) && !topics.Contains(pattern, StringComparer.Ordinal)) topics.Add(pattern);
        return string.Join("\n", topics);
    }

    /// <summary>Startup conversion of an existing database. Idempotent: nothing to do once no listener resources remain.</summary>
    public static async Task<int> RetireAsync(LooperDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        var listeners = await db.Resources
            .Include(r => r.Agents)
            .Where(r => r.Type == ResourceType.Custom && r.CustomTypeKey == TypeKey)
            .OrderBy(r => r.Name)   // a stable merge order, whatever the database hands back
            .ToListAsync(cancellationToken);
        if (listeners.Count == 0) return 0;

        foreach (var listener in listeners)
        {
            var pattern = Pattern(listener.ConfigJson);
            foreach (var agent in listener.Agents)
            {
                if (agent.TriggerMode == TriggerMode.Event)
                {
                    agent.TriggerTopics = MergeTopics(agent.TriggerTopics, pattern);
                    agent.UpdatedAtUtc = DateTime.UtcNow;
                    logger.LogInformation("Event Listener '{Listener}' folded into agent '{Agent}': it now listens for '{Pattern}' itself",
                        listener.Name, agent.Name, pattern);
                }
                else
                {
                    logger.LogWarning("Event Listener '{Listener}' was attached to scheduled agent '{Agent}'; the pattern '{Pattern}' is dropped — " +
                                      "switch the agent to 'on events' if it should be woken by that topic",
                        listener.Name, agent.Name, pattern);
                }
            }
            db.Resources.Remove(listener);
        }
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Retired {Count} Event Listener resource(s)", listeners.Count);
        return listeners.Count;
    }

    /// <summary>The same conversion for a workflow package written before the type was retired.</summary>
    public static WorkflowPackage Retire(WorkflowPackage package, List<string> warnings)
    {
        if (package.Resources is null || !package.Resources.Any(r => r.Type == ResourceType.Custom && IsListener(r.CustomTypeKey)))
        {
            return package;
        }

        var listeners = package.Resources.Where(r => r.Type == ResourceType.Custom && IsListener(r.CustomTypeKey))
            .ToDictionary(r => r.Ref, r => r, StringComparer.Ordinal);
        var agents = new List<PackagedAgent>();
        foreach (var agent in package.Agents ?? [])
        {
            var topics = agent.TriggerTopics;
            foreach (var listenerRef in (agent.ResourceRefs ?? []).Where(listeners.ContainsKey))
            {
                var listener = listeners[listenerRef];
                var pattern = Pattern(listener.ConfigJson ?? "{}");
                if (agent.TriggerMode == TriggerMode.Event)
                {
                    topics = MergeTopics(topics, pattern);
                    warnings.Add($"Event Listener '{listener.Name}' was folded into agent '{agent.Name}', which now listens for '{pattern}' itself.");
                }
                else
                {
                    warnings.Add($"Event Listener '{listener.Name}' was attached to scheduled agent '{agent.Name}' and was dropped; switch the agent to 'on events' if '{pattern}' should wake it.");
                }
            }
            agents.Add(agent with
            {
                TriggerTopics = topics,
                ResourceRefs = (agent.ResourceRefs ?? []).Where(r => !listeners.ContainsKey(r)).ToList()
            });
        }

        return package with
        {
            Resources = package.Resources.Where(r => !listeners.ContainsKey(r.Ref)).ToList(),
            Agents = agents,
            ResourceTypes = (package.ResourceTypes ?? []).Where(t => !IsListener(t.TypeKey)).ToList(),
            RedactedSecrets = (package.RedactedSecrets ?? []).Where(s => !listeners.ContainsKey(s.ResourceRef)).ToList()
        };
    }
}
