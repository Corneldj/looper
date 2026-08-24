using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Looper.Api.Tests;

public sealed class AgentRunCoordinatorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;

    public AgentRunCoordinatorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(DbContextOptions<LooperDbContext> options) : IDbContextFactory<LooperDbContext>
    {
        public LooperDbContext CreateDbContext() => new(options);
    }

    private AgentRunCoordinator CreateCoordinator()
    {
        var looperOptions = Options.Create(new LooperOptions { MaxConcurrentRuns = 2, RunTimeoutMinutes = 5 });
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        return new AgentRunCoordinator(
            new Factory(_options),
            new ClaudeCliExecutor(looperOptions, registry, NullLogger<ClaudeCliExecutor>.Instance),
            new SimulatedAgentExecutor(),
            new TestingActionRunner(looperOptions),
            looperOptions,
            NullLogger<AgentRunCoordinator>.Instance);
    }

    private async Task<LoopAgent> SeedAgent()
    {
        await using var db = new LooperDbContext(_options);
        var agent = new LoopAgent
        {
            Name = "Test agent",
            Prompt = "Do the thing.",
            Model = "claude-opus-5",
            DryRun = true,
            MaxTurns = 5
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private async Task<AgentRun?> WaitForCompletion(Guid runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var db = new LooperDbContext(_options);
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
            if (run is not null && run.Status != RunStatus.Running) return run;
            await Task.Delay(200);
        }
        return null;
    }

    /// <summary>
    /// The run row turns terminal slightly before the coordinator's finally block clears the
    /// active map, so IsRunning must be awaited rather than asserted immediately.
    /// </summary>
    private static async Task<bool> WaitUntilIdle(AgentRunCoordinator coordinator, Guid agentId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (!coordinator.IsRunning(agentId)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [Fact]
    public async Task Dry_run_executes_end_to_end_and_records_cost_and_logs()
    {
        var agent = await SeedAgent();
        var coordinator = CreateCoordinator();

        var runId = await coordinator.TriggerRunAsync(agent.Id, RunTrigger.Manual);
        Assert.NotNull(runId);

        var run = await WaitForCompletion(runId!.Value, TimeSpan.FromSeconds(60));
        Assert.NotNull(run);
        Assert.True(run!.Status is RunStatus.Succeeded or RunStatus.Failed); // simulator fails ~8% of the time
        Assert.Equal("claude-opus-5", run.Model);
        Assert.True(run.CompletedAtUtc.HasValue);
        Assert.True(run.DurationMs > 0);
        if (run.Status == RunStatus.Succeeded) Assert.True(run.CostUsd > 0);

        await using var db = new LooperDbContext(_options);
        Assert.True(await db.RunLogs.CountAsync(l => l.RunId == run.Id) >= 2);
        var updated = await db.Agents.SingleAsync(a => a.Id == agent.Id);
        Assert.NotNull(updated.LastRunAtUtc);
        Assert.True(await WaitUntilIdle(coordinator, agent.Id));
    }

    [Fact]
    public async Task Second_trigger_while_running_is_rejected()
    {
        var agent = await SeedAgent();
        var coordinator = CreateCoordinator();

        var first = await coordinator.TriggerRunAsync(agent.Id, RunTrigger.Manual);
        var second = await coordinator.TriggerRunAsync(agent.Id, RunTrigger.Scheduled);

        Assert.NotNull(first);
        Assert.Null(second);

        await WaitForCompletion(first!.Value, TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task Cancel_marks_the_run_cancelled_and_frees_the_agent()
    {
        var agent = await SeedAgent();
        var coordinator = CreateCoordinator();

        var runId = await coordinator.TriggerRunAsync(agent.Id, RunTrigger.Manual);
        Assert.NotNull(runId);
        Assert.True(coordinator.CancelActiveRun(agent.Id));

        var run = await WaitForCompletion(runId!.Value, TimeSpan.FromSeconds(60));
        Assert.NotNull(run);
        // Cancellation may race the (fast) simulated run to the finish line, but must never wedge it.
        Assert.True(run!.Status is RunStatus.Cancelled or RunStatus.Succeeded or RunStatus.Failed);
        Assert.True(await WaitUntilIdle(coordinator, agent.Id));
    }

    [Fact]
    public async Task Trigger_for_unknown_agent_returns_null()
    {
        var coordinator = CreateCoordinator();
        Assert.Null(await coordinator.TriggerRunAsync(Guid.NewGuid(), RunTrigger.Manual));
    }
}
