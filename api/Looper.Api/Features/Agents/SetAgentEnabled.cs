using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

public sealed record SetAgentEnabledCommand(Guid Id, bool Enabled) : ICommand<AgentSummaryDto>;

public sealed class SetAgentEnabledHandler(LooperDbContext db, AgentRunCoordinator coordinator)
    : ICommandHandler<SetAgentEnabledCommand, AgentSummaryDto>
{
    public async Task<AgentSummaryDto> Handle(SetAgentEnabledCommand command, CancellationToken cancellationToken)
    {
        var result = await db.Agents
            .Where(a => a.Id == command.Id)
            .Select(a => new { Agent = a, ResourceCount = a.Resources.Count })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Agent", command.Id);

        var agent = result.Agent;
        agent.Enabled = command.Enabled;
        // Enabling schedules an immediate run on the next scheduler tick; disabling unschedules.
        // Scheduled agents fire on the next tick when enabled; event agents wait for their events.
        agent.NextRunAtUtc = command.Enabled && agent.TriggerMode == Domain.TriggerMode.Scheduled
            ? DateTime.UtcNow
            : null;
        agent.UpdatedAtUtc = DateTime.UtcNow;
        if (!command.Enabled) coordinator.CancelActiveRun(agent.Id);

        await db.SaveChangesAsync(cancellationToken);

        var stats = await AgentMapper.QueryRunStatsAsync(db, agent.Id, cancellationToken);
        return agent.ToSummaryDto(coordinator.IsRunning(agent.Id), result.ResourceCount, stats);
    }
}

public sealed class SetAgentEnabledEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/agents/{id:guid}/enabled", (Guid id, SetAgentEnabledBody body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new SetAgentEnabledCommand(id, body.Enabled), ct));

    public sealed record SetAgentEnabledBody(bool Enabled);
}
