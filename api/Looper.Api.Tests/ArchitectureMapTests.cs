using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Looper.Api.Tests;

/// <summary>GET /api/architecture/map — the workspace rendered as one resources→agents graph.</summary>
public class ArchitectureMapTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    // Local mirrors of MapResourceDto / MapAgentDto (camelCase over the wire, enums as strings).
    private sealed record MapResource(
        Guid Id, string Name, string Type, string? CustomTypeKey, string Icon, string TypeLabel,
        List<Guid> AgentIds);
    private sealed record MapAgent(
        Guid Id, string Name, string Model, int AutonomyLevel, bool Enabled, bool DryRun, bool IsRunning,
        int IntervalMinutes, string? LastRunStatus, int RunsLast24h, decimal CostLast24hUsd,
        int OpenPrs, int MergedPrs, List<Guid> ResourceIds);
    private sealed record MapResponse(List<MapResource> Resources, List<MapAgent> Agents);

    private sealed record CreatedResource(Guid Id);
    private sealed record CreatedAgent(Guid Id);
    private sealed record CreatedPr(Guid Id);

    private async Task<MapResponse> GetMap()
    {
        var map = await _client.GetFromJsonAsync<MapResponse>("/api/architecture/map", TestJson.Options);
        Assert.NotNull(map);
        return map!;
    }

    private async Task<Guid> CreateRuleResource(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/resources", new
        {
            name,
            type = "Rule",
            description = "",
            configJson = """{"text":"x"}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedResource>(TestJson.Options))!.Id;
    }

    private async Task<Guid> CreateAgent(string name, params Guid[] resourceIds)
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name,
            description = "architecture map test agent",
            prompt = "Run one loop iteration and report.",
            model = "claude-sonnet-5",
            effort = "Medium",
            intervalMinutes = 60,
            maxTurns = 5,
            maxBudgetUsd = (decimal?)null,
            workingDirectory = (string?)null,
            allowedTools = (string?)null,
            bypassPermissions = true,
            dryRun = true,
            autonomyLevel = 3,
            resourceIds
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedAgent>(TestJson.Options))!.Id;
    }

    private async Task DeleteAgent(Guid id) =>
        (await _client.DeleteAsync($"/api/agents/{id}")).EnsureSuccessStatusCode();

    private async Task DeleteResource(Guid id) =>
        (await _client.DeleteAsync($"/api/resources/{id}")).EnsureSuccessStatusCode();

    [Fact]
    public async Task Empty_workspace_yields_an_empty_map()
    {
        var response = await _client.GetAsync("/api/architecture/map");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var map = await response.Content.ReadFromJsonAsync<MapResponse>(TestJson.Options);
        Assert.NotNull(map);
        Assert.Empty(map!.Resources);
        Assert.Empty(map.Agents);
    }

    [Fact]
    public async Task Rule_resource_and_its_agent_are_linked_both_ways()
    {
        Guid? ruleId = null, agentId = null;
        try
        {
            ruleId = await CreateRuleResource("House rules");
            agentId = await CreateAgent("Rule follower", ruleId.Value);

            var map = await GetMap();

            var resource = Assert.Single(map.Resources, r => r.Id == ruleId);
            Assert.Equal("📏", resource.Icon);
            Assert.Equal("Rule", resource.TypeLabel);
            Assert.Contains(agentId.Value, resource.AgentIds);

            var agent = Assert.Single(map.Agents, a => a.Id == agentId);
            Assert.Contains(ruleId.Value, agent.ResourceIds);
            Assert.Equal(3, agent.AutonomyLevel);
            Assert.False(agent.Enabled);
            Assert.False(agent.IsRunning);
            Assert.Equal(0, agent.OpenPrs);
            Assert.Equal(0, agent.MergedPrs);
        }
        finally
        {
            if (agentId is { } aid) await DeleteAgent(aid);
            if (ruleId is { } rid) await DeleteResource(rid);
        }
    }

    [Fact]
    public async Task Merged_pull_request_shows_up_in_the_agent_delivery_counts()
    {
        Guid? agentId = null;
        try
        {
            agentId = await CreateAgent("PR shipper");

            var register = await _client.PostAsJsonAsync("/api/delivery/prs", new
            {
                agentId,
                url = "https://github.com/o/r/pull/9",
                title = "t"
            }, TestJson.Options);
            Assert.Equal(HttpStatusCode.Created, register.StatusCode);
            var pr = await register.Content.ReadFromJsonAsync<CreatedPr>(TestJson.Options);

            var merge = await _client.PutAsJsonAsync($"/api/delivery/prs/{pr!.Id}", new
            {
                title = "t",
                status = "Merged",
                additions = 0,
                deletions = 0,
                reviewRounds = 0,
                reviewComments = 0,
                humanCommits = 0,
                repoPath = (string?)null,
                mergeCommitSha = (string?)null
            }, TestJson.Options);
            merge.EnsureSuccessStatusCode();

            var map = await GetMap();
            var agent = Assert.Single(map.Agents, a => a.Id == agentId);
            Assert.Equal(1, agent.MergedPrs);
            Assert.Equal(0, agent.OpenPrs);
        }
        finally
        {
            // Deleting the agent cascades to its pull requests.
            if (agentId is { } aid) await DeleteAgent(aid);
        }
    }

    [Fact]
    public async Task Built_in_graph_module_supplies_icon_and_label_for_custom_resources()
    {
        var graphDir = Directory.CreateTempSubdirectory("looper-map-knowledge-").FullName;
        Guid? resourceId = null;
        try
        {
            var create = await _client.PostAsJsonAsync("/api/resources", new
            {
                name = "Domain graph",
                type = "Custom",
                customTypeKey = "KnowledgeGraph",
                description = "",
                configJson = JsonSerializer.Serialize(new { path = graphDir })
            }, TestJson.Options);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            resourceId = (await create.Content.ReadFromJsonAsync<CreatedResource>(TestJson.Options))!.Id;

            var map = await GetMap();
            var resource = Assert.Single(map.Resources, r => r.Id == resourceId);
            Assert.Equal("Custom", resource.Type);
            Assert.Equal("KnowledgeGraph", resource.CustomTypeKey);
            Assert.Equal("🕸️", resource.Icon);
            Assert.Equal("Knowledge Graph", resource.TypeLabel);
        }
        finally
        {
            if (resourceId is { } rid) await DeleteResource(rid);
            Directory.Delete(graphDir, recursive: true);
        }
    }
}
