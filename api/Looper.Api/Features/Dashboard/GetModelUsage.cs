using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Dashboard;

public sealed record GetModelUsageQuery(int Days, Guid? WorkflowId = null) : IQuery<IReadOnlyList<ModelUsageDto>>;

public sealed class GetModelUsageHandler(LooperDbContext db)
    : IQueryHandler<GetModelUsageQuery, IReadOnlyList<ModelUsageDto>>
{
    public async Task<IReadOnlyList<ModelUsageDto>> Handle(GetModelUsageQuery query, CancellationToken cancellationToken)
    {
        var (_, fromUtc, toUtc) = DashboardWindow.Resolve(query.Days);

        // SQLite cannot aggregate decimals server-side, so fetch a projection and group in memory.
        var workflowId = query.WorkflowId;
        var runs = await db.Runs
            .Where(r => r.StartedAtUtc >= fromUtc && r.StartedAtUtc < toUtc)
            .Where(r => workflowId == null || r.Agent!.WorkflowId == workflowId)
            .Select(r => new { r.Model, r.CostUsd, r.InputTokens, r.OutputTokens })
            .ToListAsync(cancellationToken);

        return runs
            .GroupBy(r => string.IsNullOrEmpty(r.Model) ? "unknown" : r.Model)
            .Select(g => new ModelUsageDto(
                Model: g.Key,
                CostUsd: g.Sum(r => r.CostUsd),
                Runs: g.Count(),
                InputTokens: g.Sum(r => r.InputTokens),
                OutputTokens: g.Sum(r => r.OutputTokens)))
            .OrderByDescending(m => m.CostUsd)
            .ToList();
    }
}

public sealed class GetModelUsageEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/dashboard/model-usage", (int? days, Guid? workflowId, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetModelUsageQuery(days ?? 14, workflowId), ct));
}
