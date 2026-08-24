using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure.Execution;

namespace Looper.Api.Features.Agents;

/// <summary>Requests cancellation of the agent's active run. Returns false when nothing is running.</summary>
public sealed record CancelAgentRunCommand(Guid Id) : ICommand<bool>;

public sealed class CancelAgentRunHandler(AgentRunCoordinator coordinator)
    : ICommandHandler<CancelAgentRunCommand, bool>
{
    public Task<bool> Handle(CancelAgentRunCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(coordinator.CancelActiveRun(command.Id));
}

public sealed class CancelAgentRunEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/agents/{id:guid}/cancel", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var cancelled = await dispatcher.Send(new CancelAgentRunCommand(id), ct);
            return cancelled
                ? Results.NoContent()
                : Results.Problem(title: "No active run to cancel.", statusCode: StatusCodes.Status404NotFound);
        });
}
