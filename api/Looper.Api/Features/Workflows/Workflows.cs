using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Workflows;

public sealed record WorkflowDto(
    Guid Id,
    string Name,
    string Description,
    bool IsDefault,
    int AgentCount,
    int ResourceCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SaveWorkflowRequest(string Name, string Description);

public static class WorkflowMapper
{
    public static WorkflowDto ToDto(this Workflow workflow, int agentCount, int resourceCount) => new(
        workflow.Id, workflow.Name, workflow.Description, workflow.Id == Workflow.DefaultId,
        agentCount, resourceCount, workflow.CreatedAtUtc, workflow.UpdatedAtUtc);

    /// <summary>Resolves the workflow a new item goes into: the given one, or the default.</summary>
    public static async Task<Guid> ResolveAsync(LooperDbContext db, Guid? workflowId, CancellationToken cancellationToken)
    {
        var id = workflowId ?? Workflow.DefaultId;
        if (!await db.Workflows.AnyAsync(w => w.Id == id, cancellationToken))
        {
            throw new NotFoundException("Workflow", id);
        }
        return id;
    }
}

// ---------- list ----------

public sealed record GetWorkflowsQuery : IQuery<IReadOnlyList<WorkflowDto>>;

public sealed class GetWorkflowsHandler(LooperDbContext db) : IQueryHandler<GetWorkflowsQuery, IReadOnlyList<WorkflowDto>>
{
    public async Task<IReadOnlyList<WorkflowDto>> Handle(GetWorkflowsQuery query, CancellationToken cancellationToken)
    {
        var rows = await db.Workflows.AsNoTracking()
            .Select(w => new { Workflow = w, Agents = w.Agents.Count, Resources = w.Resources.Count })
            .ToListAsync(cancellationToken);
        // The default first, then by name — the switcher reads naturally.
        return rows
            .OrderBy(r => r.Workflow.Id == Workflow.DefaultId ? 0 : 1)
            .ThenBy(r => r.Workflow.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Workflow.ToDto(r.Agents, r.Resources))
            .ToList();
    }
}

public sealed class GetWorkflowsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/workflows", (IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetWorkflowsQuery(), ct));
}

// ---------- create ----------

public sealed record CreateWorkflowCommand(string Name, string Description) : ICommand<WorkflowDto>;

public sealed class CreateWorkflowValidator : AbstractValidator<CreateWorkflowCommand>
{
    public CreateWorkflowValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Description).MaximumLength(2000);
    }
}

public sealed class CreateWorkflowHandler(LooperDbContext db) : ICommandHandler<CreateWorkflowCommand, WorkflowDto>
{
    public async Task<WorkflowDto> Handle(CreateWorkflowCommand command, CancellationToken cancellationToken)
    {
        var workflow = new Workflow { Name = command.Name.Trim(), Description = command.Description.Trim() };
        db.Workflows.Add(workflow);
        await db.SaveChangesAsync(cancellationToken);
        return workflow.ToDto(0, 0);
    }
}

public sealed class CreateWorkflowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/workflows", async (SaveWorkflowRequest body, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(new CreateWorkflowCommand(body.Name, body.Description ?? ""), ct);
            return Results.Created($"/api/workflows/{dto.Id}", dto);
        });
}

// ---------- update ----------

public sealed record UpdateWorkflowCommand(Guid Id, string Name, string Description) : ICommand<WorkflowDto>;

public sealed class UpdateWorkflowValidator : AbstractValidator<UpdateWorkflowCommand>
{
    public UpdateWorkflowValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Description).MaximumLength(2000);
    }
}

public sealed class UpdateWorkflowHandler(LooperDbContext db) : ICommandHandler<UpdateWorkflowCommand, WorkflowDto>
{
    public async Task<WorkflowDto> Handle(UpdateWorkflowCommand command, CancellationToken cancellationToken)
    {
        var row = await db.Workflows
            .Where(w => w.Id == command.Id)
            .Select(w => new { Workflow = w, Agents = w.Agents.Count, Resources = w.Resources.Count })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Workflow", command.Id);

        row.Workflow.Name = command.Name.Trim();
        row.Workflow.Description = command.Description.Trim();
        row.Workflow.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return row.Workflow.ToDto(row.Agents, row.Resources);
    }
}

public sealed class UpdateWorkflowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/api/workflows/{id:guid}", (Guid id, SaveWorkflowRequest body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new UpdateWorkflowCommand(id, body.Name, body.Description ?? ""), ct));
}

// ---------- delete ----------

/// <summary>Removes a workflow with everything in it (agents, their runs, resources, metric values). The default one stays.</summary>
public sealed record DeleteWorkflowCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteWorkflowHandler(LooperDbContext db, AgentRunCoordinator coordinator) : ICommandHandler<DeleteWorkflowCommand, bool>
{
    public async Task<bool> Handle(DeleteWorkflowCommand command, CancellationToken cancellationToken)
    {
        if (command.Id == Workflow.DefaultId)
        {
            throw new WorkflowConflictException("The default workflow cannot be deleted — rename it, or empty it instead.");
        }
        var workflow = await db.Workflows.Include(w => w.Agents)
            .FirstOrDefaultAsync(w => w.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Workflow", command.Id);

        foreach (var agent in workflow.Agents) coordinator.CancelActiveRun(agent.Id);
        db.Workflows.Remove(workflow);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>Mapped to 409 by the endpoint — a rule, not a validation slip.</summary>
public sealed class WorkflowConflictException(string message) : Exception(message);

public sealed class DeleteWorkflowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/api/workflows/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            try
            {
                await dispatcher.Send(new DeleteWorkflowCommand(id), ct);
                return Results.NoContent();
            }
            catch (WorkflowConflictException ex)
            {
                return Results.Problem(title: ex.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });
}
