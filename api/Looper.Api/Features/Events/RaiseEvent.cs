using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Events;

public sealed record EventDto(
    Guid Id,
    string Topic,
    string Payload,
    EventSource Source,
    Guid? SourceAgentId,
    string? SourceAgentName,
    int ChainDepth,
    int Listeners,
    DateTime CreatedAtUtc);

/// <summary>
/// Raises an event on the bus. Agents raise with their runId (the injected protocol);
/// the UI/API raise as the user. Matching and delivery are deterministic — the response
/// says how many listeners the event fanned out to.
/// </summary>
public sealed record RaiseEventCommand(
    string Topic,
    string? Payload,
    Guid? RunId,
    Guid? AgentId) : ICommand<EventDto>;

public sealed class RaiseEventValidator : AbstractValidator<RaiseEventCommand>
{
    public RaiseEventValidator()
    {
        RuleFor(c => c.Topic).NotEmpty().MaximumLength(200)
            .Must(t => EventDispatcher.IsValidTopic(t.Trim()))
            .WithMessage("Topics are dotted lowercase keys — letters, digits and dashes, e.g. 'prd.approved' or 'docs.updated'. No wildcards when raising.");
        RuleFor(c => c.Payload).MaximumLength(20_000);
    }
}

public sealed class RaiseEventHandler(
    LooperDbContext db,
    EventDispatcher dispatcher,
    AgentRunCoordinator coordinator) : ICommandHandler<RaiseEventCommand, EventDto>
{
    public async Task<EventDto> Handle(RaiseEventCommand command, CancellationToken cancellationToken)
    {
        Guid? agentId = command.AgentId;
        var depth = 0;
        var source = EventSource.User;

        if (command.RunId is not null)
        {
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == command.RunId, cancellationToken)
                ?? throw new ValidationException($"Unknown run '{command.RunId}'.");
            agentId = run.AgentId;
            depth = run.EventDepth; // events raised inside an event-triggered run inherit its depth
            source = EventSource.Agent;
        }
        else if (agentId is not null)
        {
            source = EventSource.Agent;
        }

        var evt = await dispatcher.RaiseAsync(
            db, command.Topic.Trim(), command.Payload?.Trim() ?? "", source,
            agentId, command.RunId, depth, cancellationToken);

        // Deliver immediately where possible; busy/parked listeners are retried on scheduler ticks.
        await dispatcher.PumpAsync(db, coordinator, cancellationToken);

        var listeners = await db.EventDeliveries.CountAsync(d => d.EventId == evt.Id, cancellationToken);
        var agentName = agentId is null
            ? null
            : await db.Agents.Where(a => a.Id == agentId).Select(a => a.Name).FirstOrDefaultAsync(cancellationToken);

        return new EventDto(evt.Id, evt.Topic, evt.Payload, evt.Source, agentId, agentName,
            evt.ChainDepth, listeners, evt.CreatedAtUtc);
    }
}

public sealed class RaiseEventEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/events", async (RaiseEventCommand command, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(command, ct);
            return Results.Created($"/api/events/{dto.Id}", dto);
        });
}
