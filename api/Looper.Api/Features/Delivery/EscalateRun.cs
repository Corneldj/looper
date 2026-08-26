using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Delivery;

/// <summary>
/// Marks a run as escalated: the agent deliberately handed the work to a human. Reported by
/// the agent itself mid-run (the executor injects the curl protocol) or set from the UI.
/// This is the numerator of the escalation rate — the autonomy-calibration metric.
/// </summary>
public sealed record EscalateRunCommand(Guid RunId, string Reason) : ICommand<bool>;

public sealed class EscalateRunValidator : AbstractValidator<EscalateRunCommand>
{
    public EscalateRunValidator()
    {
        RuleFor(c => c.Reason).NotEmpty().MaximumLength(2000);
    }
}

public sealed class EscalateRunHandler(LooperDbContext db) : ICommandHandler<EscalateRunCommand, bool>
{
    public async Task<bool> Handle(EscalateRunCommand command, CancellationToken cancellationToken)
    {
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == command.RunId, cancellationToken)
            ?? throw new NotFoundException("Run", command.RunId);

        run.Escalated = true;
        run.EscalationReason = command.Reason.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class EscalateRunEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/runs/{id:guid}/escalate", async (Guid id, EscalateBody body, IDispatcher dispatcher, CancellationToken ct) =>
        {
            await dispatcher.Send(new EscalateRunCommand(id, body.Reason), ct);
            return Results.NoContent();
        });

    public sealed record EscalateBody(string Reason);
}
