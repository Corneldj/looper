using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Runs;

public sealed record GetAgentRunsQuery(Guid AgentId, int Take, int Skip) : IQuery<IReadOnlyList<RunSummaryDto>>;

public sealed class GetAgentRunsHandler(LooperDbContext db)
    : IQueryHandler<GetAgentRunsQuery, IReadOnlyList<RunSummaryDto>>
{
    public async Task<IReadOnlyList<RunSummaryDto>> Handle(GetAgentRunsQuery query, CancellationToken cancellationToken)
    {
        var agentExists = await db.Agents.AnyAsync(a => a.Id == query.AgentId, cancellationToken);
        if (!agentExists) throw new NotFoundException("Agent", query.AgentId);

        var take = Math.Clamp(query.Take, 1, 200);
        var skip = Math.Max(query.Skip, 0);

        var runs = await db.Runs
            .Where(r => r.AgentId == query.AgentId)
            .OrderByDescending(r => r.StartedAtUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return runs.Select(r => r.ToSummaryDto()).ToList();
    }
}

public sealed class GetAgentRunsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/agents/{agentId:guid}/runs", (Guid agentId, int? take, int? skip, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetAgentRunsQuery(agentId, take ?? 50, skip ?? 0), ct));
}
