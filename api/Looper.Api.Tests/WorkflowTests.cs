using System.Net;
using System.Net.Http.Json;
using Looper.Api.Domain;

namespace Looper.Api.Tests;

/// <summary>
/// Workflows: one workbench each. Resources, agents and metrics belong to exactly one;
/// everything pre-existing sits in the seeded default; lists and dashboards filter by it.
/// </summary>
public class WorkflowApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record WorkflowResponse(Guid Id, string Name, string Description, bool IsDefault, int AgentCount, int ResourceCount);
    private sealed record IdResponse(Guid Id, Guid WorkflowId);
    private sealed record AgentResponse(Guid Id, Guid WorkflowId, List<Guid> ResourceIds);
    private sealed record SummaryResponse(int TotalAgents, int ActiveAgents);
    private sealed record MapResponse(List<IdResponse> Resources, List<IdResponse> Agents);
    private sealed record MetricResponse(Guid ResourceId, string Name);

    private async Task<Guid> CreateWorkflow(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/workflows", new { name, description = "test" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkflowResponse>(TestJson.Options))!.Id;
    }

    private async Task<IdResponse> CreateRule(string name, Guid? workflowId)
    {
        var response = await _client.PostAsJsonAsync("/api/resources", new
        {
            name, type = "Rule", description = "", configJson = """{"text":"x"}""", workflowId
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!;
    }

    private Task<HttpResponseMessage> PostAgent(string name, Guid? workflowId, params Guid[] resourceIds) =>
        _client.PostAsJsonAsync("/api/agents", new
        {
            name, description = "", prompt = "Loop.", model = "claude-haiku-4-5", effort = "Low", intervalMinutes = 60,
            triggerMode = "Scheduled", triggerTopics = (string?)null, maxTurns = 5, maxBudgetUsd = (decimal?)null,
            workingDirectory = (string?)null, allowedTools = (string?)null, bypassPermissions = true,
            dryRun = true, autonomyLevel = 2, resourceIds, workflowId
        }, TestJson.Options);

    [Fact]
    public async Task The_default_workflow_exists_and_cannot_be_deleted()
    {
        var workflows = await _client.GetFromJsonAsync<List<WorkflowResponse>>("/api/workflows", TestJson.Options);
        var def = Assert.Single(workflows!, w => w.IsDefault);
        Assert.Equal(Workflow.DefaultId, def.Id);
        Assert.Equal("Default", def.Name);
        Assert.Same(def, workflows![0]);   // listed first

        var delete = await _client.DeleteAsync($"/api/workflows/{Workflow.DefaultId}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        // Renaming it is fine.
        var rename = await _client.PutAsJsonAsync($"/api/workflows/{Workflow.DefaultId}", new { name = "Default", description = "Everything from before." }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
    }

    [Fact]
    public async Task Items_land_in_the_default_workflow_unless_told_otherwise_and_lists_filter_by_it()
    {
        var marketing = await CreateWorkflow("Marketing");
        var defaultRule = await CreateRule("Default rule", null);
        var marketingRule = await CreateRule("Marketing rule", marketing);
        var agent = await (await PostAgent("Campaign loop", marketing, marketingRule.Id)).Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options);
        try
        {
            Assert.Equal(Workflow.DefaultId, defaultRule.WorkflowId);
            Assert.Equal(marketing, marketingRule.WorkflowId);
            Assert.Equal(marketing, agent!.WorkflowId);

            var marketingResources = await _client.GetFromJsonAsync<List<IdResponse>>($"/api/resources?workflowId={marketing}", TestJson.Options);
            Assert.Equal([marketingRule.Id], marketingResources!.Select(r => r.Id));
            Assert.Contains(await _client.GetFromJsonAsync<List<IdResponse>>($"/api/resources?workflowId={Workflow.DefaultId}", TestJson.Options) ?? [], r => r.Id == defaultRule.Id);

            var marketingAgents = await _client.GetFromJsonAsync<List<AgentResponse>>($"/api/agents?workflowId={marketing}", TestJson.Options);
            Assert.Equal([agent.Id], marketingAgents!.Select(a => a.Id));
            Assert.DoesNotContain(await _client.GetFromJsonAsync<List<AgentResponse>>($"/api/agents?workflowId={Workflow.DefaultId}", TestJson.Options) ?? [], a => a.Id == agent.Id);

            var map = await _client.GetFromJsonAsync<MapResponse>($"/api/architecture/map?workflowId={marketing}", TestJson.Options);
            Assert.Equal([marketingRule.Id], map!.Resources.Select(r => r.Id));
            Assert.Equal([agent.Id], map.Agents.Select(a => a.Id));

            var summary = await _client.GetFromJsonAsync<SummaryResponse>($"/api/dashboard/summary?days=7&workflowId={marketing}", TestJson.Options);
            Assert.Equal(1, summary!.TotalAgents);

            var counts = Assert.Single((await _client.GetFromJsonAsync<List<WorkflowResponse>>("/api/workflows", TestJson.Options))!, w => w.Id == marketing);
            Assert.Equal(1, counts.AgentCount);
            Assert.Equal(1, counts.ResourceCount);
        }
        finally
        {
            await _client.DeleteAsync($"/api/resources/{defaultRule.Id}");
            await _client.DeleteAsync($"/api/workflows/{marketing}");
        }

        // Deleting the workflow took its agent and resource with it.
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/agents/{agent!.Id}")).StatusCode);
        Assert.DoesNotContain(await _client.GetFromJsonAsync<List<IdResponse>>("/api/resources", TestJson.Options) ?? [], r => r.Id == marketingRule.Id);
    }

    [Fact]
    public async Task A_workflow_is_a_closed_set_for_wiring()
    {
        var support = await CreateWorkflow("Support");
        var defaultRule = await CreateRule("Shared-looking rule", null);
        try
        {
            // Creating an agent in one workflow with a resource from another is refused …
            var crossed = await PostAgent("Support loop", support, defaultRule.Id);
            Assert.Equal(HttpStatusCode.BadRequest, crossed.StatusCode);
            Assert.Contains("another workflow", await crossed.Content.ReadAsStringAsync());

            // … and so is wiring one in later from the canvas.
            var agent = await (await PostAgent("Support loop", support)).Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options);
            var attach = await _client.PostAsync($"/api/agents/{agent!.Id}/resources/{defaultRule.Id}", null);
            Assert.Equal(HttpStatusCode.BadRequest, attach.StatusCode);

            // Metrics follow their workflow too.
            var metric = await _client.PostAsJsonAsync("/api/resources", new
            {
                name = "Tickets closed", type = "Custom", customTypeKey = "Metric", description = "", configJson = """{"aggregation":"sum"}""", workflowId = support
            }, TestJson.Options);
            Assert.Equal(HttpStatusCode.Created, metric.StatusCode);
            var supportMetrics = await _client.GetFromJsonAsync<List<MetricResponse>>($"/api/metrics?workflowId={support}", TestJson.Options);
            Assert.Equal(["Tickets closed"], supportMetrics!.Select(m => m.Name));
            Assert.DoesNotContain(await _client.GetFromJsonAsync<List<MetricResponse>>($"/api/metrics?workflowId={Workflow.DefaultId}", TestJson.Options) ?? [], m => m.Name == "Tickets closed");

            Assert.Equal(HttpStatusCode.NotFound, (await CreateRuleExpecting(Guid.NewGuid())).StatusCode);
        }
        finally
        {
            await _client.DeleteAsync($"/api/resources/{defaultRule.Id}");
            await _client.DeleteAsync($"/api/workflows/{support}");
        }
    }

    private Task<HttpResponseMessage> CreateRuleExpecting(Guid workflowId) =>
        _client.PostAsJsonAsync("/api/resources", new { name = "Orphan", type = "Rule", description = "", configJson = """{"text":"x"}""", workflowId }, TestJson.Options);

    [Fact]
    public async Task Workflow_validation_and_not_found()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/workflows", new { name = "", description = "" }, TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsJsonAsync($"/api/workflows/{Guid.NewGuid()}", new { name = "x", description = "" }, TestJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/workflows/{Guid.NewGuid()}")).StatusCode);
    }
}
