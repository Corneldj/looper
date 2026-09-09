using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.UserActions;

/// <summary>
/// The agent asks the human for something only they can do or decide. Not a failure: the
/// raising run stays successful, and the schedule parks until the request is resolved.
/// Re-raising the same title updates the open request instead of duplicating it.
/// </summary>
public sealed record RaiseUserActionCommand(
    Guid? RunId,
    Guid? AgentId,
    string Title,
    string? Details) : ICommand<UserActionDto>;

public sealed class RaiseUserActionValidator : AbstractValidator<RaiseUserActionCommand>
{
    public RaiseUserActionValidator()
    {
        RuleFor(c => c).Must(c => c.RunId is not null || c.AgentId is not null)
            .WithMessage("Provide runId (preferred — the executor injects $LOOPER_RUN_ID) or agentId.");
        RuleFor(c => c.Title).NotEmpty().MaximumLength(300);
        RuleFor(c => c.Details).MaximumLength(20_000);
    }
}

public sealed class RaiseUserActionHandler(LooperDbContext db)
    : ICommandHandler<RaiseUserActionCommand, UserActionDto>
{
    public async Task<UserActionDto> Handle(RaiseUserActionCommand command, CancellationToken cancellationToken)
    {
        Guid agentId;
        AgentRun? run = null;
        if (command.RunId is not null)
        {
            run = await db.Runs.FirstOrDefaultAsync(r => r.Id == command.RunId, cancellationToken)
                ?? throw new ValidationException($"Unknown run '{command.RunId}'.");
            agentId = run.AgentId;
        }
        else
        {
            agentId = command.AgentId!.Value;
        }

        var agent = await db.Agents.AsNoTracking()
                .Where(a => a.Id == agentId)
                .Select(a => new
                {
                    a.Name,
                    CanRecordToMemory = a.Resources.Any(res =>
                        res.Type == ResourceType.Custom && res.CustomTypeKey != null
                        && Modules.BuiltIn.GraphInfrastructure.MemoryTypeKeys.Contains(res.CustomTypeKey))
                })
                .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ValidationException($"Unknown agent '{agentId}'.");

        // Blocking behaviour comes from the agent's User Action resource at raise time.
        var blocking = true;
        var actionResource = await db.Resources.AsNoTracking()
            .Where(r => r.Type == ResourceType.UserAction && r.Agents.Any(a => a.Id == agentId))
            .FirstOrDefaultAsync(cancellationToken);
        if (actionResource is not null)
        {
            blocking = ResourceConfig.Parse<UserActionConfig>(actionResource).BlockScheduling;
        }

        var title = command.Title.Trim();
        var existing = await db.UserActionRequests.FirstOrDefaultAsync(
            r => r.AgentId == agentId && r.Status == UserActionStatus.Open && r.Title == title, cancellationToken);

        UserActionRequest request;
        if (existing is not null)
        {
            existing.Details = command.Details?.Trim() ?? existing.Details;
            existing.RunId = command.RunId ?? existing.RunId;
            request = existing;
        }
        else
        {
            request = new UserActionRequest
            {
                AgentId = agentId,
                RunId = command.RunId,
                Title = title,
                Details = command.Details?.Trim() ?? "",
                Blocking = blocking,
            };
            db.UserActionRequests.Add(request);
        }

        if (run is not null) run.ActionRequested = true;
        await db.SaveChangesAsync(cancellationToken);
        return request.ToDto(agent.Name, agent.CanRecordToMemory);
    }
}

public sealed class RaiseUserActionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/user-actions", async (RaiseUserActionCommand command, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(command, ct);
            return Results.Created($"/api/user-actions/{dto.Id}", dto);
        });
}
