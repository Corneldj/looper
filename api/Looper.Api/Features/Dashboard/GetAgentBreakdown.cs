using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Dashboard;

public sealed record GetAgentBreakdownQuery(int Days) : IQuery<IReadOnlyList<AgentBreakdownDto>>;

public sealed class GetAgentBreakdownHandler(LooperDbContext db)
    : IQueryHandler<GetAgentBreakdownQuery, IReadOnlyList<AgentBreakdownDto>>
{
    public async Task<IReadOnlyList<AgentBreakdownDto>> Handle(GetAgentBreakdownQuery query, CancellationToken cancellationToken)
    {
        var (_, fromUtc, toUtc) = DashboardWindow.Resolve(query.Days);

        var agents = await db.Agents
            .Select(a => new { a.Id, a.Name, a.Model, a.Enabled })
            .ToListAsync(cancellationToken);

        // SQLite cannot aggregate decimals server-side, so fetch a projection and aggregate in memory.
        var runs = await db.Runs
            .Where(r => r.StartedAtUtc >= fromUtc && r.StartedAtUtc < toUtc)
            .Select(r => new { r.AgentId, r.Status, r.CostUsd, r.DurationMs })
            .ToListAsync(cancellationToken);

        var runsByAgent = runs.ToLookup(r => r.AgentId);

        return agents
            .Where(a => a.Enabled || runsByAgent[a.Id].Any())
            .Select(a =>
            {
                var agentRuns = runsByAgent[a.Id].ToList();
                var completed = agentRuns.Where(r => r.Status != RunStatus.Running).ToList();
                var cost = agentRuns.Sum(r => r.CostUsd);
                return new AgentBreakdownDto(
                    AgentId: a.Id,
                    Name: a.Name,
                    Model: a.Model,
                    Enabled: a.Enabled,
                    CostUsd: cost,
                    Runs: agentRuns.Count,
                    SuccessRate: completed.Count == 0
                        ? 0
                        : (double)completed.Count(r => r.Status == RunStatus.Succeeded) / completed.Count,
                    AvgDurationMs: completed.Count == 0 ? 0 : (long)completed.Average(r => r.DurationMs),
                    AvgCostPerRunUsd: agentRuns.Count == 0 ? 0 : cost / agentRuns.Count);
            })
            .OrderByDescending(a => a.CostUsd)
            .ToList();
    }
}

public sealed class GetAgentBreakdownEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/dashboard/agent-breakdown", (int? days, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetAgentBreakdownQuery(days ?? 14), ct));
}
