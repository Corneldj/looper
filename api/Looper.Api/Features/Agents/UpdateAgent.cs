using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

public sealed record UpdateAgentCommand(Guid Id, SaveAgentRequest Request) : ICommand<AgentDetailDto>;

public sealed class UpdateAgentValidator : AbstractValidator<UpdateAgentCommand>
{
    public UpdateAgentValidator()
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

public sealed class UpdateAgentHandler(LooperDbContext db, AgentRunCoordinator coordinator)
    : ICommandHandler<UpdateAgentCommand, AgentDetailDto>
{
    public async Task<AgentDetailDto> Handle(UpdateAgentCommand command, CancellationToken cancellationToken)
    {
        var agent = await db.Agents
            .Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Agent", command.Id);

        var request = command.Request;
        request.Apply(agent);

        var resources = await db.Resources
            .Where(r => request.ResourceIds.Contains(r.Id))
            .ToListAsync(cancellationToken);
        AgentTriggers.RequireSameWorkflow(agent.WorkflowId, resources);
        AgentTriggers.RequireSomethingToListenFor(request);
        agent.Resources.Clear();
        agent.Resources.AddRange(resources);

        agent.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var stats = await AgentMapper.QueryRunStatsAsync(db, agent.Id, cancellationToken);
        return agent.ToDetailDto(coordinator.IsRunning(agent.Id), stats);
    }
}

public sealed class UpdateAgentEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/api/agents/{id:guid}", (Guid id, SaveAgentRequest request, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new UpdateAgentCommand(id, request), ct));
}
