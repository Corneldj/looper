using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.UserActions;

public sealed record UserActionDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    Guid? RunId,
    string Title,
    string Details,
    UserActionStatus Status,
    bool Blocking,
    string? Response,
    DateTime CreatedAtUtc,
    DateTime? ResolvedAtUtc,
    /// <summary>Where the answer was recorded when resolved.</summary>
    string? ResolutionNote,
    /// <summary>The agent has a memory graph attached, so an answer may be recorded there instead of as a rule.</summary>
    bool CanRecordToMemory);

public static class UserActionMapper
{
    public static UserActionDto ToDto(this UserActionRequest request, string agentName, bool canRecordToMemory) => new(
        request.Id, request.AgentId, agentName, request.RunId, request.Title, request.Details,
        request.Status, request.Blocking, request.Response, request.CreatedAtUtc, request.ResolvedAtUtc,
        request.ResolutionNote, canRecordToMemory);
}

/// <summary>The one question the scheduler and Run-now both ask: is this loop parked on a human?</summary>
public static class UserActionGate
{
    public static Task<bool> IsBlockedAsync(LooperDbContext db, Guid agentId, CancellationToken cancellationToken) =>
        db.UserActionRequests.AnyAsync(
            r => r.AgentId == agentId && r.Status == UserActionStatus.Open && r.Blocking, cancellationToken);
}
