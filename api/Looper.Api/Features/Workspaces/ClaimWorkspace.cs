using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Workspaces;

/// <summary>
/// Claims a dedicated workspace for one unit of work. Idempotent by (pool, unit slug):
/// re-claiming an active unit returns the existing workspace, so parallel iterations of
/// the same unit converge on one directory while different units stay isolated.
/// Called by agents mid-run (the executor injects the protocol) or from the UI.
/// </summary>
public sealed record ClaimWorkspaceCommand(
    Guid ResourceId,
    string Unit,
    string? Context,
    Guid? RunId,
    Guid? AgentId) : ICommand<WorkspaceClaimDto>;

public sealed class ClaimWorkspaceValidator : AbstractValidator<ClaimWorkspaceCommand>
{
    public ClaimWorkspaceValidator()
    {
        RuleFor(c => c.Unit).NotEmpty().MaximumLength(120)
            .Must(unit => WorkspaceProvisioner.Slugify(unit).Length > 0)
            .WithMessage("The unit name must contain at least one letter or digit.");
        RuleFor(c => c.Context).MaximumLength(20_000);
    }
}

public sealed class ClaimWorkspaceHandler(LooperDbContext db, WorkspaceProvisioner provisioner)
    : ICommandHandler<ClaimWorkspaceCommand, WorkspaceClaimDto>
{
    public async Task<WorkspaceClaimDto> Handle(ClaimWorkspaceCommand command, CancellationToken cancellationToken)
    {
        var resource = await db.Resources.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == command.ResourceId && r.Type == ResourceType.WorkspacePool, cancellationToken)
            ?? throw new ValidationException($"'{command.ResourceId}' is not a Dynamic Workspaces resource.");
        var config = ResourceConfig.Parse<WorkspacePoolConfig>(resource);

        Guid? agentId = command.AgentId;
        if (agentId is null && command.RunId is not null)
        {
            agentId = await db.Runs.Where(r => r.Id == command.RunId)
                .Select(r => (Guid?)r.AgentId).FirstOrDefaultAsync(cancellationToken);
        }

        var slug = WorkspaceProvisioner.Slugify(command.Unit);
        var existing = await db.Workspaces.FirstOrDefaultAsync(
            w => w.ResourceId == resource.Id && w.Unit == slug && w.Status != WorkspaceStatus.Cleaned,
            cancellationToken);
        if (existing is not null)
        {
            existing.LastUsedAtUtc = DateTime.UtcNow;
            if (existing.Status == WorkspaceStatus.Done) existing.Status = WorkspaceStatus.Active; // unit reopened
            await db.SaveChangesAsync(cancellationToken);
            return new WorkspaceClaimDto(existing.Id, existing.Unit, existing.Path, Created: false, existing.ContextBrief);
        }

        if (config.MaxWorkspaces is int cap)
        {
            var open = await db.Workspaces.CountAsync(
                w => w.ResourceId == resource.Id && w.Status != WorkspaceStatus.Cleaned, cancellationToken);
            if (open >= cap)
            {
                throw new ValidationException(
                    $"The pool '{resource.Name}' is at its cap of {cap} workspaces — mark finished units done or clean old ones first.");
            }
        }

        var createdBy = agentId is not null
            ? await db.Agents.Where(a => a.Id == agentId).Select(a => a.Name).FirstOrDefaultAsync(cancellationToken) ?? "unknown agent"
            : "the workbench";

        string path;
        try
        {
            path = await provisioner.ProvisionAsync(config, slug, command.Context ?? "", createdBy, cancellationToken);
        }
        catch (WorkspaceProvisioningException ex)
        {
            throw new ValidationException(ex.Message);
        }

        var workspace = new ManagedWorkspace
        {
            ResourceId = resource.Id,
            AgentId = agentId,
            RunId = command.RunId,
            Unit = slug,
            Path = path,
            ContextBrief = command.Context ?? "",
        };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(cancellationToken);

        return new WorkspaceClaimDto(workspace.Id, slug, path, Created: true, workspace.ContextBrief);
    }
}

public sealed class ClaimWorkspaceEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/workspaces", async (ClaimWorkspaceCommand command, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(command, ct);
            return dto.Created ? Results.Created($"/api/workspaces/{dto.Id}", dto) : Results.Ok(dto);
        });
}
