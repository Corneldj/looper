using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

// ============================================================================
// Single-edge wiring: the workbench canvas connects a resource to an agent by
// dragging from the resource's port onto the agent (and cuts an edge with ✕).
// One call per edge, idempotent both ways, no full agent body round-tripped.
// ============================================================================

public sealed record AttachResourceCommand(Guid AgentId, Guid ResourceId) : ICommand<AgentDetailDto>;

public sealed record DetachResourceCommand(Guid AgentId, Guid ResourceId) : ICommand<AgentDetailDto>;

public sealed class AttachResourceHandler(LooperDbContext db, AgentRunCoordinator coordinator)
    : ICommandHandler<AttachResourceCommand, AgentDetailDto>
{
    public async Task<AgentDetailDto> Handle(AttachResourceCommand command, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == command.AgentId, cancellationToken)
            ?? throw new NotFoundException("Agent", command.AgentId);

        if (agent.Resources.All(r => r.Id != command.ResourceId))
        {
            var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == command.ResourceId, cancellationToken)
                ?? throw new NotFoundException("Resource", command.ResourceId);
            AgentTriggers.RequireSameWorkflow(agent.WorkflowId, [resource]);
            agent.Resources.Add(resource);
            agent.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        var stats = await AgentMapper.QueryRunStatsAsync(db, agent.Id, cancellationToken);
        return agent.ToDetailDto(coordinator.IsRunning(agent.Id), stats);
    }
}

public sealed class DetachResourceHandler(LooperDbContext db, AgentRunCoordinator coordinator)
    : ICommandHandler<DetachResourceCommand, AgentDetailDto>
{
    public async Task<AgentDetailDto> Handle(DetachResourceCommand command, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == command.AgentId, cancellationToken)
            ?? throw new NotFoundException("Agent", command.AgentId);

        var attached = agent.Resources.FirstOrDefault(r => r.Id == command.ResourceId);
        if (attached is not null)
        {
            agent.Resources.Remove(attached);
            agent.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        var stats = await AgentMapper.QueryRunStatsAsync(db, agent.Id, cancellationToken);
        return agent.ToDetailDto(coordinator.IsRunning(agent.Id), stats);
    }
}

public sealed class WireResourceEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/agents/{id:guid}/resources/{resourceId:guid}",
            (Guid id, Guid resourceId, IDispatcher dispatcher, CancellationToken ct) =>
                dispatcher.Send(new AttachResourceCommand(id, resourceId), ct));

        app.MapDelete("/api/agents/{id:guid}/resources/{resourceId:guid}",
            (Guid id, Guid resourceId, IDispatcher dispatcher, CancellationToken ct) =>
                dispatcher.Send(new DetachResourceCommand(id, resourceId), ct));
    }
}
