using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Metrics;

/// <summary>Every Metric resource with its reading for the window — the dashboard's outcome cards.</summary>
public sealed record GetMetricsQuery(int Days, Guid? WorkflowId = null) : IQuery<IReadOnlyList<MetricSummaryDto>>;

public sealed class GetMetricsHandler(LooperDbContext db) : IQueryHandler<GetMetricsQuery, IReadOnlyList<MetricSummaryDto>>
{
    public async Task<IReadOnlyList<MetricSummaryDto>> Handle(GetMetricsQuery query, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(query.Days, 1, 365);
        var now = DateTime.UtcNow;

        var metrics = await db.Resources.AsNoTracking()
            .Where(r => r.Type == ResourceType.Custom && r.CustomTypeKey == MetricModule.TypeKey_)
            .Where(r => query.WorkflowId == null || r.WorkflowId == query.WorkflowId)
            .OrderBy(r => r.CreatedAtUtc)
            .Select(r => new
            {
                r.Id, r.Name, r.Description, r.ConfigJson,
                Agents = r.Agents.OrderBy(a => a.Name).Select(a => a.Name).ToList()
            })
            .ToListAsync(cancellationToken);
        if (metrics.Count == 0) return [];

        var ids = metrics.Select(m => m.Id).ToList();
        var samples = await db.MetricValues.AsNoTracking()
            .Where(v => ids.Contains(v.ResourceId))
            .OrderBy(v => v.RecordedAtUtc)
            .Select(v => new { v.ResourceId, v.Value, v.RecordedAtUtc })
            .ToListAsync(cancellationToken);
        var byMetric = samples.GroupBy(s => s.ResourceId)
            .ToDictionary(g => g.Key, g => g.Select(s => new MetricMath.Sample(s.Value, s.RecordedAtUtc)).ToList());

        return metrics.Select(m =>
        {
            var config = MetricResources.Parse(new Modules.ResourceModuleContext(m.ConfigJson));
            var ordered = byMetric.GetValueOrDefault(m.Id) ?? [];
            var summary = MetricMath.Summarize(config, ordered, now, days);
            return new MetricSummaryDto(
                m.Id, m.Name, m.Description, config.Unit, config.Aggregation, config.Direction, config.Target,
                summary.Latest, summary.LatestAtUtc, summary.Current, summary.Previous, summary.TrendPct,
                summary.CountInWindow, ordered.Count, summary.Series, m.Agents);
        }).ToList();
    }
}

public sealed class GetMetricsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/metrics", (int? days, Guid? workflowId, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetMetricsQuery(days ?? 30, workflowId), ct));
}
