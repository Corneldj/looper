using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.UserActions;

public sealed record GetUserActionsQuery(Guid? AgentId, bool IncludeResolved) : IQuery<IReadOnlyList<UserActionDto>>;

public sealed class GetUserActionsHandler(LooperDbContext db)
    : IQueryHandler<GetUserActionsQuery, IReadOnlyList<UserActionDto>>
{
    public async Task<IReadOnlyList<UserActionDto>> Handle(GetUserActionsQuery query, CancellationToken cancellationToken)
    {
        var rows = await db.UserActionRequests
            .Where(r => query.AgentId == null || r.AgentId == query.AgentId)
            .Where(r => query.IncludeResolved || r.Status == UserActionStatus.Open)
            .Select(r => new
            {
                Request = r,
                AgentName = r.Agent.Name,
                CanRecordToMemory = r.Agent.Resources.Any(res =>
                    res.Type == ResourceType.Custom && res.CustomTypeKey != null
                    && Modules.BuiltIn.GraphInfrastructure.MemoryTypeKeys.Contains(res.CustomTypeKey))
            })
            .OrderByDescending(r => r.Request.CreatedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);

        return rows.Select(r => r.Request.ToDto(r.AgentName, r.CanRecordToMemory)).ToList();
    }
}

public sealed class GetUserActionsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/user-actions", (Guid? agentId, bool? includeResolved, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetUserActionsQuery(agentId, includeResolved ?? false), ct));
}
