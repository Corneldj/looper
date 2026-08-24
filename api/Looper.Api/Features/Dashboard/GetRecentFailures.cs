using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Dashboard;

public sealed record GetRecentFailuresQuery(int Take) : IQuery<IReadOnlyList<RecentFailureDto>>;

public sealed class GetRecentFailuresHandler(LooperDbContext db)
    : IQueryHandler<GetRecentFailuresQuery, IReadOnlyList<RecentFailureDto>>
{
    private const int MaxErrorMessageLength = 300;

    public async Task<IReadOnlyList<RecentFailureDto>> Handle(GetRecentFailuresQuery query, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(query.Take, 1, 50);

        var failures = await db.Runs
            .Where(r => r.Status == RunStatus.Failed || r.Status == RunStatus.TimedOut)
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(take)
            .Select(r => new { r.Id, r.AgentId, AgentName = r.Agent!.Name, r.StartedAtUtc, r.Status, r.ErrorMessage })
            .ToListAsync(cancellationToken);

        return failures
            .Select(r => new RecentFailureDto(
                RunId: r.Id,
                AgentId: r.AgentId,
                AgentName: r.AgentName,
                StartedAtUtc: r.StartedAtUtc,
                Status: r.Status,
                ErrorMessage: r.ErrorMessage is { Length: > MaxErrorMessageLength } message
                    ? message[..MaxErrorMessageLength]
                    : r.ErrorMessage))
            .ToList();
    }
}

public sealed class GetRecentFailuresEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/dashboard/recent-failures", (int? take, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetRecentFailuresQuery(take ?? 8), ct));
}
