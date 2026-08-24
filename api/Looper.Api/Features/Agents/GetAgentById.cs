using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

public sealed record GetAgentByIdQuery(Guid Id) : IQuery<AgentDetailDto>;

public sealed class GetAgentByIdHandler(LooperDbContext db, AgentRunCoordinator coordinator)
    : IQueryHandler<GetAgentByIdQuery, AgentDetailDto>
{
    public async Task<AgentDetailDto> Handle(GetAgentByIdQuery query, CancellationToken cancellationToken)
    {
        var agent = await db.Agents
            .Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == query.Id, cancellationToken)
            ?? throw new NotFoundException("Agent", query.Id);

        var stats = await AgentMapper.QueryRunStatsAsync(db, agent.Id, cancellationToken);
        return agent.ToDetailDto(coordinator.IsRunning(agent.Id), stats);
    }
}

public sealed class GetAgentByIdEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/agents/{id:guid}", (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetAgentByIdQuery(id), ct));
}
