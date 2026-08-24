using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

public sealed record DeleteAgentCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteAgentHandler(LooperDbContext db, AgentRunCoordinator coordinator)
    : ICommandHandler<DeleteAgentCommand, bool>
{
    public async Task<bool> Handle(DeleteAgentCommand command, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Agent", command.Id);

        // Stop any in-flight run before the agent (and its runs, via cascade) disappears.
        coordinator.CancelActiveRun(command.Id);

        db.Agents.Remove(agent);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class DeleteAgentEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/api/agents/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            await dispatcher.Send(new DeleteAgentCommand(id), ct);
            return Results.NoContent();
        });
}
