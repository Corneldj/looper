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
        RuleFor(c => c.Request.MaxBudgetUsd).GreaterThan(0).When(c => c.Request.MaxBudgetUsd.HasValue);
    }
}

public sealed class CreateAgentHandler(LooperDbContext db)
    : ICommandHandler<CreateAgentCommand, AgentDetailDto>
{
    public async Task<AgentDetailDto> Handle(CreateAgentCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var resources = await db.Resources
            .Where(r => request.ResourceIds.Contains(r.Id))
            .ToListAsync(cancellationToken);

        // New agents start disabled and unscheduled; SetAgentEnabled puts them on the loop.
        var agent = new LoopAgent
        {
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

public sealed class CreateAgentEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/agents", async (SaveAgentRequest request, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(new CreateAgentCommand(request), ct);
            return Results.Created($"/api/agents/{dto.Id}", dto);
        });
}
