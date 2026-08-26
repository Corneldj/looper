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
    IReadOnlyList<Guid> AgentIds);

public sealed record MapAgentDto(
    Guid Id,
    string Name,
    string Model,
    int AutonomyLevel,
    bool Enabled,
    bool DryRun,
    bool IsRunning,
    int IntervalMinutes,
    RunStatus? LastRunStatus,
    int RunsLast24h,
    decimal CostLast24hUsd,
    int OpenPrs,
    int MergedPrs,
    IReadOnlyList<Guid> ResourceIds);

/// <summary>The whole workspace as one graph: resources feed agents, agents produce delivery.</summary>
public sealed record ArchitectureMapDto(
    IReadOnlyList<MapResourceDto> Resources,
    IReadOnlyList<MapAgentDto> Agents);

public sealed record GetArchitectureMapQuery : IQuery<ArchitectureMapDto>;

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
            .Select(r => new
            {
                r.Id, r.Name, r.Type, r.CustomTypeKey,
                AgentIds = r.Agents.Select(a => a.Id).ToList(),
            })
            .OrderBy(r => r.Type).ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);

        var agents = await db.Agents.AsNoTracking()
            .Select(a => new
            {
                a.Id, a.Name, a.Model, a.AutonomyLevel, a.Enabled, a.DryRun, a.IntervalMinutes,
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

        return new ArchitectureMapDto(
            resources.Select(r =>
            {
                var (icon, label) = TypeMeta(r.Type, r.CustomTypeKey);
                return new MapResourceDto(r.Id, r.Name, r.Type, r.CustomTypeKey, icon, label, r.AgentIds);
            }).ToList(),
            agents.Select(a => new MapAgentDto(
                a.Id, a.Name, a.Model, a.AutonomyLevel, a.Enabled, a.DryRun,
                coordinator.IsRunning(a.Id), a.IntervalMinutes, a.LastRunStatus,
                a.RunsLast24h, Math.Round(a.CostLast24hUsd, 4),
                prCounts.GetValueOrDefault(a.Id)?.Open ?? 0,
                prCounts.GetValueOrDefault(a.Id)?.Merged ?? 0,
                a.ResourceIds)).ToList());
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
        app.MapGet("/api/architecture/map", (IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetArchitectureMapQuery(), ct));
}
