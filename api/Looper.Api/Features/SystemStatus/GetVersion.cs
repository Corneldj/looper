using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;

namespace Looper.Api.Features.SystemStatus;

public sealed record VersionDto(string Name, string Version);

public sealed record GetVersionQuery : IQuery<VersionDto>;

/// <summary>What is running: the assembly version, so the UI and support conversations can name it.</summary>
public sealed class GetVersionHandler : IQueryHandler<GetVersionQuery, VersionDto>
{
    public static string Current => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev";

    public Task<VersionDto> Handle(GetVersionQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new VersionDto("Looper", Current));
}

public sealed class GetVersionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system/version", (IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetVersionQuery(), ct));
}
