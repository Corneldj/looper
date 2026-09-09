using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

public sealed record CreateAgentCommand(SaveAgentRequest Request) : ICommand<AgentDetailDto>;

public sealed class CreateAgentValidator : AbstractValidator<CreateAgentCommand>
{
    public CreateAgentValidator()
    {
        RuleFor(c => c.Request.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Request.Prompt).NotEmpty();
        RuleFor(c => c.Request.Model).NotEmpty();
        RuleFor(c => c.Request.IntervalMinutes).InclusiveBetween(1, 10080);
        RuleFor(c => c.Request.MaxTurns).InclusiveBetween(1, 250);
        RuleFor(c => c.Request.AutonomyLevel).InclusiveBetween(1, 4);
        RuleFor(c => c.Request.TriggerTopics)
            .Must(t => Looper.Api.Infrastructure.Execution.EventDispatcher.ParsePatterns(t)
                .All(Looper.Api.Infrastructure.Execution.EventDispatcher.IsValidPattern))
            .When(c => c.Request.TriggerMode == Looper.Api.Domain.TriggerMode.Event)
            .WithMessage("Topic patterns are dotted lowercase keys, optionally ending in '.*' (e.g. 'agent.docs-gardener.*').");
        RuleFor(c => c.Request.MaxBudgetUsd).GreaterThan(0).When(c => c.Request.MaxBudgetUsd.HasValue);
    }
}

public sealed class CreateAgentHandler(LooperDbContext db)
    : ICommandHandler<CreateAgentCommand, AgentDetailDto>
{
    public async Task<AgentDetailDto> Handle(CreateAgentCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var workflowId = await Workflows.WorkflowMapper.ResolveAsync(db, request.WorkflowId, cancellationToken);
        var resources = await db.Resources
            .Where(r => request.ResourceIds.Contains(r.Id))
            .ToListAsync(cancellationToken);
        AgentTriggers.RequireSameWorkflow(workflowId, resources);
        AgentTriggers.RequireSomethingToListenFor(request, resources);

        // New agents start disabled and unscheduled; SetAgentEnabled puts them on the loop.
        var agent = new LoopAgent
        {
            WorkflowId = workflowId,
            Enabled = false,
            NextRunAtUtc = null,
            Resources = resources
        };
        request.Apply(agent);

        db.Agents.Add(agent);
        await db.SaveChangesAsync(cancellationToken);
        return agent.ToDetailDto(isRunning: false, AgentRunStats.None);
    }
}

/// <summary>Trigger rules that need the attached resources, so they live past the validator.</summary>
public static class AgentTriggers
{
    /// <summary>A workflow is a closed set: an agent only uses resources from its own workflow.</summary>
    public static void RequireSameWorkflow(Guid workflowId, IEnumerable<Resource> resources)
    {
        var foreign = resources.Where(r => r.WorkflowId != workflowId).Select(r => r.Name).ToList();
        if (foreign.Count > 0)
        {
            throw new ValidationException(
                $"Resources belong to another workflow and cannot be attached here: {string.Join(", ", foreign)}.");
        }
    }

    /// <summary>An event-triggered agent must be reachable: its own topic list or at least one Event Listener resource.</summary>
    public static void RequireSomethingToListenFor(SaveAgentRequest request, IEnumerable<Resource> resources)
    {
        if (request.TriggerMode != TriggerMode.Event) return;
        if (Looper.Api.Infrastructure.Execution.EventDispatcher.ParsePatterns(request.TriggerTopics).Count > 0) return;
        if (resources.Any(Looper.Api.Modules.BuiltIn.EventResources.IsListener)) return;
        throw new ValidationException(
            "An event-triggered agent needs something to listen for: pick at least one event, or attach an Event Listener resource.");
    }
}

public sealed class CreateAgentEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/agents", async (SaveAgentRequest request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(new CreateAgentCommand(request), ct);
            return Results.Created($"/api/agents/{dto.Id}", dto);
        });
}
