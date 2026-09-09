using System.Net;
using System.Net.Http.Json;

namespace Looper.Api.Tests;

/// <summary>Single-edge wiring behind the workbench canvas: drag-to-connect and ✕-to-cut.</summary>
public class AgentWiringTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record IdResponse(Guid Id);
    private sealed record AgentResponse(Guid Id, List<Guid> ResourceIds, int ResourceCount);
    private sealed record MapResponse(List<MapResource> Resources, List<MapAgent> Agents);
    private sealed record MapResource(Guid Id, string Description, List<Guid> AgentIds);
    private sealed record MapAgent(Guid Id, string Description, string Effort, string TriggerMode,
        DateTime? NextRunAtUtc, List<Guid> ResourceIds);

    private async Task<Guid> CreateRule(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/resources", new
        {
            name, type = "Rule", description = "wiring test rule", configJson = """{"text":"Be nice."}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
    }

    private async Task<Guid> CreateAgent(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name, description = "wiring test agent", prompt = "Loop.", model = "claude-haiku-4-5", effort = "Low",
            intervalMinutes = 60, triggerMode = "Scheduled", triggerTopics = (string?)null, maxTurns = 5,
            maxBudgetUsd = (decimal?)null, workingDirectory = (string?)null, allowedTools = (string?)null,
            bypassPermissions = true, dryRun = true, autonomyLevel = 2, resourceIds = Array.Empty<Guid>()
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
    }

    [Fact]
    public async Task Attach_and_detach_one_edge_idempotently()
    {
        var ruleId = await CreateRule("Wiring rule");
        var agentId = await CreateAgent("Wiring agent");
        try
        {
            var attach = await _client.PostAsync($"/api/agents/{agentId}/resources/{ruleId}", null);
            Assert.Equal(HttpStatusCode.OK, attach.StatusCode);
            var attached = await attach.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options);
            Assert.Equal([ruleId], attached!.ResourceIds);
            Assert.Equal(1, attached.ResourceCount);

            // Attaching twice is a no-op, not a duplicate edge.
            var again = await _client.PostAsync($"/api/agents/{agentId}/resources/{ruleId}", null);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Single((await again.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options))!.ResourceIds);

            // The map reflects the edge from both ends, with the fields the canvas renders.
            var map = await _client.GetFromJsonAsync<MapResponse>("/api/architecture/map", TestJson.Options);
            var mapAgent = Assert.Single(map!.Agents, a => a.Id == agentId);
            Assert.Contains(ruleId, mapAgent.ResourceIds);
            Assert.Equal("wiring test agent", mapAgent.Description);
            Assert.Equal("Low", mapAgent.Effort);
            Assert.Equal("Scheduled", mapAgent.TriggerMode);
            var mapResource = Assert.Single(map.Resources, r => r.Id == ruleId);
            Assert.Contains(agentId, mapResource.AgentIds);
            Assert.Equal("wiring test rule", mapResource.Description);

            var detach = await _client.DeleteAsync($"/api/agents/{agentId}/resources/{ruleId}");
            Assert.Equal(HttpStatusCode.OK, detach.StatusCode);
            Assert.Empty((await detach.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options))!.ResourceIds);

            // Detaching what isn't attached is fine too.
            var detachAgain = await _client.DeleteAsync($"/api/agents/{agentId}/resources/{ruleId}");
            Assert.Equal(HttpStatusCode.OK, detachAgain.StatusCode);
        }
        finally
        {
            await _client.DeleteAsync($"/api/agents/{agentId}");
            await _client.DeleteAsync($"/api/resources/{ruleId}");
        }
    }

    [Fact]
    public async Task Unknown_endpoints_of_an_edge_are_404s()
    {
        var agentId = await CreateAgent("Lonely agent");
        try
        {
            var missingResource = await _client.PostAsync($"/api/agents/{agentId}/resources/{Guid.NewGuid()}", null);
            Assert.Equal(HttpStatusCode.NotFound, missingResource.StatusCode);

            var missingAgent = await _client.PostAsync($"/api/agents/{Guid.NewGuid()}/resources/{Guid.NewGuid()}", null);
            Assert.Equal(HttpStatusCode.NotFound, missingAgent.StatusCode);
        }
        finally
        {
            await _client.DeleteAsync($"/api/agents/{agentId}");
        }
    }
}
