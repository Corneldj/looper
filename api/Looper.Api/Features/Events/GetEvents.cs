using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Events;

public sealed record GetEventsQuery(int Take) : IQuery<IReadOnlyList<EventDto>>;

public sealed class GetEventsHandler(LooperDbContext db)
    : IQueryHandler<GetEventsQuery, IReadOnlyList<EventDto>>
{
    public async Task<IReadOnlyList<EventDto>> Handle(GetEventsQuery query, CancellationToken cancellationToken)
    {
        var take = Math.Clamp(query.Take, 1, 200);
        var rows = await db.Events
            .OrderByDescending(e => e.CreatedAtUtc)
            .Take(take)
            .Select(e => new
            {
                Event = e,
                AgentName = db.Agents.Where(a => a.Id == e.SourceAgentId).Select(a => a.Name).FirstOrDefault(),
                Listeners = db.EventDeliveries.Count(d => d.EventId == e.Id),
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new EventDto(
            r.Event.Id, r.Event.Topic, r.Event.Payload, r.Event.Source,
            r.Event.SourceAgentId, r.AgentName, r.Event.ChainDepth, r.Listeners, r.Event.CreatedAtUtc)).ToList();
    }
}

public sealed class GetEventsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/events", (int? take, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetEventsQuery(take ?? 50), ct));
}
