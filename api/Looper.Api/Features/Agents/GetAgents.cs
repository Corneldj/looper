using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

public sealed record GetAgentsQuery(Guid? WorkflowId = null) : IQuery<IReadOnlyList<AgentSummaryDto>>;

public sealed class GetAgentsHandler(LooperDbContext db, AgentRunCoordinator coordinator)
    : IQueryHandler<GetAgentsQuery, IReadOnlyList<AgentSummaryDto>>
{
    public async Task<IReadOnlyList<AgentSummaryDto>> Handle(GetAgentsQuery query, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var agents = await db.Agents
            .Where(a => query.WorkflowId == null || a.WorkflowId == query.WorkflowId)
            .Select(a => new
            {
                Agent = a,
                ResourceCount = a.Resources.Count,
                LastRunStatus = a.Runs.OrderByDescending(r => r.StartedAtUtc)
                    .Select(r => (RunStatus?)r.Status).FirstOrDefault(),
                RunsLast24h = a.Runs.Count(r => r.StartedAtUtc >= since),
                // SQLite cannot aggregate decimals; sum as double and convert back.
                CostLast24h = a.Runs.Where(r => r.StartedAtUtc >= since).Sum(r => (double)r.CostUsd)
            })
            .OrderBy(a => a.Agent.Name)
            .ToListAsync(cancellationToken);

        return agents.Select(a => a.Agent.ToSummaryDto(
            coordinator.IsRunning(a.Agent.Id),
            a.ResourceCount,
            new AgentRunStats(a.LastRunStatus, a.RunsLast24h, (decimal)a.CostLast24h))).ToList();
    }
}

public sealed class GetAgentsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/agents", (Guid? workflowId, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetAgentsQuery(workflowId), ct));
}
