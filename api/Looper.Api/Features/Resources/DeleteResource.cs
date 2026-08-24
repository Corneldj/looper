using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Resources;

public sealed record DeleteResourceCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteResourceHandler(LooperDbContext db)
    : ICommandHandler<DeleteResourceCommand, bool>
{
    public async Task<bool> Handle(DeleteResourceCommand command, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Resource", command.Id);

        db.Resources.Remove(resource);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class DeleteResourceEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/api/resources/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            await dispatcher.Send(new DeleteResourceCommand(id), ct);
            return Results.NoContent();
        });
}
