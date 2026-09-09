using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Features.ResourceTypes;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Architecture;

public sealed record MapResourceDto(
    Guid Id,
    string Name,
    ResourceType Type,
    string? CustomTypeKey,
    string Icon,
    string TypeLabel,
    string Description,
    IReadOnlyList<Guid> AgentIds);

/// <summary>A metric attached to an agent with its 30-day reading (total, average or gauge per the metric) — the canvas's outcome line.</summary>
public sealed record MapMetricDto(Guid ResourceId, string Name, string Unit, double? Current);

/// <summary>An agent as the workbench canvas shows it: identity, trigger, schedule state, 24h activity, wiring, outcomes.</summary>
public sealed record MapAgentDto(
    Guid Id,
    string Name,
    string Description,
    string Model,
    EffortLevel Effort,
    int AutonomyLevel,
    bool Enabled,
    bool DryRun,
    bool IsRunning,
    int IntervalMinutes,
    TriggerMode TriggerMode,
    string? TriggerTopics,
    DateTime? LastRunAtUtc,
    DateTime? NextRunAtUtc,
    RunStatus? LastRunStatus,
    int RunsLast24h,
    decimal CostLast24hUsd,
    int OpenPrs,
    int MergedPrs,
    IReadOnlyList<Guid> ResourceIds,
    IReadOnlyList<MapMetricDto> Metrics,
    /// <summary>Topics this agent raises: its completion topics plus attached Event Raisers.</summary>
    IReadOnlyList<string> Raises,
    /// <summary>Patterns that wake this agent: its own topics (Event mode) plus attached Event Listeners.</summary>
    IReadOnlyList<string> Listens);

/// <summary>The whole workspace as one graph: resources feed agents, agents produce delivery.</summary>
public sealed record ArchitectureMapDto(
    IReadOnlyList<MapResourceDto> Resources,
    IReadOnlyList<MapAgentDto> Agents);

public sealed record GetArchitectureMapQuery(Guid? WorkflowId = null) : IQuery<ArchitectureMapDto>;

public sealed class GetArchitectureMapHandler(
    LooperDbContext db,
    ResourceModuleRegistry registry,
    AgentRunCoordinator coordinator) : IQueryHandler<GetArchitectureMapQuery, ArchitectureMapDto>
{
    public async Task<ArchitectureMapDto> Handle(GetArchitectureMapQuery query, CancellationToken cancellationToken)
    {
        var since24h = DateTime.UtcNow.AddHours(-24);
        var since30d = DateTime.UtcNow.AddDays(-30);

        var resources = await db.Resources.AsNoTracking()
            .Where(r => query.WorkflowId == null || r.WorkflowId == query.WorkflowId)
            .Select(r => new
            {
                r.Id, r.Name, r.Type, r.CustomTypeKey, r.Description, r.ConfigJson,
                AgentIds = r.Agents.Select(a => a.Id).ToList(),
            })
            .OrderBy(r => r.Type).ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

        var agents = await db.Agents.AsNoTracking()
            .Where(a => query.WorkflowId == null || a.WorkflowId == query.WorkflowId)
            .Select(a => new
            {
                a.Id, a.Name, a.Description, a.Model, a.Effort, a.AutonomyLevel, a.Enabled, a.DryRun, a.IntervalMinutes,
                a.TriggerMode, a.TriggerTopics, a.LastRunAtUtc, a.NextRunAtUtc,
                ResourceIds = a.Resources.Select(r => r.Id).ToList(),
                LastRunStatus = a.Runs.OrderByDescending(r => r.StartedAtUtc)
                    .Select(r => (RunStatus?)r.Status).FirstOrDefault(),
                RunsLast24h = a.Runs.Count(r => r.StartedAtUtc >= since24h),
                CostLast24hUsd = (decimal?)a.Runs.Where(r => r.StartedAtUtc >= since24h)
                    .Sum(r => (double)r.CostUsd) ?? 0,
            })
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);

        var prCounts = await db.PullRequests.AsNoTracking()
            .Where(pr => pr.Status == PrStatus.Open
                || (pr.Status == PrStatus.Merged && pr.MergedAtUtc >= since30d))
            .GroupBy(pr => pr.AgentId)
            .Select(g => new
            {
                AgentId = g.Key,
                Open = g.Count(pr => pr.Status == PrStatus.Open),
                Merged = g.Count(pr => pr.Status == PrStatus.Merged),
            })
            .ToDictionaryAsync(g => g.AgentId, cancellationToken);

        // Outcomes: the newest reading of every attached Metric resource.
        var metricResources = resources
            .Where(r => r.Type == ResourceType.Custom && string.Equals(r.CustomTypeKey, Modules.BuiltIn.MetricModule.TypeKey_, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var metricIds = metricResources.Select(r => r.Id).ToList();
        var samplesByMetric = (await db.MetricValues.AsNoTracking()
                .Where(v => metricIds.Contains(v.ResourceId))
                .OrderBy(v => v.RecordedAtUtc)
                .Select(v => new { v.ResourceId, v.Value, v.RecordedAtUtc })
                .ToListAsync(cancellationToken))
            .GroupBy(v => v.ResourceId)
            .ToDictionary(g => g.Key, g => g.Select(v => new Features.Metrics.MetricMath.Sample(v.Value, v.RecordedAtUtc)).ToList());
        var now = DateTime.UtcNow;
        var metricsByAgent = metricResources
            .Select(r =>
            {
                var config = Modules.BuiltIn.MetricResources.Parse(new Modules.ResourceModuleContext(r.ConfigJson));
                var current = Features.Metrics.MetricMath.Summarize(config, samplesByMetric.GetValueOrDefault(r.Id) ?? [], now, 30).Current;
                return (Resource: r, Metric: new MapMetricDto(r.Id, r.Name, config.Unit, current));
            })
            .SelectMany(x => x.Resource.AgentIds.Select(agentId => (AgentId: agentId, x.Metric)))
            .GroupBy(x => x.AgentId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<MapMetricDto>)g.Select(x => x.Metric).ToList());

        // Event wiring: raisers and listeners attached to each agent, by resource.
        var raisersByAgent = resources
            .Where(r => r.Type == ResourceType.Custom && string.Equals(r.CustomTypeKey, Modules.BuiltIn.EventRaiserModule.TypeKey_, StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.AgentIds, Topic: Modules.BuiltIn.EventResources.ParseRaiser(new Modules.ResourceModuleContext(r.ConfigJson)).Topic))
            .Where(x => Infrastructure.Execution.EventDispatcher.IsValidTopic(x.Topic))
            .SelectMany(x => x.AgentIds.Select(id => (AgentId: id, x.Topic)))
            .GroupBy(x => x.AgentId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Topic).Distinct().ToList());
        return new ArchitectureMapDto(
            resources.Select(r =>
            {
                var (icon, label) = TypeMeta(r.Type, r.CustomTypeKey);
                return new MapResourceDto(r.Id, r.Name, r.Type, r.CustomTypeKey, icon, label, r.Description, r.AgentIds);
            }).ToList(),
            agents.Select(a => new MapAgentDto(
                a.Id, a.Name, a.Description, a.Model, a.Effort, a.AutonomyLevel, a.Enabled, a.DryRun,
                coordinator.IsRunning(a.Id), a.IntervalMinutes, a.TriggerMode, a.TriggerTopics,
                a.LastRunAtUtc, a.NextRunAtUtc, a.LastRunStatus,
                a.RunsLast24h, Math.Round(a.CostLast24hUsd, 4),
                prCounts.GetValueOrDefault(a.Id)?.Open ?? 0,
                prCounts.GetValueOrDefault(a.Id)?.Merged ?? 0,
                a.ResourceIds,
                metricsByAgent.GetValueOrDefault(a.Id) ?? [],
                new[] { Infrastructure.Execution.EventDispatcher.CompletionTopic(a.Name, true), Infrastructure.Execution.EventDispatcher.CompletionTopic(a.Name, false) }
                    .Concat(raisersByAgent.GetValueOrDefault(a.Id) ?? []).Distinct().ToList(),
                Infrastructure.Execution.EventDispatcher.EffectivePatterns(a.TriggerMode, a.TriggerTopics))).ToList());
    }

    private (string Icon, string Label) TypeMeta(ResourceType type, string? customTypeKey)
    {
        if (type == ResourceType.Custom && customTypeKey is not null && registry.TryGet(customTypeKey, out var module))
        {
            return (module.Icon, module.DisplayName);
        }

        var builtIn = ResourceTypeCatalog.BuiltIns.FirstOrDefault(t => t.TypeKey == type.ToString());
        return builtIn is not null ? (builtIn.Icon, builtIn.Label) : ("🧩", customTypeKey ?? type.ToString());
    }
}

public sealed class GetArchitectureMapEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/architecture/map", (Guid? workflowId, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetArchitectureMapQuery(workflowId), ct));
}
