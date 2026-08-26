using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Workspaces;

/// <summary>Marks a unit of work complete; the janitor removes the directory after retention.</summary>
public sealed record CompleteWorkspaceCommand(Guid Id, string? Summary) : ICommand<bool>;

public sealed class CompleteWorkspaceHandler(LooperDbContext db)
    : ICommandHandler<CompleteWorkspaceCommand, bool>
{
    public async Task<bool> Handle(CompleteWorkspaceCommand command, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Workspace", command.Id);

        if (workspace.Status == WorkspaceStatus.Active)
        {
            workspace.Status = WorkspaceStatus.Done;
            workspace.DoneAtUtc = DateTime.UtcNow;
            workspace.LastUsedAtUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(command.Summary))
            {
                var briefPath = Path.Combine(workspace.Path, "WORKBRIEF.md");
                try
                {
                    if (File.Exists(briefPath))
                    {
                        await File.AppendAllTextAsync(briefPath,
                            $"\n---\nMarked done {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC: {command.Summary.Trim()}\n",
                            cancellationToken);
                    }
                }
                catch (IOException)
                {
                    // The brief is a convenience; completion must not fail on it.
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }
}

public sealed class CompleteWorkspaceEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/workspaces/{id:guid}/done", async (Guid id, CompleteBody? body, IDispatcher dispatcher, CancellationToken ct) =>
        {
            await dispatcher.Send(new CompleteWorkspaceCommand(id, body?.Summary), ct);
            return Results.NoContent();
        });

    public sealed record CompleteBody(string? Summary);
}
