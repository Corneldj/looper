using System.Globalization;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Dashboard;

public sealed record GetCostSeriesQuery(int Days) : IQuery<IReadOnlyList<CostSeriesPointDto>>;

public sealed class GetCostSeriesHandler(LooperDbContext db)
    : IQueryHandler<GetCostSeriesQuery, IReadOnlyList<CostSeriesPointDto>>
{
    public async Task<IReadOnlyList<CostSeriesPointDto>> Handle(GetCostSeriesQuery query, CancellationToken cancellationToken)
    {
        var (_, fromUtc, toUtc) = DashboardWindow.Resolve(query.Days);

        // Date grouping in SQL is fragile on SQLite, so fetch a projection and group client-side.
        var runs = await db.Runs
            .Where(r => r.StartedAtUtc >= fromUtc && r.StartedAtUtc < toUtc)
            .Select(r => new { r.StartedAtUtc, r.CostUsd, r.Status })
            .ToListAsync(cancellationToken);

        var byDate = runs.GroupBy(r => r.StartedAtUtc.Date).ToDictionary(g => g.Key);

        var points = new List<CostSeriesPointDto>();
        for (var date = fromUtc.Date; date <= toUtc.Date; date = date.AddDays(1))
        {
            byDate.TryGetValue(date, out var group);
            points.Add(new CostSeriesPointDto(
                Date: date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                CostUsd: group?.Sum(r => r.CostUsd) ?? 0,
                Runs: group?.Count() ?? 0,
                Failures: group?.Count(r => r.Status == RunStatus.Failed || r.Status == RunStatus.TimedOut) ?? 0));
        }

        return points;
    }
}

public sealed class GetCostSeriesEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/dashboard/cost-series", (int? days, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetCostSeriesQuery(days ?? 14), ct));
}
