using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Agents;

public sealed record AgentSummaryDto(
    Guid Id,
    string Name,
    string Description,
    string Model,
    EffortLevel Effort,
    int IntervalMinutes,
    bool Enabled,
    bool DryRun,
    int AutonomyLevel,
    bool IsRunning,
    int ResourceCount,
    DateTime? LastRunAtUtc,
    DateTime? NextRunAtUtc,
    RunStatus? LastRunStatus,
    int RunsLast24h,
    decimal CostLast24hUsd);

public sealed record AgentDetailDto(
    Guid Id,
    string Name,
    string Description,
    string Model,
    EffortLevel Effort,
    int IntervalMinutes,
    bool Enabled,
    bool DryRun,
    int AutonomyLevel,
    bool IsRunning,
    int ResourceCount,
    DateTime? LastRunAtUtc,
    DateTime? NextRunAtUtc,
    RunStatus? LastRunStatus,
    int RunsLast24h,
    decimal CostLast24hUsd,
    string Prompt,
    int MaxTurns,
    decimal? MaxBudgetUsd,
    string? WorkingDirectory,
    string? AllowedTools,
    bool BypassPermissions,
    List<Guid> ResourceIds,
    DateTime CreatedAtUtc);

/// <summary>Shared request body for creating and updating an agent.</summary>
public sealed record SaveAgentRequest(
    string Name,
    string Description,
    string Prompt,
    string Model,
    EffortLevel Effort,
    int IntervalMinutes,
    int MaxTurns,
    decimal? MaxBudgetUsd,
    string? WorkingDirectory,
    string? AllowedTools,
    bool BypassPermissions,
    bool DryRun,
    int AutonomyLevel,
    List<Guid> ResourceIds);

/// <summary>Run-derived figures shown alongside an agent: latest outcome and 24h activity.</summary>
public sealed record AgentRunStats(RunStatus? LastRunStatus, int RunsLast24h, decimal CostLast24hUsd)
{
    public static readonly AgentRunStats None = new(null, 0, 0m);
}

public static class AgentMapper
{
    public static AgentSummaryDto ToSummaryDto(this LoopAgent agent, bool isRunning, int resourceCount, AgentRunStats stats) => new(
        agent.Id,
        agent.Name,
        agent.Description,
        agent.Model,
        agent.Effort,
        agent.IntervalMinutes,
        agent.Enabled,
        agent.DryRun,
        agent.AutonomyLevel,
        isRunning,
        resourceCount,
        agent.LastRunAtUtc,
        agent.NextRunAtUtc,
        stats.LastRunStatus,
        stats.RunsLast24h,
        stats.CostLast24hUsd);

    /// <summary>Requires <see cref="LoopAgent.Resources"/> to be loaded.</summary>
    public static AgentDetailDto ToDetailDto(this LoopAgent agent, bool isRunning, AgentRunStats stats) => new(
        agent.Id,
        agent.Name,
        agent.Description,
        agent.Model,
        agent.Effort,
        agent.IntervalMinutes,
        agent.Enabled,
        agent.DryRun,
        agent.AutonomyLevel,
        isRunning,
        agent.Resources.Count,
        agent.LastRunAtUtc,
        agent.NextRunAtUtc,
        stats.LastRunStatus,
        stats.RunsLast24h,
        stats.CostLast24hUsd,
        agent.Prompt,
        agent.MaxTurns,
        agent.MaxBudgetUsd,
        agent.WorkingDirectory,
        agent.AllowedTools,
        agent.BypassPermissions,
        agent.Resources.Select(r => r.Id).ToList(),
        agent.CreatedAtUtc);

    /// <summary>Copies the editable fields onto the entity. Scheduling state (Enabled/NextRunAtUtc) is not touched.</summary>
    public static void Apply(this SaveAgentRequest request, LoopAgent agent)
    {
        agent.Name = request.Name.Trim();
        agent.Description = request.Description.Trim();
        agent.AutonomyLevel = Math.Clamp(request.AutonomyLevel, 1, 4);
        agent.Prompt = request.Prompt;
        agent.Model = request.Model.Trim();
        agent.Effort = request.Effort;
        agent.IntervalMinutes = request.IntervalMinutes;
        agent.MaxTurns = request.MaxTurns;
        agent.MaxBudgetUsd = request.MaxBudgetUsd;
        agent.WorkingDirectory = Normalize(request.WorkingDirectory);
        agent.AllowedTools = Normalize(request.AllowedTools);
        agent.BypassPermissions = request.BypassPermissions;
        agent.DryRun = request.DryRun;
    }

    public static async Task<AgentRunStats> QueryRunStatsAsync(LooperDbContext db, Guid agentId, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var stats = await db.Agents
            .Where(a => a.Id == agentId)
            .Select(a => new
            {
                LastRunStatus = a.Runs.OrderByDescending(r => r.StartedAtUtc)
                    .Select(r => (RunStatus?)r.Status).FirstOrDefault(),
                RunsLast24h = a.Runs.Count(r => r.StartedAtUtc >= since),
                // SQLite cannot aggregate decimals; sum as double and convert back.
                CostLast24h = a.Runs.Where(r => r.StartedAtUtc >= since).Sum(r => (double)r.CostUsd)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return stats is null
            ? AgentRunStats.None
            : new AgentRunStats(stats.LastRunStatus, stats.RunsLast24h, (decimal)stats.CostLast24h);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
