using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Workspaces;

public sealed record GetWorkspacesQuery(Guid? ResourceId, bool IncludeCleaned) : IQuery<IReadOnlyList<WorkspaceDto>>;

public sealed class GetWorkspacesHandler(LooperDbContext db)
    : IQueryHandler<GetWorkspacesQuery, IReadOnlyList<WorkspaceDto>>
{
    public async Task<IReadOnlyList<WorkspaceDto>> Handle(GetWorkspacesQuery query, CancellationToken cancellationToken)
    {
        var rows = await db.Workspaces
            .Where(w => query.ResourceId == null || w.ResourceId == query.ResourceId)
            .Where(w => query.IncludeCleaned || w.Status != WorkspaceStatus.Cleaned)
            .Select(w => new
            {
                Workspace = w,
                PoolName = w.Resource.Name,
                AgentName = db.Agents.Where(a => a.Id == w.AgentId).Select(a => a.Name).FirstOrDefault(),
            })
            .OrderByDescending(w => w.Workspace.LastUsedAtUtc)
            .Take(200)
            .ToListAsync(cancellationToken);

        return rows.Select(r => r.Workspace.ToDto(r.PoolName, r.AgentName)).ToList();
    }
}

public sealed class GetWorkspacesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/workspaces", (Guid? resourceId, bool? includeCleaned, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetWorkspacesQuery(resourceId, includeCleaned ?? false), ct));
}
