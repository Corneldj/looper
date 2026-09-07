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

        // A script's on-disk projection goes with it; other types own their folders (user paths).
        if (resource.Type == Looper.Api.Domain.ResourceType.Custom &&
            string.Equals(resource.CustomTypeKey, Looper.Api.Modules.BuiltIn.ScriptModule.TypeKey_, StringComparison.OrdinalIgnoreCase))
        {
            Looper.Api.Modules.BuiltIn.ScriptResources.RemoveMaterialized(resource.Id);
        }
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
