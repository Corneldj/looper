using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Modules;

namespace Looper.Api.Features.ResourceTypes;

public sealed record GetResourceTypesQuery : IQuery<IReadOnlyList<ResourceTypeDto>>;

public sealed class GetResourceTypesHandler(ResourceModuleRegistry registry)
    : IQueryHandler<GetResourceTypesQuery, IReadOnlyList<ResourceTypeDto>>
{
    public Task<IReadOnlyList<ResourceTypeDto>> Handle(GetResourceTypesQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<ResourceTypeDto> all =
        [
            .. ResourceTypeCatalog.BuiltIns,
            .. registry.All.Select(m => m.ToDto(registry.IsBuiltIn(m.TypeKey)))
        ];
        return Task.FromResult(all);
    }
}

public sealed class GetResourceTypesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/resource-types", (IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetResourceTypesQuery(), ct));
}
