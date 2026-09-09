using Looper.Api.Domain;
using Looper.Api.Features.Workflows;
using Looper.Api.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

// ============================================================================
// The Event Listener resource type is gone: an agent's "on events" trigger is the
// listener. Old databases and old packages are converted, never left dangling.
// ============================================================================

public sealed class LegacyEventListenerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly DbContextOptions<LooperDbContext> _options;

    public LegacyEventListenerTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static Resource Listener(string name, string pattern) => new()
    {
        Name = name, Type = ResourceType.Custom, CustomTypeKey = "EventListener", ConfigJson = $$"""{"topic":"{{pattern}}"}"""
    };

    [Fact]
    public async Task Startup_folds_listeners_into_event_agents_drops_them_from_scheduled_ones_and_removes_the_resources()
    {
        Guid eventAgentId, scheduledAgentId;
        await using (var db = new LooperDbContext(_options))
        {
            var shared = Listener("On newsletter", "newsletter.*");
            var eventAgent = new LoopAgent { Name = "Follow-up", Prompt = "p", TriggerMode = TriggerMode.Event, TriggerTopics = "prd.approved", Resources = { shared, Listener("On docs", "docs.updated") } };
            var scheduledAgent = new LoopAgent { Name = "Digest", Prompt = "p", TriggerMode = TriggerMode.Scheduled, IntervalMinutes = 60, Resources = { shared } };
            db.Agents.AddRange(eventAgent, scheduledAgent);
            await db.SaveChangesAsync();
            eventAgentId = eventAgent.Id;
            scheduledAgentId = scheduledAgent.Id;

            Assert.Equal(2, await LegacyEventListeners.RetireAsync(db, NullLogger.Instance, CancellationToken.None));
            Assert.Equal(0, await LegacyEventListeners.RetireAsync(db, NullLogger.Instance, CancellationToken.None)); // idempotent
        }

        await using (var db = new LooperDbContext(_options))
        {
            var eventAgent = await db.Agents.Include(a => a.Resources).SingleAsync(a => a.Id == eventAgentId);
            Assert.Equal("prd.approved\ndocs.updated\nnewsletter.*", eventAgent.TriggerTopics);   // listeners merge in name order
            Assert.Empty(eventAgent.Resources);

            var scheduledAgent = await db.Agents.Include(a => a.Resources).SingleAsync(a => a.Id == scheduledAgentId);
            Assert.Equal(TriggerMode.Scheduled, scheduledAgent.TriggerMode);
            Assert.Null(scheduledAgent.TriggerTopics);
            Assert.Empty(scheduledAgent.Resources);

            Assert.False(await db.Resources.AnyAsync(r => r.CustomTypeKey == "EventListener"));
        }
    }

    [Fact]
    public void An_old_package_is_converted_the_same_way_and_says_so()
    {
        var package = new WorkflowPackage(
            WorkflowPackage.FormatName, 1, DateTime.UtcNow, "Looper 0.9", new PackagedWorkflow("Old", ""),
            [new PackagedResourceType("EventListener", "Event Listener", "📡", "", "// built in back then", null, null)],
            [
                new PackagedResource("r1", "On newsletter", ResourceType.Custom, "EventListener", "", """{"topic":"newsletter.sent"}"""),
                new PackagedResource("r2", "House rules", ResourceType.RuleSet, null, "", "{}")
            ],
            [
                new PackagedAgent("a1", "Follow-up", "", "Go.", "claude-opus-5", EffortLevel.High, 60, TriggerMode.Event, null, 10, null, null, null, true, true, 2, ["r1", "r2"]),
                new PackagedAgent("a2", "Digest", "", "Go.", "claude-opus-5", EffortLevel.High, 60, TriggerMode.Scheduled, null, 10, null, null, null, true, true, 2, ["r1"])
            ],
            [new RedactedSecret("r1", "On newsletter", "nothing")]);
        var warnings = new List<string>();

        var converted = LegacyEventListeners.Retire(package, warnings);

        Assert.Equal(["r2"], converted.Resources.Select(r => r.Ref));
        Assert.Empty(converted.ResourceTypes);
        Assert.Empty(converted.RedactedSecrets);
        var follow = converted.Agents.Single(a => a.Ref == "a1");
        Assert.Equal("newsletter.sent", follow.TriggerTopics);
        Assert.Equal(["r2"], follow.ResourceRefs);
        var digest = converted.Agents.Single(a => a.Ref == "a2");
        Assert.Null(digest.TriggerTopics);
        Assert.Empty(digest.ResourceRefs);
        Assert.Contains(warnings, w => w.Contains("folded into agent 'Follow-up'"));
        Assert.Contains(warnings, w => w.Contains("scheduled agent 'Digest'") && w.Contains("dropped"));
        Assert.Same(converted, LegacyEventListeners.Retire(converted, warnings)); // nothing left to convert
    }
}
