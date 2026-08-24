using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.ResourceTypes;

public sealed record DeleteResourceTypeCommand(string TypeKey) : ICommand<DeleteResourceTypeResult>;

public enum DeleteResourceTypeResult { Deleted, NotFound, InUse }

public sealed class DeleteResourceTypeHandler(
    LooperDbContext db,
    ResourceModuleRegistry registry,
    ILogger<DeleteResourceTypeHandler> logger) : ICommandHandler<DeleteResourceTypeCommand, DeleteResourceTypeResult>
{
    public async Task<DeleteResourceTypeResult> Handle(DeleteResourceTypeCommand command, CancellationToken cancellationToken)
    {
        var record = await db.ResourceModules
            .FirstOrDefaultAsync(m => m.TypeKey == command.TypeKey, cancellationToken);
        if (record is null) return DeleteResourceTypeResult.NotFound;

        var inUse = await db.Resources.AnyAsync(
            r => r.Type == ResourceType.Custom && r.CustomTypeKey == record.TypeKey, cancellationToken);
        if (inUse) return DeleteResourceTypeResult.InUse;

        registry.Remove(record.TypeKey);
        db.ResourceModules.Remove(record);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            File.Delete(Path.Combine(registry.ModulesDirectory, record.DllFileName));
        }
        catch (IOException ex)
        {
            // The loaded assembly may hold the file on some platforms; it disappears on next cleanup.
            logger.LogWarning(ex, "Could not delete module DLL {Dll}; it will be ignored at next startup", record.DllFileName);
        }

        return DeleteResourceTypeResult.Deleted;
    }
}

public sealed class DeleteResourceTypeEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/api/resource-types/{typeKey}",
            async (string typeKey, IDispatcher dispatcher, CancellationToken ct) =>
                await dispatcher.Send(new DeleteResourceTypeCommand(typeKey), ct) switch
                {
                    DeleteResourceTypeResult.Deleted => Results.NoContent(),
                    DeleteResourceTypeResult.InUse => Results.Problem(
                        title: "This resource type is still used by existing resources. Delete those resources first.",
                        statusCode: StatusCodes.Status409Conflict),
                    _ => Results.Problem(title: $"Resource type '{typeKey}' was not found.",
                        statusCode: StatusCodes.Status404NotFound)
                });
}
