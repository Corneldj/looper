using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Tests;

/// <summary>The prompt-side contract: how the executor teaches an agent to raise user action requests.</summary>
public class UserActionProtocolTests
{
    [Fact]
    public void Protocol_names_the_endpoint_the_run_id_and_that_raising_is_not_a_failure()
    {
        var protocol = ClaudeCliExecutor.BuildUserActionProtocol(new UserActionConfig());

        Assert.Contains("/api/user-actions", protocol);
        Assert.Contains("$LOOPER_RUN_ID", protocol);
        Assert.Contains("NOT", protocol);      // "Raising a request is NOT ..."
        Assert.Contains("failure", protocol);  // "... a failure"
    }

    [Fact]
    public void Protocol_appends_the_configured_guidance()
    {
        var protocol = ClaudeCliExecutor.BuildUserActionProtocol(new UserActionConfig
        {
            Instructions = "Only raise when a credential is missing."
        });

        Assert.Contains("Only raise when a credential is missing.", protocol);
    }

    [Fact]
    public void Prompt_with_a_user_action_resource_carries_the_protocol()
    {
        var agent = new LoopAgent { Name = "Worker", Prompt = "Base task." };
        var resources = new[]
        {
            new Resource { Name = "Ask me", Type = ResourceType.UserAction, ConfigJson = "{}" }
        };

        var prompt = ClaudeCliExecutor.BuildPrompt(agent, resources, Array.Empty<ResourceContribution>());

        Assert.StartsWith("Base task.", prompt, StringComparison.Ordinal);
        Assert.Contains("USER ACTION REQUESTS", prompt);
        Assert.Contains("/api/user-actions", prompt);
    }

    [Fact]
    public void The_protocol_says_answers_arrive_as_resources_not_as_carried_context()
    {
        var protocol = ClaudeCliExecutor.BuildUserActionProtocol(new UserActionConfig());

        Assert.Contains("clean context", protocol);
        Assert.Contains("never handed to you as a message", protocol);
        Assert.Contains("standing rule", protocol);
        Assert.Contains("raise a new request", protocol);
    }

    [Fact]
    public void A_recorded_decision_reaches_the_next_run_through_its_rule_set_like_any_other_rule()
    {
        // The deterministic path: the answer is a rule in "<agent> decisions", so CollectRules —
        // the same function every real run's system prompt is built from — carries it.
        var decisions = new Resource
        {
            Name = "Worker decisions", Type = ResourceType.RuleSet,
            ConfigJson = """{"rules":[{"text":"Use Postgres (the user's decision on: Which database?)","enabled":true}]}"""
        };

        var rules = ClaudeCliExecutor.CollectRules([decisions], []).ToList();

        Assert.Contains("Use Postgres (the user's decision on: Which database?)", rules);
        Assert.DoesNotContain("USER RESPONSES", ClaudeCliExecutor.BuildPrompt(new LoopAgent { Prompt = "Base task." }, [decisions], []));
    }
}

/// <summary>End-to-end behaviour of raise / list / resolve and how open requests gate the loop.</summary>
public class UserActionApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record AgentResponse(Guid Id, string Name, bool Enabled, DateTime? NextRunAtUtc);
    private sealed record ResourceResponse(Guid Id, string Type, string ConfigJson);
    private sealed record RunResponse(Guid Id, string Status, bool ActionRequested);
    private sealed record UserActionResponse(
        Guid Id, Guid AgentId, string AgentName, Guid? RunId, string Title, string Details,
        string Status, bool Blocking, string? Response, DateTime CreatedAtUtc, DateTime? ResolvedAtUtc,
        string? ResolutionNote, bool CanRecordToMemory);
    private sealed record RuleSetResponse(Guid Id, string Name, string Type, string ConfigJson);
    private sealed record AgentDetailResponse(Guid Id, List<Guid> ResourceIds);

    private async Task<AgentResponse> CreateAgent(string name, params Guid[] resourceIds)
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name,
            description = "user action test agent",
            prompt = "Run one loop iteration and report.",
            model = "claude-sonnet-5",
            effort = "Medium",
            intervalMinutes = 60,
            maxTurns = 5,
            maxBudgetUsd = (decimal?)null,
            workingDirectory = (string?)null,
            allowedTools = (string?)null,
            bypassPermissions = true,
            autonomyLevel = 3,
            dryRun = true,
            resourceIds
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options))!;
    }

    private async Task<UserActionResponse> Raise(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/user-actions", body, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserActionResponse>(TestJson.Options))!;
    }

    private async Task<List<UserActionResponse>> ListOpen(Guid agentId) =>
        (await _client.GetFromJsonAsync<List<UserActionResponse>>(
            $"/api/user-actions?agentId={agentId}", TestJson.Options))!;

    private async Task<RunResponse> WaitForRunCompletion(Guid agentId, Guid runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var runs = await _client.GetFromJsonAsync<List<RunResponse>>($"/api/agents/{agentId}/runs", TestJson.Options);
            var run = runs!.FirstOrDefault(r => r.Id == runId);
            if (run is not null && run.Status != "Running") return run;
            await Task.Delay(300);
        }
        Assert.Fail($"Run {runId} did not complete within 60s.");
        return null!; // unreachable
    }

    private async Task DeleteAgent(Guid agentId) =>
        (await _client.DeleteAsync($"/api/agents/{agentId}")).EnsureSuccessStatusCode();

    [Fact]
    public async Task Raise_defaults_to_blocking_and_re_raising_the_same_title_updates_instead_of_duplicating()
    {
        var agent = await CreateAgent("UA dedupe agent");
        try
        {
            var raised = await Raise(new
            {
                agentId = agent.Id,
                title = "Approve the deploy plan",
                details = "First details"
            });
            Assert.Equal(agent.Id, raised.AgentId);
            Assert.Equal("Open", raised.Status);
            Assert.True(raised.Blocking); // no UserAction resource attached -> default blocking
            Assert.Equal("Approve the deploy plan", raised.Title);

            var open = await ListOpen(agent.Id);
            var listed = Assert.Single(open);
            Assert.Equal(raised.Id, listed.Id);

            // Same title again: the open request is updated, not duplicated.
            var reRaised = await Raise(new
            {
                agentId = agent.Id,
                title = "Approve the deploy plan",
                details = "Updated details"
            });
            Assert.Equal(raised.Id, reRaised.Id);

            open = await ListOpen(agent.Id);
            listed = Assert.Single(open);
            Assert.Equal("Updated details", listed.Details);
        }
        finally
        {
            await DeleteAgent(agent.Id);
        }
    }

    [Fact]
    public async Task Open_blocking_request_gates_run_now_and_resolving_reopens_it()
    {
        var agent = await CreateAgent("UA gating agent");
        try
        {
            var raised = await Raise(new
            {
                agentId = agent.Id,
                title = "Provide the staging credentials",
                details = "Need the vault path."
            });

            // Run-now is refused while the loop is parked on a human, and says why.
            var blockedRun = await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/run", new { }, TestJson.Options);
            Assert.Equal(HttpStatusCode.BadRequest, blockedRun.StatusCode);
            Assert.Contains("Provide the staging credentials", await blockedRun.Content.ReadAsStringAsync());

            // Resolve with a response.
            var resolve = await _client.PostAsJsonAsync($"/api/user-actions/{raised.Id}/resolve",
                new { response = "Credentials are in the vault under /staging." }, TestJson.Options);
            resolve.EnsureSuccessStatusCode();
            var resolved = await resolve.Content.ReadFromJsonAsync<UserActionResponse>(TestJson.Options);
            Assert.Equal("Resolved", resolved!.Status);
            Assert.Equal("Credentials are in the vault under /staging.", resolved.Response);
            Assert.NotNull(resolved.ResolvedAtUtc);
            Assert.Contains("standing rule", resolved.ResolutionNote);
            Assert.Empty(await ListOpen(agent.Id));

            // With the request resolved, run-now goes through (dry run — simulated).
            var trigger = await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/run", new { }, TestJson.Options);
            Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);
            using var runIdDoc = JsonDocument.Parse(await trigger.Content.ReadAsStringAsync());
            var runId = runIdDoc.RootElement.GetProperty("runId").GetGuid();
            await WaitForRunCompletion(agent.Id, runId);
        }
        finally
        {
            await DeleteAgent(agent.Id);
        }
    }

    [Fact]
    public async Task An_answer_becomes_a_rule_in_the_agents_own_decisions_set_which_grows_on_every_decision()
    {
        var agent = await CreateAgent("UA decisions agent");
        try
        {
            var first = await Raise(new { agentId = agent.Id, title = "Which database?", details = "" });
            var resolved = await (await _client.PostAsJsonAsync($"/api/user-actions/{first.Id}/resolve",
                new { response = "Use Postgres.", recordAs = "rule" }, TestJson.Options)).Content.ReadFromJsonAsync<UserActionResponse>(TestJson.Options);
            Assert.Equal("Recorded as a standing rule in 'UA decisions agent decisions'.", resolved!.ResolutionNote);

            var second = await Raise(new { agentId = agent.Id, title = "Which region?", details = "" });
            (await _client.PostAsJsonAsync($"/api/user-actions/{second.Id}/resolve", new { response = "eu-west-1" }, TestJson.Options)).EnsureSuccessStatusCode();

            // One rule set, attached to the agent, carrying both decisions in order — the next run's system prompt.
            var sets = (await _client.GetFromJsonAsync<List<RuleSetResponse>>("/api/resources?type=RuleSet", TestJson.Options))!
                .Where(r => r.Name == "UA decisions agent decisions").ToList();
            var set = Assert.Single(sets);
            Assert.Contains("Use Postgres. (the user's decision on: Which database?)", set.ConfigJson);
            Assert.Contains("eu-west-1 (the user's decision on: Which region?)", set.ConfigJson);
            var detail = await _client.GetFromJsonAsync<AgentDetailResponse>($"/api/agents/{agent.Id}", TestJson.Options);
            Assert.Contains(set.Id, detail!.ResourceIds);

            // No answer, or "none": completed without touching any resource.
            var third = await Raise(new { agentId = agent.Id, title = "Rotate the key", details = "" });
            var silent = await (await _client.PostAsJsonAsync($"/api/user-actions/{third.Id}/resolve", new { response = "" }, TestJson.Options)).Content.ReadFromJsonAsync<UserActionResponse>(TestJson.Options);
            Assert.Contains("nothing recorded", silent!.ResolutionNote);
            var fourth = await Raise(new { agentId = agent.Id, title = "Approve the copy", details = "" });
            var kept = await (await _client.PostAsJsonAsync($"/api/user-actions/{fourth.Id}/resolve", new { response = "Approved.", recordAs = "none" }, TestJson.Options)).Content.ReadFromJsonAsync<UserActionResponse>(TestJson.Options);
            Assert.Contains("not handed to the agent", kept!.ResolutionNote);
            Assert.Equal(2, ResourceConfigRules((await _client.GetFromJsonAsync<List<RuleSetResponse>>("/api/resources?type=RuleSet", TestJson.Options))!.Single(r => r.Id == set.Id).ConfigJson));

            Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync($"/api/user-actions/{fourth.Id}/resolve", new { response = "x", recordAs = "somewhere" }, TestJson.Options)).StatusCode);
        }
        finally
        {
            await DeleteAgent(agent.Id);
            foreach (var set in (await _client.GetFromJsonAsync<List<RuleSetResponse>>("/api/resources?type=RuleSet", TestJson.Options))!.Where(r => r.Name == "UA decisions agent decisions"))
            {
                await _client.DeleteAsync($"/api/resources/{set.Id}");
            }
        }
    }

    private static int ResourceConfigRules(string configJson) =>
        JsonDocument.Parse(configJson).RootElement.GetProperty("rules").GetArrayLength();

    [Fact]
    public async Task An_answer_can_go_to_the_memory_graph_inbox_when_one_is_attached_and_nowhere_else_otherwise()
    {
        var graphDir = Path.Combine(Path.GetTempPath(), $"looper-ua-graph-{Guid.NewGuid():N}");
        var graph = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "Team memory", type = "Custom", customTypeKey = "MemoryGraph", description = "",
            configJson = System.Text.Json.JsonSerializer.Serialize(new { path = graphDir })
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, graph.StatusCode);
        var graphId = (await graph.Content.ReadFromJsonAsync<RuleSetResponse>(TestJson.Options))!.Id;
        var withGraph = await CreateAgent("UA memory agent", graphId);
        var without = await CreateAgent("UA plain agent");
        try
        {
            var raised = await Raise(new { agentId = withGraph.Id, title = "Tone of voice?", details = "" });
            Assert.True(raised.CanRecordToMemory);
            var resolved = await (await _client.PostAsJsonAsync($"/api/user-actions/{raised.Id}/resolve",
                new { response = "Friendly, never salesy.", recordAs = "memory" }, TestJson.Options)).Content.ReadFromJsonAsync<UserActionResponse>(TestJson.Options);
            Assert.Contains("inbox of 'Team memory'", resolved!.ResolutionNote);
            var inbox = Directory.GetFiles(Path.Combine(graphDir, "inbox"));
            Assert.Contains(inbox, f => File.ReadAllText(f).Contains("Friendly, never salesy."));

            var plain = await Raise(new { agentId = without.Id, title = "Tone of voice?", details = "" });
            Assert.False(plain.CanRecordToMemory);
            var refused = await _client.PostAsJsonAsync($"/api/user-actions/{plain.Id}/resolve", new { response = "x", recordAs = "memory" }, TestJson.Options);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("no memory graph", await refused.Content.ReadAsStringAsync());
        }
        finally
        {
            await DeleteAgent(withGraph.Id);
            await DeleteAgent(without.Id);
            await _client.DeleteAsync($"/api/resources/{graphId}");
            try { Directory.Delete(graphDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Resolving_on_a_disabled_agent_does_not_force_a_next_run()
    {
        var agent = await CreateAgent("UA disabled agent");
        try
        {
            Assert.False(agent.Enabled);
            var raised = await Raise(new { agentId = agent.Id, title = "Pick a name", details = "" });
            (await _client.PostAsJsonAsync($"/api/user-actions/{raised.Id}/resolve",
                new { response = "Call it Looper." }, TestJson.Options)).EnsureSuccessStatusCode();

            var agents = await _client.GetFromJsonAsync<List<AgentResponse>>("/api/agents", TestJson.Options);
            var current = Assert.Single(agents!, a => a.Id == agent.Id);
            Assert.Null(current.NextRunAtUtc); // disabled loops stay parked
        }
        finally
        {
            await DeleteAgent(agent.Id);
        }
    }

    [Fact]
    public async Task Resolving_on_an_enabled_agent_unparks_the_schedule()
    {
        var agent = await CreateAgent("UA unpark agent");
        try
        {
            // Enable: schedules an immediate tick, but the factory polls every 3600s so nothing runs.
            (await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/enabled", new { enabled = true }, TestJson.Options))
                .EnsureSuccessStatusCode();

            var raised = await Raise(new
            {
                agentId = agent.Id,
                title = "Choose the database vendor",
                details = "Postgres or SQLite?"
            });
            (await _client.PostAsJsonAsync($"/api/user-actions/{raised.Id}/resolve",
                new { response = "Postgres." }, TestJson.Options)).EnsureSuccessStatusCode();

            var agents = await _client.GetFromJsonAsync<List<AgentResponse>>("/api/agents", TestJson.Options);
            var current = Assert.Single(agents!, a => a.Id == agent.Id);
            Assert.NotNull(current.NextRunAtUtc);
            Assert.True(current.NextRunAtUtc > DateTime.UtcNow.AddMinutes(-1),
                $"expected an immediate next run, got {current.NextRunAtUtc:O}");
            Assert.True(current.NextRunAtUtc <= DateTime.UtcNow.AddMinutes(1));
        }
        finally
        {
            (await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/enabled", new { enabled = false }, TestJson.Options))
                .EnsureSuccessStatusCode();
            await DeleteAgent(agent.Id);
        }
    }

    [Fact]
    public async Task Raising_with_a_run_id_marks_the_run_as_action_requested()
    {
        var agent = await CreateAgent("UA run-marking agent");
        try
        {
            var trigger = await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/run", new { }, TestJson.Options);
            Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);
            using var runIdDoc = JsonDocument.Parse(await trigger.Content.ReadAsStringAsync());
            var runId = runIdDoc.RootElement.GetProperty("runId").GetGuid();
            await WaitForRunCompletion(agent.Id, runId);

            var raised = await Raise(new
            {
                runId,
                title = "Confirm the rollout window",
                details = "Weekend or weekday?"
            });
            Assert.Equal(agent.Id, raised.AgentId); // agent resolved from the run
            Assert.Equal(runId, raised.RunId);

            var runs = await _client.GetFromJsonAsync<List<RunResponse>>($"/api/agents/{agent.Id}/runs", TestJson.Options);
            var run = Assert.Single(runs!, r => r.Id == runId);
            Assert.True(run.ActionRequested);
        }
        finally
        {
            await DeleteAgent(agent.Id);
        }
    }

    [Fact]
    public async Task Non_blocking_config_raises_without_parking_the_loop()
    {
        var createResource = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "Ask sparingly",
            type = "UserAction",
            description = "",
            configJson = """{"blockScheduling":false,"instructions":"x"}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, createResource.StatusCode);
        var resource = (await createResource.Content.ReadFromJsonAsync<ResourceResponse>(TestJson.Options))!;

        AgentResponse? agent = null;
        try
        {
            agent = await CreateAgent("UA non-blocking agent", resource.Id);

            var raised = await Raise(new
            {
                agentId = agent.Id,
                title = "FYI: pick a logo when you get a chance",
                details = ""
            });
            Assert.False(raised.Blocking); // blockScheduling:false from the attached resource
            Assert.Equal("Open", raised.Status);

            // The open non-blocking request does not gate run-now.
            var trigger = await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/run", new { }, TestJson.Options);
            Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);
            using var runIdDoc = JsonDocument.Parse(await trigger.Content.ReadAsStringAsync());
            var runId = runIdDoc.RootElement.GetProperty("runId").GetGuid();
            await WaitForRunCompletion(agent.Id, runId);
        }
        finally
        {
            if (agent is not null) await DeleteAgent(agent.Id);
            (await _client.DeleteAsync($"/api/resources/{resource.Id}")).EnsureSuccessStatusCode();
        }
    }
}

/// <summary>The one question the scheduler and run-now ask, answered straight against the store.</summary>
public sealed class UserActionGateTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;

    public UserActionGateTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Only_an_open_blocking_request_blocks_the_agent()
    {
        Guid openBlockingAgentId, resolvedAgentId, openNonBlockingAgentId;
        await using (var db = new LooperDbContext(_options))
        {
            var openBlockingAgent = new LoopAgent { Name = "Blocked", Prompt = "p" };
            var resolvedAgent = new LoopAgent { Name = "Resolved", Prompt = "p" };
            var openNonBlockingAgent = new LoopAgent { Name = "Advisory", Prompt = "p" };
            db.Agents.AddRange(openBlockingAgent, resolvedAgent, openNonBlockingAgent);

            db.UserActionRequests.AddRange(
                new UserActionRequest
                {
                    Agent = openBlockingAgent, Title = "Open and blocking",
                    Status = UserActionStatus.Open, Blocking = true
                },
                new UserActionRequest
                {
                    Agent = resolvedAgent, Title = "Already handled",
                    Status = UserActionStatus.Resolved, Blocking = true,
                    ResolvedAtUtc = DateTime.UtcNow, Response = "done"
                },
                new UserActionRequest
                {
                    Agent = openNonBlockingAgent, Title = "Open but advisory",
                    Status = UserActionStatus.Open, Blocking = false
                });
            await db.SaveChangesAsync();

            openBlockingAgentId = openBlockingAgent.Id;
            resolvedAgentId = resolvedAgent.Id;
            openNonBlockingAgentId = openNonBlockingAgent.Id;
        }

        await using (var db = new LooperDbContext(_options))
        {
            Assert.True(await Features.UserActions.UserActionGate.IsBlockedAsync(db, openBlockingAgentId, CancellationToken.None));
            Assert.False(await Features.UserActions.UserActionGate.IsBlockedAsync(db, resolvedAgentId, CancellationToken.None));
            Assert.False(await Features.UserActions.UserActionGate.IsBlockedAsync(db, openNonBlockingAgentId, CancellationToken.None));
        }
    }
}
