using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Dashboard;

public sealed record GetDashboardSummaryQuery(int Days, Guid? WorkflowId = null) : IQuery<DashboardSummaryDto>;

public sealed class GetDashboardSummaryHandler(LooperDbContext db)
    : IQueryHandler<GetDashboardSummaryQuery, DashboardSummaryDto>
{
    public async Task<DashboardSummaryDto> Handle(GetDashboardSummaryQuery query, CancellationToken cancellationToken)
    {
        var (days, fromUtc, toUtc) = DashboardWindow.Resolve(query.Days);
        var previousFromUtc = fromUtc.AddDays(-days);

        // SQLite cannot aggregate decimals server-side, so fetch a projection and aggregate in memory.
        var workflowId = query.WorkflowId;
        var runs = await db.Runs
            .Where(r => r.StartedAtUtc >= fromUtc && r.StartedAtUtc < toUtc)
            .Where(r => workflowId == null || r.Agent!.WorkflowId == workflowId)
            .Select(r => new { r.Status, r.CostUsd, r.DurationMs, r.InputTokens, r.OutputTokens })
            .ToListAsync(cancellationToken);

        var previousCosts = await db.Runs
            .Where(r => r.StartedAtUtc >= previousFromUtc && r.StartedAtUtc < fromUtc)
            .Where(r => workflowId == null || r.Agent!.WorkflowId == workflowId)
            .Select(r => r.CostUsd)
            .ToListAsync(cancellationToken);

        var activeAgents = await db.Agents.CountAsync(a => a.Enabled && (workflowId == null || a.WorkflowId == workflowId), cancellationToken);
        var totalAgents = await db.Agents.CountAsync(a => workflowId == null || a.WorkflowId == workflowId, cancellationToken);

        var completed = runs.Where(r => r.Status != RunStatus.Running).ToList();
        var totalCost = runs.Sum(r => r.CostUsd);
        var previousCost = previousCosts.Sum();

        return new DashboardSummaryDto(
            TotalCostUsd: totalCost,
            TotalRuns: runs.Count,
            SuccessRate: completed.Count == 0
                ? 0
                : (double)completed.Count(r => r.Status == RunStatus.Succeeded) / completed.Count,
            ActiveAgents: activeAgents,
            TotalAgents: totalAgents,
            AvgDurationMs: completed.Count == 0 ? 0 : (long)completed.Average(r => r.DurationMs),
            TotalInputTokens: runs.Sum(r => r.InputTokens),
            TotalOutputTokens: runs.Sum(r => r.OutputTokens),
            CostTrendPct: previousCost == 0 ? null : (double)((totalCost - previousCost) / previousCost * 100),
            RunsTrendPct: previousCosts.Count == 0 ? null : (double)(runs.Count - previousCosts.Count) / previousCosts.Count * 100);
    }
}

public sealed class GetDashboardSummaryEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/dashboard/summary", (int? days, Guid? workflowId, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetDashboardSummaryQuery(days ?? 14, workflowId), ct));
}
