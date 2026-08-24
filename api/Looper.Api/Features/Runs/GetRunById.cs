using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Runs;

public sealed record GetRunByIdQuery(Guid Id) : IQuery<RunDetailDto>;

public sealed class GetRunByIdHandler(LooperDbContext db)
    : IQueryHandler<GetRunByIdQuery, RunDetailDto>
{
    public async Task<RunDetailDto> Handle(GetRunByIdQuery query, CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .Where(r => r.Id == query.Id)
            .Select(r => new
            {
                Run = r,
                AgentName = r.Agent!.Name,
                Logs = r.Logs.OrderBy(l => l.TimestampUtc).ThenBy(l => l.Id).ToList()
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Run", query.Id);

        return run.Run.ToDetailDto(run.AgentName, run.Logs);
    }
}

public sealed class GetRunByIdEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/runs/{id:guid}", (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetRunByIdQuery(id), ct));
}
