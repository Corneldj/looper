using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Delivery;

public sealed record GetPullRequestsQuery(int Days, Guid? AgentId, Guid? WorkflowId = null) : IQuery<IReadOnlyList<PullRequestDto>>;

public sealed class GetPullRequestsHandler(LooperDbContext db)
    : IQueryHandler<GetPullRequestsQuery, IReadOnlyList<PullRequestDto>>
{
    public async Task<IReadOnlyList<PullRequestDto>> Handle(GetPullRequestsQuery query, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(query.Days, 1, 365));
        var rows = await db.PullRequests
            .Where(pr => pr.OpenedAtUtc >= since || pr.Status == Domain.PrStatus.Open)
            .Where(pr => query.AgentId == null || pr.AgentId == query.AgentId)
            .Where(pr => query.WorkflowId == null || pr.Agent.WorkflowId == query.WorkflowId)
            .Select(pr => new { Pr = pr, AgentName = pr.Agent.Name })
            .OrderByDescending(pr => pr.Pr.OpenedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);

        return rows.Select(r => r.Pr.ToDto(r.AgentName)).ToList();
    }
}

public sealed class GetPullRequestsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/delivery/prs", (int? days, Guid? agentId, Guid? workflowId, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetPullRequestsQuery(days ?? 30, agentId, workflowId), ct));
}
