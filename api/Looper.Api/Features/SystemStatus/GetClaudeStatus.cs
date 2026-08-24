using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure.Execution;

namespace Looper.Api.Features.SystemStatus;

public sealed record ClaudeStatusDto(bool Available, string? Version, string Command, string? Error);

public sealed record GetClaudeStatusQuery(bool Refresh) : IQuery<ClaudeStatusDto>;

public sealed class GetClaudeStatusHandler(ClaudeCliStatusService statusService)
    : IQueryHandler<GetClaudeStatusQuery, ClaudeStatusDto>
{
    public async Task<ClaudeStatusDto> Handle(GetClaudeStatusQuery query, CancellationToken cancellationToken)
    {
        var status = await statusService.GetStatusAsync(query.Refresh, cancellationToken);
        return new ClaudeStatusDto(status.Available, status.Version, status.Command, status.Error);
    }
}

public sealed class GetClaudeStatusEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/system/claude-status", (bool? refresh, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetClaudeStatusQuery(refresh ?? false), ct));
}
