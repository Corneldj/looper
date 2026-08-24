using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Resources;

public sealed record GetResourcesQuery(ResourceType? Type) : IQuery<IReadOnlyList<ResourceDto>>;

public sealed class GetResourcesHandler(LooperDbContext db, Looper.Api.Modules.ResourceModuleRegistry registry)
    : IQueryHandler<GetResourcesQuery, IReadOnlyList<ResourceDto>>
{
    public async Task<IReadOnlyList<ResourceDto>> Handle(GetResourcesQuery query, CancellationToken cancellationToken)
    {
        var resources = await db.Resources
            .Where(r => query.Type == null || r.Type == query.Type)
            .Select(r => new { Resource = r, AgentCount = r.Agents.Count })
            .OrderBy(r => r.Resource.Type).ThenBy(r => r.Resource.Name)
            .ToListAsync(cancellationToken);

        return resources.Select(r => r.Resource.ToDto(r.AgentCount, registry)).ToList();
    }
}

public sealed class GetResourcesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/resources", (ResourceType? type, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetResourcesQuery(type), ct));
}
