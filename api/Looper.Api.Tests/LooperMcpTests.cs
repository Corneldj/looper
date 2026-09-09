using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Infrastructure.Mcp;
using Looper.Api.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Looper.Api.Tests;

// ============================================================================
// Resources are tools, not skills: a run gets an MCP server whose tool set is a
// pure function of the attached resources, and every call goes through the same
// commands the HTTP API uses. The protocol is exercised over real HTTP, the way
// Claude Code speaks it.
// ============================================================================

public sealed class LooperToolsetTests
{
    private static Resource Custom(string typeKey, string name, string config = "{}") =>
        new() { Name = name, Type = ResourceType.Custom, CustomTypeKey = typeKey, ConfigJson = config };

    [Fact]
    public void The_tool_set_is_a_function_of_the_attached_resources()
    {
        Assert.Equal(["report_deliverable", "escalate", "raise_event"], LooperTools.ForRun([]).Select(t => t.Name));

        var resources = new List<Resource>
        {
            new() { Name = "Ask me", Type = ResourceType.UserAction, ConfigJson = """{"instructions":"Only for credentials."}""" },
            Custom("Metric", "Sign-ups", """{"unit":"sign-ups","aggregation":"sum","direction":"higher"}"""),
            Custom("Metric", "Bounce rate", """{"unit":"%","aggregation":"latest","direction":"lower"}"""),
            new() { Name = "Features", Type = ResourceType.WorkspacePool, ConfigJson = """{"rootPath":"/tmp/looper-pool"}""" },
            Custom("Specification", "Checkout spec", """{"specId":"SPEC-1","content":"AC-1 Works."}""")
        };
        var tools = LooperTools.ForRun(resources);

        Assert.Equal(["report_deliverable", "escalate", "raise_event", "ask_user", "record_metric", "claim_workspace", "finish_workspace", "list_workspaces"],
            tools.Select(t => t.Name));
        Assert.Contains("Only for credentials.", tools.Single(t => t.Name == "ask_user").Description);
        Assert.Contains("REQUIRED", tools.Single(t => t.Name == "report_deliverable").Description);   // a spec is attached

        var metric = tools.Single(t => t.Name == "record_metric");
        Assert.Equal(["Bounce rate", "Sign-ups"], metric.InputSchema["properties"]!["metric"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("values add up", metric.Description);

        var claim = tools.Single(t => t.Name == "claim_workspace");
        Assert.Equal(["unit"], claim.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));   // one pool: no need to name it
    }

    [Fact]
    public void Every_real_run_gets_the_looper_server_first_and_keeps_its_tools_when_a_tool_allowlist_is_set()
    {
        var mine = new Resource { Name = "looper", Type = ResourceType.McpServer, ConfigJson = """{"transport":"http","url":"http://example/mcp"}""" };
        var json = ClaudeCliExecutor.BuildMcpConfig([mine], [], "http://localhost:5210/mcp/runs/abc")!;
        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal("http://localhost:5210/mcp/runs/abc", servers.GetProperty("looper").GetProperty("url").GetString());
        Assert.True(servers.TryGetProperty("looper-x", out _));   // the user's server of the same name is renamed, not ours

        Assert.Null(ClaudeCliExecutor.BuildMcpConfig([], []));     // nothing to configure without a run
        Assert.Equal("mcp__looper__", LooperTools.QualifiedName(""));
    }

    [Fact]
    public void Prompts_point_at_tools_and_never_at_curl()
    {
        var agent = new LoopAgent { Name = "Worker", Prompt = "Base task." };
        var resources = new Resource[]
        {
            new() { Name = "Ask me", Type = ResourceType.UserAction, ConfigJson = "{}" },
            new() { Name = "Features", Type = ResourceType.WorkspacePool, ConfigJson = """{"rootPath":"/tmp/looper-pool"}""" }
        };
        var prompt = ClaudeCliExecutor.BuildPrompt(agent, resources, []);
        Assert.Contains("ask_user", prompt);
        Assert.Contains("claim_workspace", prompt);
        Assert.DoesNotContain("curl", prompt);
        Assert.DoesNotContain("curl", ClaudeCliExecutor.DeliveryProtocol);
        Assert.Contains("report_deliverable", ClaudeCliExecutor.DeliveryProtocol);
    }
}

public class LooperMcpApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private async Task<Guid> PostId(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body, TestJson.Options);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestJson.Options)).GetProperty("id").GetGuid();
    }

    /// <summary>An agent with the given resources and a run row for it — what the CLI would be talking on behalf of.</summary>
    private async Task<(Guid AgentId, Guid RunId)> SeedRun(params Guid[] resourceIds)
    {
        var agentId = await PostId("/api/agents", new
        {
            name = $"Tool user {Guid.NewGuid():N}"[..20], description = "", prompt = "Do things.", model = "claude-opus-5", effort = "High",
            intervalMinutes = 60, triggerMode = "Scheduled", triggerTopics = (string?)null, maxTurns = 10, maxBudgetUsd = (decimal?)null,
            workingDirectory = (string?)null, allowedTools = (string?)null, bypassPermissions = true, dryRun = false, autonomyLevel = 2,
            resourceIds
        });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LooperDbContext>();
        var run = new AgentRun { AgentId = agentId, Trigger = RunTrigger.Manual, Model = "claude-opus-5" };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return (agentId, run.Id);
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> Rpc(Guid runId, string method, object? parameters = null, object? id = null)
    {
        var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null) message["id"] = id;
        if (parameters is not null) message["params"] = parameters;
        var response = await _client.PostAsJsonAsync($"/mcp/runs/{runId}", message);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task A_run_offers_exactly_the_tools_its_resources_grant_and_calls_go_through_the_real_commands()
    {
        var ask = await PostId("/api/resources", new { name = "Ask me", type = "UserAction", description = "", configJson = "{}" });
        var metric = await PostId("/api/resources", new { name = "Sign-ups", type = "Custom", customTypeKey = "Metric", description = "", configJson = """{"unit":"sign-ups","aggregation":"sum","direction":"higher"}""" });
        var (agentId, runId) = await SeedRun(ask, metric);
        try
        {
            // Handshake, the way Claude Code opens the connection.
            var (status, init) = await Rpc(runId, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "claude-code", version = "2.1" } }, id: 1);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("2025-06-18", init!["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Equal("looper", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            Assert.Equal(HttpStatusCode.Accepted, (await Rpc(runId, "notifications/initialized")).Status);   // a notification: no body

            var (_, list) = await Rpc(runId, "tools/list", id: 2);
            var names = list!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
            Assert.Equal(["report_deliverable", "escalate", "raise_event", "ask_user", "record_metric"], names);
            Assert.DoesNotContain("claim_workspace", names);   // no pool attached

            // ask_user creates the same request the HTTP endpoint would, bound to this run and agent.
            var (_, asked) = await Rpc(runId, "tools/call", new { name = "ask_user", arguments = new { title = "Which region?", details = "eu-west-1 or us-east-1" } }, id: 3);
            Assert.False(asked!["result"]!["isError"]!.GetValue<bool>());
            var requests = await _client.GetFromJsonAsync<List<JsonElement>>($"/api/user-actions?agentId={agentId}", TestJson.Options);
            var request = Assert.Single(requests!);
            Assert.Equal("Which region?", request.GetProperty("title").GetString());
            Assert.Equal(runId, request.GetProperty("runId").GetGuid());

            // record_metric lands a value on the dashboard's metric.
            var (_, recorded) = await Rpc(runId, "tools/call", new { name = "record_metric", arguments = new { metric = "Sign-ups", value = 12, note = "from the list" } }, id: 4);
            Assert.False(recorded!["result"]!["isError"]!.GetValue<bool>());
            var values = await _client.GetFromJsonAsync<List<JsonElement>>($"/api/metrics/{metric}/values", TestJson.Options);
            Assert.Equal(12, Assert.Single(values!).GetProperty("value").GetDouble());

            // Mistakes come back as tool errors the model can read, not as crashes.
            var (_, wrongMetric) = await Rpc(runId, "tools/call", new { name = "record_metric", arguments = new { metric = "Sign-ups", value = "many" } }, id: 5);
            Assert.True(wrongMetric!["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("must be a number", wrongMetric["result"]!["content"]![0]!["text"]!.GetValue<string>());

            var (_, badTopic) = await Rpc(runId, "tools/call", new { name = "raise_event", arguments = new { topic = "Not A Topic" } }, id: 6);
            Assert.True(badTopic!["result"]!["isError"]!.GetValue<bool>());
            Assert.StartsWith("Refused:", badTopic["result"]!["content"]![0]!["text"]!.GetValue<string>());

            var (_, unknown) = await Rpc(runId, "tools/call", new { name = "claim_workspace", arguments = new { unit = "x" } }, id: 7);
            Assert.True(unknown!["result"]!["isError"]!.GetValue<bool>());
            Assert.Contains("This run offers:", unknown["result"]!["content"]![0]!["text"]!.GetValue<string>());

            var (_, noMethod) = await Rpc(runId, "resources/list", id: 8);
            Assert.Equal(-32601, noMethod!["error"]!["code"]!.GetValue<int>());

            // escalate marks the run.
            var (_, escalated) = await Rpc(runId, "tools/call", new { name = "escalate", arguments = new { reason = "Need the API key." } }, id: 9);
            Assert.False(escalated!["result"]!["isError"]!.GetValue<bool>());
            var runs = await _client.GetFromJsonAsync<List<JsonElement>>($"/api/agents/{agentId}/runs", TestJson.Options);
            Assert.True(runs!.Single(r => r.GetProperty("id").GetGuid() == runId).GetProperty("escalated").GetBoolean());
        }
        finally
        {
            await _client.DeleteAsync($"/api/agents/{agentId}");
            await _client.DeleteAsync($"/api/resources/{ask}");
            await _client.DeleteAsync($"/api/resources/{metric}");
        }
    }

    [Fact]
    public async Task Transport_edges_fail_closed()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"/mcp/runs/{Guid.NewGuid()}", new { jsonrpc = "2.0", id = 1, method = "ping" })).StatusCode);

        var (_, runId) = await SeedRun();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.GetAsync($"/mcp/runs/{runId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/mcp/runs/{runId}")).StatusCode);

        var garbage = await _client.PostAsync($"/mcp/runs/{runId}", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.Equal(-32700, JsonNode.Parse(await garbage.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<int>());

        var (status, pong) = await Rpc(runId, "ping", id: "p1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("p1", pong!["id"]!.GetValue<string>());
    }
}
