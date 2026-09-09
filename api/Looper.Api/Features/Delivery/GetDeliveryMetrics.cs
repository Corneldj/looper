using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Delivery;

/// <summary>
/// The five delivery metrics — output volume is meaningless when agents can generate unlimited
/// plausible work, so these measure value that stuck. Dry-run (simulated) runs are excluded
/// throughout: pretend spend must not flatter or damn real conversion.
/// </summary>
public sealed record GetDeliveryMetricsQuery(int Days, Guid? WorkflowId = null) : IQuery<DeliveryMetricsDto>;

public sealed class GetDeliveryMetricsHandler(LooperDbContext db)
    : IQueryHandler<GetDeliveryMetricsQuery, DeliveryMetricsDto>
{
    public async Task<DeliveryMetricsDto> Handle(GetDeliveryMetricsQuery query, CancellationToken cancellationToken)
    {
        var days = Math.Clamp(query.Days, 1, 365);
        var since = DateTime.UtcNow.AddDays(-days);

        // Real (non-dry) completed runs in the window: the spend and escalation base.
        var workflowId = query.WorkflowId;
        var runs = await db.Runs.AsNoTracking()
            .Where(r => r.StartedAtUtc >= since && !r.DryRun && r.Status != RunStatus.Running)
            .Where(r => workflowId == null || r.Agent!.WorkflowId == workflowId)
            .Select(r => new { r.AgentId, r.CostUsd, r.Escalated })
            .ToListAsync(cancellationToken);

        var prs = await db.PullRequests.AsNoTracking()
            .Where(pr => pr.OpenedAtUtc >= since
                || (pr.MergedAtUtc != null && pr.MergedAtUtc >= since)
                || pr.Status == PrStatus.Open)
            .Where(pr => workflowId == null || pr.Agent.WorkflowId == workflowId)
            .ToListAsync(cancellationToken);

        var mergedInWindow = prs.Where(pr => pr.Status == PrStatus.Merged && pr.MergedAtUtc >= since).ToList();
        var reviewedInWindow = prs.Where(pr =>
            (pr.Status == PrStatus.Merged && pr.MergedAtUtc >= since) ||
            (pr.Status == PrStatus.Closed && pr.ClosedAtUtc >= since)).ToList();

        var totalCost = runs.Sum(r => r.CostUsd);
        var completedRuns = runs.Count;
        var escalatedRuns = runs.Count(r => r.Escalated);

        // 1. Cost per merged PR — whether agent spend converts into shipped work.
        decimal? costPerMerged = mergedInWindow.Count > 0 ? totalCost / mergedInWindow.Count : null;

        // 2. First-pass success — whether the harness is good, or humans are quietly fixing everything.
        double? firstPassRate = mergedInWindow.Count > 0
            ? mergedInWindow.Count(pr => pr.FirstPass == true) / (double)mergedInWindow.Count
            : null;

        // 3. Code survival — whether agent output lasts, or gets rewritten next sprint.
        var checkedPrs = mergedInWindow.Where(pr => pr.SurvivalCheckedAtUtc != null && pr.Additions > 0).ToList();
        var checkedAdditions = checkedPrs.Sum(pr => pr.Additions);
        double? survivalRate = checkedAdditions > 0
            ? checkedPrs.Sum(pr => (long)(pr.SurvivingAdditions ?? 0)) / (double)checkedAdditions
            : null;

        // 4. Review churn per unit of change — whether "faster to produce" became "slower to accept".
        var changedLines = reviewedInWindow.Sum(pr => (long)pr.Additions + pr.Deletions);
        double? churn = changedLines > 0
            ? reviewedInWindow.Sum(pr => (long)pr.ReviewRounds + pr.ReviewComments) / (double)changedLines * 100
            : null;

        // 5. Escalation rate — whether autonomy levels are set correctly.
        double? escalationRate = completedRuns > 0 ? escalatedRuns / (double)completedRuns : null;

        var agents = await BuildAgentRows(runs
            .GroupBy(r => r.AgentId)
            .ToDictionary(g => g.Key, g => (Cost: g.Sum(r => r.CostUsd), Completed: g.Count(), Escalated: g.Count(r => r.Escalated))),
            mergedInWindow.GroupBy(pr => pr.AgentId).ToDictionary(g => g.Key, g => g.ToList()),
            cancellationToken);

        return new DeliveryMetricsDto(
            days,
            totalCost,
            mergedInWindow.Count,
            prs.Count(pr => pr.Status == PrStatus.Open),
            prs.Count(pr => pr.Status == PrStatus.Closed && pr.ClosedAtUtc >= since),
            costPerMerged,
            firstPassRate,
            survivalRate,
            checkedPrs.Count,
            churn,
            escalationRate,
            escalatedRuns,
            completedRuns,
            agents);
    }

    private async Task<List<AgentDeliveryRowDto>> BuildAgentRows(
        Dictionary<Guid, (decimal Cost, int Completed, int Escalated)> runsByAgent,
        Dictionary<Guid, List<AgentPullRequest>> mergedByAgent,
        CancellationToken cancellationToken)
    {
        var agentIds = runsByAgent.Keys.Union(mergedByAgent.Keys).ToList();
        var agents = await db.Agents.AsNoTracking()
            .Where(a => agentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.Name, a.AutonomyLevel })
            .ToListAsync(cancellationToken);

        var rows = new List<AgentDeliveryRowDto>();
        foreach (var agent in agents)
        {
            var (cost, completed, escalated) = runsByAgent.GetValueOrDefault(agent.Id);
            var merged = mergedByAgent.GetValueOrDefault(agent.Id) ?? [];

            double? firstPass = merged.Count > 0 ? merged.Count(pr => pr.FirstPass == true) / (double)merged.Count : null;
            double? escalationRate = completed > 0 ? escalated / (double)completed : null;

            rows.Add(new AgentDeliveryRowDto(
                agent.Id,
                agent.Name,
                agent.AutonomyLevel,
                merged.Count,
                cost,
                merged.Count > 0 ? cost / merged.Count : null,
                firstPass,
                escalationRate,
                completed,
                Recommend(agent.AutonomyLevel, merged.Count, completed, firstPass, escalationRate)));
        }

        return rows.OrderByDescending(r => r.CostUsd).ToList();
    }

    /// <summary>
    /// Autonomy is a dial: promote on evidence (high first-pass, low escalation over enough
    /// volume), demote as readily. Advisory only — the human moves the dial.
    /// </summary>
    private static string? Recommend(int level, int mergedPrs, int completedRuns,
        double? firstPass, double? escalationRate)
    {
        if (completedRuns < 3) return null; // not enough evidence either way

        if ((firstPass is < 0.5 && mergedPrs >= 2) || escalationRate is > 0.3)
        {
            return level > 1 ? "demote" : "hold";
        }
        if (firstPass is >= 0.8 && mergedPrs >= 5 && escalationRate is <= 0.1)
        {
            return level < 4 ? "promote" : "hold";
        }
        return "hold";
    }
}

public sealed class GetDeliveryMetricsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/delivery/metrics", (int? days, Guid? workflowId, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetDeliveryMetricsQuery(days ?? 30, workflowId), ct));
}
