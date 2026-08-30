using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Graphs;

/// <summary>One graph resource seen as infrastructure: role wiring plus the janitor's last health measurement.</summary>
public sealed record GraphStatusDto(
    Guid ResourceId,
    string Name,
    string TypeKey,
    string Path,
    string? Curator,
    string CurationTopic,
    bool AutoLog,
    int PreambleK,
    int InboxThreshold,
    GraphHealthDto? Health);

public sealed record GraphHealthDto(
    DateTime CheckedUtc,
    int Nodes,
    int FactsCurrent,
    int FactsTotal,
    int Episodes,
    int InboxPending,
    int CompetingCount,
    int StaleCount,
    int ProblemCount,
    int UsageEvents);

public sealed record GetGraphsQuery : IQuery<IReadOnlyList<GraphStatusDto>>;

public sealed class GetGraphsHandler(LooperDbContext db) : IQueryHandler<GetGraphsQuery, IReadOnlyList<GraphStatusDto>>
{
    public async Task<IReadOnlyList<GraphStatusDto>> Handle(GetGraphsQuery query, CancellationToken cancellationToken)
    {
        var resources = await db.Resources.AsNoTracking()
            .Where(r => r.Type == ResourceType.Custom && r.CustomTypeKey != null)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

        return resources
            .Where(GraphInfrastructure.IsGraph)
            .Select(resource =>
            {
                var context = new ResourceModuleContext(resource.ConfigJson);
                var config = GraphInfrastructure.SharedConfig(context);
                var path = context.GetString("path") ?? "";
                return new GraphStatusDto(
                    resource.Id,
                    resource.Name,
                    resource.CustomTypeKey!,
                    path,
                    config.HasCurator ? config.Curator!.Trim() : null,
                    GraphInfrastructure.CurationTopic(resource.Name),
                    config.AutoLog,
                    config.EffectivePreambleK,
                    config.EffectiveInboxThreshold,
                    ReadHealth(path));
            })
            .ToList();
    }

    private static GraphHealthDto? ReadHealth(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var snapshot = GraphMaintenanceService.ReadSnapshot(
            System.IO.Path.Combine(path, GraphInfrastructure.HealthFileName));
        if (snapshot is null) return null;

        var report = snapshot.Report;
        int Get(string name) => report.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : 0;
        return new GraphHealthDto(
            snapshot.CheckedUtc,
            Get("nodes"),
            Get("facts_current"),
            Get("facts_total"),
            Get("episodes"),
            Get("inbox_pending"),
            Get("competing_count"),
            Get("stale_count"),
            Get("problem_count"),
            Get("usage_events"));
    }
}

public sealed class GetGraphsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/graphs", (IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetGraphsQuery(), ct));
}
