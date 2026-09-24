using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Resources;

public sealed record GetResourcesQuery(ResourceType? Type, Guid? WorkflowId = null) : IQuery<IReadOnlyList<ResourceDto>>;

public sealed class GetResourcesHandler(LooperDbContext db, Looper.Api.Modules.ResourceModuleRegistry registry)
    : IQueryHandler<GetResourcesQuery, IReadOnlyList<ResourceDto>>
{
    public async Task<IReadOnlyList<ResourceDto>> Handle(GetResourcesQuery query, CancellationToken cancellationToken)
    {
        var resources = await db.Resources
            .Where(r => query.Type == null || r.Type == query.Type)
            .Where(r => query.WorkflowId == null || r.WorkflowId == query.WorkflowId)
            .Select(r => new { Resource = r, AgentCount = r.Agents.Count })
            .OrderBy(r => r.Resource.Type).ThenBy(r => r.Resource.Name)
            .ToListAsync(cancellationToken);

        return resources.Select(r => r.Resource.ToDto(r.AgentCount, registry)).ToList();
    }
}

/// <summary>
/// One resource as it is stored now. Runs rewrite some resources — a one-off prompt is cleared as
/// a run takes it, a ticket selection can clear after a success, answers land in rule sets — so an
/// editor opens on this, not on the list the page loaded earlier.
/// </summary>
public sealed record GetResourceByIdQuery(Guid Id) : IQuery<ResourceDto>;

public sealed class GetResourceByIdHandler(LooperDbContext db, Looper.Api.Modules.ResourceModuleRegistry registry)
    : IQueryHandler<GetResourceByIdQuery, ResourceDto>
{
    public async Task<ResourceDto> Handle(GetResourceByIdQuery query, CancellationToken cancellationToken)
    {
        var found = await db.Resources
            .Where(r => r.Id == query.Id)
            .Select(r => new { Resource = r, AgentCount = r.Agents.Count })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Resource", query.Id);
        return found.Resource.ToDto(found.AgentCount, registry);
    }
}

public sealed class GetResourcesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/resources", (ResourceType? type, Guid? workflowId, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetResourcesQuery(type, workflowId), ct));
        app.MapGet("/api/resources/{id:guid}", (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetResourceByIdQuery(id), ct));
    }
}
