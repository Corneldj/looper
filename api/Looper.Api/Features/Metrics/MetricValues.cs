using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Metrics;

/// <summary>The raw measurements behind one metric, newest first — provenance for every number on a card.</summary>
public sealed record GetMetricValuesQuery(Guid ResourceId, int Take) : IQuery<IReadOnlyList<MetricValueDto>>;

public sealed class GetMetricValuesHandler(LooperDbContext db) : IQueryHandler<GetMetricValuesQuery, IReadOnlyList<MetricValueDto>>
{
    public async Task<IReadOnlyList<MetricValueDto>> Handle(GetMetricValuesQuery query, CancellationToken cancellationToken)
    {
        if (!await db.Resources.AnyAsync(r => r.Id == query.ResourceId, cancellationToken))
        {
            throw new NotFoundException("Metric", query.ResourceId);
        }

        var values = await db.MetricValues.AsNoTracking()
            .Where(v => v.ResourceId == query.ResourceId)
            .OrderByDescending(v => v.RecordedAtUtc)
            .Take(Math.Clamp(query.Take, 1, 1000))
            .ToListAsync(cancellationToken);

        var agentIds = values.Where(v => v.AgentId != null).Select(v => v.AgentId!.Value).Distinct().ToList();
        var names = await db.Agents.AsNoTracking()
            .Where(a => agentIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);

        return values.Select(v => v.ToDto(v.AgentId is { } id ? names.GetValueOrDefault(id) : null)).ToList();
    }
}

public sealed class GetMetricValuesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/metrics/{id:guid}/values", (Guid id, int? take, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetMetricValuesQuery(id, take ?? 200), ct));
}

/// <summary>Removes one measurement — a mis-report, a duplicate, a test entry.</summary>
public sealed record DeleteMetricValueCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteMetricValueHandler(LooperDbContext db) : ICommandHandler<DeleteMetricValueCommand, bool>
{
    public async Task<bool> Handle(DeleteMetricValueCommand command, CancellationToken cancellationToken)
    {
        var value = await db.MetricValues.FirstOrDefaultAsync(v => v.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Metric value", command.Id);
        db.MetricValues.Remove(value);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class DeleteMetricValueEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/api/metrics/values/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            await dispatcher.Send(new DeleteMetricValueCommand(id), ct);
            return Results.NoContent();
        });
}
