namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// Dry-run executor: produces realistic cost/usage numbers without spawning the CLI or spending tokens.
/// Lets users rehearse loop cadence, budgets and dashboards before going live.
/// </summary>
public sealed class SimulatedAgentExecutor : IAgentExecutor
{
    private sealed record Pricing(decimal InputPerMTok, decimal OutputPerMTok);

    private static readonly Dictionary<string, Pricing> PriceTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5"] = new(10.00m, 50.00m),
        ["claude-opus-5"] = new(5.00m, 25.00m),
        ["claude-opus-4-8"] = new(5.00m, 25.00m),
        ["claude-sonnet-5"] = new(3.00m, 15.00m),
        ["claude-haiku-4-5"] = new(1.00m, 5.00m)
    };

    public async Task<AgentExecutionOutcome> ExecuteAsync(
        AgentExecutionContext context, RunLogWriter log, CancellationToken cancellationToken)
    {
        var agent = context.Agent;
        var random = Random.Shared;

        await log("info", $"[dry run] Simulating loop for '{agent.Name}' on {agent.Model} (effort {agent.Effort}). No tokens are spent.");

        var turns = random.Next(3, Math.Max(4, agent.MaxTurns));
        for (var turn = 1; turn <= Math.Min(turns, 4); turn++)
        {
            await Task.Delay(random.Next(300, 900), cancellationToken);
            await log("info", $"[dry run] Turn {turn}: tool activity simulated.");
        }

        var pricing = PriceTable.TryGetValue(agent.Model, out var found) ? found : new Pricing(5.00m, 25.00m);
        var effortMultiplier = 0.5 + (int)agent.Effort * 0.45;

        long inputTokens = (long)(random.Next(15_000, 90_000) * effortMultiplier);
        long outputTokens = (long)(random.Next(1_200, 7_000) * effortMultiplier);
        long cacheRead = random.Next(0, 40_000);
        var cost = Math.Round(
            inputTokens * pricing.InputPerMTok / 1_000_000m
            + outputTokens * pricing.OutputPerMTok / 1_000_000m
            + cacheRead * pricing.InputPerMTok * 0.1m / 1_000_000m, 6);

        if (agent.MaxBudgetUsd is { } budget and > 0 && cost > budget)
        {
            cost = budget;
            await log("warn", $"[dry run] Simulated cost clipped at the ${budget} run budget.");
        }

        var failed = random.NextDouble() < 0.08;
        if (failed)
        {
            await log("error", "[dry run] Simulated failure: a tool call errored mid-loop.");
            return new AgentExecutionOutcome(false, null, "Simulated failure (dry run).",
                cost / 2, inputTokens / 2, outputTokens / 3, cacheRead, 0, turns / 2, random.Next(20_000, 90_000));
        }

        await log("info", $"[dry run] Loop finished: {turns} turns, ~${cost:F4}.");
        return new AgentExecutionOutcome(true,
            $"Dry run completed for '{agent.Name}'. This simulated iteration would have executed: {Summarize(agent.Prompt)}",
            null, cost, inputTokens, outputTokens, cacheRead, random.Next(0, 8_000), turns,
            random.Next(30_000, 240_000));
    }

    private static string Summarize(string prompt)
    {
        var flattened = prompt.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= 160 ? flattened : flattened[..160] + "…";
    }
}
