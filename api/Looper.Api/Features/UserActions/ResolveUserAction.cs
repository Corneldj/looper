using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.UserActions;

/// <summary>
/// The human completes the request: mark it resolved (optionally with a response that reaches
/// the agent's next run as context) and unpark the schedule — the next iteration fires
/// immediately when the agent is enabled.
/// </summary>
public sealed record ResolveUserActionCommand(Guid Id, string? Response) : ICommand<UserActionDto>;

public sealed class ResolveUserActionValidator : AbstractValidator<ResolveUserActionCommand>
{
    public ResolveUserActionValidator()
    {
        RuleFor(c => c.Response).MaximumLength(20_000);
    }
}

public sealed class ResolveUserActionHandler(LooperDbContext db)
    : ICommandHandler<ResolveUserActionCommand, UserActionDto>
{
    public async Task<UserActionDto> Handle(ResolveUserActionCommand command, CancellationToken cancellationToken)
    {
        var row = await db.UserActionRequests
            .Where(r => r.Id == command.Id)
            .Select(r => new { Request = r, AgentName = r.Agent.Name, r.Agent.Enabled })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("User action request", command.Id);

        var request = row.Request;
        if (request.Status == UserActionStatus.Open)
        {
            request.Status = UserActionStatus.Resolved;
            request.ResolvedAtUtc = DateTime.UtcNow;
            request.Response = string.IsNullOrWhiteSpace(command.Response) ? null : command.Response.Trim();

            // Unpark: when nothing else blocks the agent and it's enabled, run at the next tick.
            var stillBlocked = await UserActionGate.IsBlockedAsync(db, request.AgentId, cancellationToken);
            if (!stillBlocked && row.Enabled)
            {
                await db.Agents.Where(a => a.Id == request.AgentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.NextRunAtUtc, DateTime.UtcNow), cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        return request.ToDto(row.AgentName);
    }
}

public sealed class ResolveUserActionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/user-actions/{id:guid}/resolve", (Guid id, ResolveBody? body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new ResolveUserActionCommand(id, body?.Response), ct));

    public sealed record ResolveBody(string? Response);
}
