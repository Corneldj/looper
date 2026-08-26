using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Workspaces;

/// <summary>Immediately removes the workspace directory (janitor-ahead-of-schedule); the record stays as history.</summary>
public sealed record CleanWorkspaceCommand(Guid Id) : ICommand<bool>;

public sealed class CleanWorkspaceHandler(LooperDbContext db, WorkspaceProvisioner provisioner)
    : ICommandHandler<CleanWorkspaceCommand, bool>
{
    public async Task<bool> Handle(CleanWorkspaceCommand command, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces
            .Include(w => w.Resource)
            .FirstOrDefaultAsync(w => w.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Workspace", command.Id);

        if (workspace.Status != WorkspaceStatus.Cleaned)
        {
            try
            {
                provisioner.RemoveDirectory(
                    ResourceConfig.Parse<WorkspacePoolConfig>(workspace.Resource), workspace.Path);
            }
            catch (WorkspaceProvisioningException ex)
            {
                throw new ValidationException(ex.Message);
            }

            workspace.Status = WorkspaceStatus.Cleaned;
            workspace.CleanedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}

public sealed class CleanWorkspaceEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/api/workspaces/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            await dispatcher.Send(new CleanWorkspaceCommand(id), ct);
            return Results.NoContent();
        });
}
