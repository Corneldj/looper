using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

/// <summary>Triggers a manual run. Returns the new run id, or null when the agent is already running.</summary>
public sealed record RunAgentNowCommand(Guid Id) : ICommand<Guid?>;

public sealed class RunAgentNowHandler(LooperDbContext db, AgentRunCoordinator coordinator)
    : ICommandHandler<RunAgentNowCommand, Guid?>
{
    public async Task<Guid?> Handle(RunAgentNowCommand command, CancellationToken cancellationToken)
    {
        var exists = await db.Agents.AnyAsync(a => a.Id == command.Id, cancellationToken);
        if (!exists) throw new NotFoundException("Agent", command.Id);

        return await coordinator.TriggerRunAsync(command.Id, RunTrigger.Manual, cancellationToken);
    }
}

public sealed class RunAgentNowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/agents/{id:guid}/run", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var runId = await dispatcher.Send(new RunAgentNowCommand(id), ct);
            return runId is { } startedRunId
                ? Results.Ok(new { runId = startedRunId })
                : Results.Problem(title: "Agent is already running.", statusCode: StatusCodes.Status409Conflict);
        });
}
