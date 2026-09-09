using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Events;

/// <summary>
/// One entry of the event catalog: a topic (or listen pattern) and where it comes from —
/// so raisers and listeners are picked from a list instead of typed from memory.
/// </summary>
public sealed record EventTopicDto(
    string Topic,
    /// <summary>completion · raiser · listener · curation · seen</summary>
    string Kind,
    /// <summary>Human description of the origin: "Docs gardener finishes", "raised by Newsletter sent", …</summary>
    string Source,
    bool IsPattern);

public sealed record GetEventTopicsQuery : IQuery<IReadOnlyList<EventTopicDto>>;

public sealed class GetEventTopicsHandler(LooperDbContext db) : IQueryHandler<GetEventTopicsQuery, IReadOnlyList<EventTopicDto>>
{
    private static readonly string[] MemoryGraphTypes = ["ContinuousVectorMemoryGraph", "KnowledgeGraph", "MemoryGraph"];

    public async Task<IReadOnlyList<EventTopicDto>> Handle(GetEventTopicsQuery query, CancellationToken cancellationToken)
    {
        var catalog = new List<EventTopicDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string topic, string kind, string source, bool isPattern)
        {
            if (seen.Add(topic)) catalog.Add(new EventTopicDto(topic, kind, source, isPattern));
        }

        // Every agent announces its own completion — the backbone every chain can hang off.
        var agents = await db.Agents.AsNoTracking()
            .OrderBy(a => a.Name)
            .Select(a => new { a.Name, a.TriggerMode, a.TriggerTopics })
            .ToListAsync(cancellationToken);
        foreach (var agent in agents)
        {
            Add(EventDispatcher.CompletionTopic(agent.Name, true), "completion", $"{agent.Name} succeeds", false);
            Add(EventDispatcher.CompletionTopic(agent.Name, false), "completion", $"{agent.Name} fails", false);
        }

        // Named events the user defined through resources.
        var resources = await db.Resources.AsNoTracking()
            .Where(r => r.Type == ResourceType.Custom)
            .OrderBy(r => r.Name)
            .Select(r => new { r.Name, r.CustomTypeKey, r.ConfigJson, Agents = r.Agents.Select(a => a.Name).ToList() })
            .ToListAsync(cancellationToken);
        foreach (var resource in resources)
        {
            var attached = resource.Agents.Count == 0 ? "not attached yet" : "raised by " + string.Join(", ", resource.Agents);
            if (string.Equals(resource.CustomTypeKey, EventRaiserModule.TypeKey_, StringComparison.OrdinalIgnoreCase))
            {
                var topic = EventResources.ParseRaiser(new Modules.ResourceModuleContext(resource.ConfigJson)).Topic;
                if (EventDispatcher.IsValidTopic(topic)) Add(topic, "raiser", $"{resource.Name} · {attached}", false);
            }
            else if (MemoryGraphTypes.Contains(resource.CustomTypeKey, StringComparer.OrdinalIgnoreCase))
            {
                Add($"graph.{EventDispatcher.Slug(resource.Name, "graph")}.needs-curation", "curation",
                    $"{resource.Name} needs curation", false);
            }
        }
        foreach (var agent in agents.Where(a => a.TriggerMode == TriggerMode.Event))
        {
            foreach (var pattern in EventDispatcher.ParsePatterns(agent.TriggerTopics).Where(EventDispatcher.IsValidPattern))
            {
                Add(pattern, "listener", $"{agent.Name} listens", pattern.EndsWith(".*", StringComparison.Ordinal));
            }
        }

        // Anything that actually crossed the bus and is not otherwise known.
        var recent = await db.Events.AsNoTracking()
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(500)
            .Select(e => e.Topic)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var topic in recent.Order(StringComparer.Ordinal))
        {
            Add(topic, "seen", "raised before", false);
        }

        return catalog;
    }
}

public sealed class GetEventTopicsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/events/topics", (IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetEventTopicsQuery(), ct));
}
