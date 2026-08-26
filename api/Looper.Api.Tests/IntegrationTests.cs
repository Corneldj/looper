using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Looper.Api.Tests;

/// <summary>Boots the real API (decorators, dispatcher, EF, scheduler) on a throwaway SQLite file.</summary>
public class LooperApiFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"looper-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Looper", $"Data Source={DbPath}");
        builder.UseSetting("Looper:SchedulerPollSeconds", "3600");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(DbPath); } catch (IOException) { }
    }
}

public static class TestJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

public class ResourcesApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record ResourceResponse(
        Guid Id, string Name, string Type, string? CustomTypeKey, string Description, string ConfigJson, int AgentCount);

    [Fact]
    public async Task Full_resource_lifecycle_masks_and_preserves_secrets()
    {
        // Create — the response must never contain the secret.
        var create = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "ADO PAT",
            type = "PatToken",
            description = "test token",
            configJson = """{"envVar":"ADO_PAT","value":"super-secret-123"}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<ResourceResponse>(TestJson.Options);
        Assert.NotNull(created);
        Assert.DoesNotContain("super-secret-123", created!.ConfigJson);
        Assert.Contains("__SECRET_UNCHANGED__", created.ConfigJson);

        // Update sending the sentinel back — the stored secret must survive.
        var update = await _client.PutAsJsonAsync($"/api/resources/{created.Id}", new
        {
            name = "ADO PAT renamed",
            description = "still a test",
            configJson = """{"envVar":"ADO_PAT","value":"__SECRET_UNCHANGED__"}"""
        }, TestJson.Options);
        update.EnsureSuccessStatusCode();

        await using (var connection = new SqliteConnection($"Data Source={factory.DbPath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT ConfigJson FROM Resources WHERE Id = @id";
            command.Parameters.AddWithValue("@id", created.Id.ToString().ToUpperInvariant());
            var stored = (string?)await command.ExecuteScalarAsync();
            Assert.NotNull(stored);
            Assert.Contains("super-secret-123", stored);
        }

        // List reflects the rename and stays masked.
        var list = await _client.GetFromJsonAsync<List<ResourceResponse>>("/api/resources", TestJson.Options);
        var item = Assert.Single(list!, r => r.Id == created.Id);
        Assert.Equal("ADO PAT renamed", item.Name);
        Assert.DoesNotContain("super-secret-123", item.ConfigJson);

        // Delete.
        var delete = await _client.DeleteAsync($"/api/resources/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.DoesNotContain(
            await _client.GetFromJsonAsync<List<ResourceResponse>>("/api/resources", TestJson.Options) ?? [],
            r => r.Id == created.Id);
    }

    [Fact]
    public async Task Invalid_resource_is_rejected_by_the_validation_decorator()
    {
        var response = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "",
            type = "Rule",
            description = "",
            configJson = "not json"
        }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_resource_returns_404()
    {
        var response = await _client.PutAsJsonAsync($"/api/resources/{Guid.NewGuid()}", new
        {
            name = "x", description = "", configJson = "{}"
        }, TestJson.Options);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

public class ResourceTypesApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record TypeResponse(string TypeKey, string Label, string Icon, string Blurb, bool BuiltIn,
        List<FieldResponse>? Fields);
    private sealed record FieldResponse(string Key, string Label, string Kind, bool Required);
    private sealed record InstallResponse(TypeResponse Type, string SourceCode, decimal CostUsd);
    private sealed record ResourceResponse(Guid Id, string Type, string? CustomTypeKey, string ConfigJson);

    [Fact]
    public async Task Dynamic_resource_type_installs_serves_forms_and_masks_its_secrets()
    {
        // The catalog starts with the eight built-ins.
        var initial = await _client.GetFromJsonAsync<List<TypeResponse>>("/api/resource-types", TestJson.Options);
        Assert.True(initial!.Count(t => t.BuiltIn) >= 8);

        // Install a module from source (same pipeline the AI generation uses after Claude writes the code).
        var install = await _client.PostAsJsonAsync("/api/resource-types",
            new { sourceCode = ResourceModuleCompilerTests.SampleModuleSource }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, install.StatusCode);
        var installed = await install.Content.ReadFromJsonAsync<InstallResponse>(TestJson.Options);
        Assert.Equal("SlackWebhook", installed!.Type.TypeKey);
        Assert.False(installed.Type.BuiltIn);
        Assert.Contains(installed.Type.Fields!, f => f is { Key: "webhookUrl", Kind: "Password", Required: true });

        // Duplicate install is rejected.
        var duplicate = await _client.PostAsJsonAsync("/api/resource-types",
            new { sourceCode = ResourceModuleCompilerTests.SampleModuleSource }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        // Create a resource of the new type — its Password field is masked in responses.
        var createResource = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "Team Slack",
            type = "Custom",
            customTypeKey = "SlackWebhook",
            description = "",
            configJson = """{"webhookUrl":"https://hooks.slack.example/secret-path","channel":"#loops"}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, createResource.StatusCode);
        var resource = await createResource.Content.ReadFromJsonAsync<ResourceResponse>(TestJson.Options);
        Assert.Equal("SlackWebhook", resource!.CustomTypeKey);
        Assert.DoesNotContain("secret-path", resource.ConfigJson);
        Assert.Contains("#loops", resource.ConfigJson);

        // A type that is in use cannot be removed.
        var deleteInUse = await _client.DeleteAsync("/api/resource-types/SlackWebhook");
        Assert.Equal(HttpStatusCode.Conflict, deleteInUse.StatusCode);

        // After deleting the resource the type can go, and leaves the catalog.
        (await _client.DeleteAsync($"/api/resources/{resource.Id}")).EnsureSuccessStatusCode();
        var deleteType = await _client.DeleteAsync("/api/resource-types/SlackWebhook");
        Assert.Equal(HttpStatusCode.NoContent, deleteType.StatusCode);
        var finalTypes = await _client.GetFromJsonAsync<List<TypeResponse>>("/api/resource-types", TestJson.Options);
        Assert.DoesNotContain(finalTypes!, t => t.TypeKey == "SlackWebhook");
    }

    [Fact]
    public async Task Broken_module_source_returns_the_compiler_errors()
    {
        var response = await _client.PostAsJsonAsync("/api/resource-types",
            new { sourceCode = "public class Nope : Looper.Api.Modules.IResourceTypeModule {" }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("error", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}

public class AgentsAndDashboardApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record AgentResponse(Guid Id, string Name, string Model, string Effort, int IntervalMinutes,
        bool Enabled, bool DryRun, bool IsRunning, DateTime? NextRunAtUtc, List<Guid>? ResourceIds);
    private sealed record RunResponse(Guid Id, string Status, decimal CostUsd, long InputTokens, long OutputTokens,
        int NumTurns, long DurationMs);
    private sealed record RunDetailResponse(Guid Id, string Status, string AgentName, List<LogResponse> Logs);
    private sealed record LogResponse(DateTime TimestampUtc, string Level, string Message);
    private sealed record SummaryResponse(decimal TotalCostUsd, int TotalRuns, double SuccessRate, int TotalAgents);
    private sealed record SeriesPoint(string Date, decimal CostUsd, int Runs, int Failures);
    private sealed record BreakdownRow(Guid AgentId, string Name, string Model, decimal CostUsd, int Runs);

    private async Task<AgentResponse> CreateAgent(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name,
            description = "integration test agent",
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
            resourceIds = Array.Empty<Guid>()
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options))!;
    }

    [Fact]
    public async Task Agent_lifecycle_run_history_and_dashboard_aggregation()
    {
        var agent = await CreateAgent("Loop smoke test");
        Assert.False(agent.Enabled);
        Assert.Equal("claude-sonnet-5", agent.Model);

        // Enabling schedules an immediate first run.
        var enable = await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/enabled", new { enabled = true }, TestJson.Options);
        enable.EnsureSuccessStatusCode();
        var enabled = await enable.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options);
        Assert.True(enabled!.Enabled);
        Assert.NotNull(enabled.NextRunAtUtc);
        (await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/enabled", new { enabled = false }, TestJson.Options))
            .EnsureSuccessStatusCode();

        // Manual dry run: completes with cost, tokens and logs (simulated — no tokens spent).
        var trigger = await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/run", new { }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);
        using var runIdDoc = JsonDocument.Parse(await trigger.Content.ReadAsStringAsync());
        var runId = runIdDoc.RootElement.GetProperty("runId").GetGuid();

        RunResponse? run = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var runs = await _client.GetFromJsonAsync<List<RunResponse>>($"/api/agents/{agent.Id}/runs", TestJson.Options);
            run = runs!.FirstOrDefault(r => r.Id == runId);
            if (run is not null && run.Status != "Running") break;
            await Task.Delay(300);
        }
        Assert.NotNull(run);
        Assert.True(run!.Status is "Succeeded" or "Failed", $"unexpected status {run.Status}");
        Assert.True(run.DurationMs > 0);

        var detail = await _client.GetFromJsonAsync<RunDetailResponse>($"/api/runs/{runId}", TestJson.Options);
        Assert.Equal("Loop smoke test", detail!.AgentName);
        Assert.NotEmpty(detail.Logs);

        // Dashboard reflects the run.
        var summary = await _client.GetFromJsonAsync<SummaryResponse>("/api/dashboard/summary?days=14", TestJson.Options);
        Assert.True(summary!.TotalRuns >= 1);
        Assert.True(summary.TotalAgents >= 1);

        var series = await _client.GetFromJsonAsync<List<SeriesPoint>>("/api/dashboard/cost-series?days=14", TestJson.Options);
        Assert.True(series!.Count >= 14, $"expected a filled series, got {series.Count} points");
        Assert.Equal(1, series.Count(p => p.Runs > 0)); // exactly today has the run

        var breakdown = await _client.GetFromJsonAsync<List<BreakdownRow>>("/api/dashboard/agent-breakdown?days=14", TestJson.Options);
        Assert.Contains(breakdown!, row => row.AgentId == agent.Id && row.Runs >= 1);

        // Cleanup.
        (await _client.DeleteAsync($"/api/agents/{agent.Id}")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Agent_without_a_prompt_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name = "No prompt",
            description = "",
            prompt = "",
            model = "claude-opus-5",
            effort = "High",
            intervalMinutes = 60,
            maxTurns = 10,
            bypassPermissions = true,
            autonomyLevel = 3,
            dryRun = true,
            resourceIds = Array.Empty<Guid>()
        }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Running_an_unknown_agent_returns_404()
    {
        var response = await _client.PostAsJsonAsync($"/api/agents/{Guid.NewGuid()}/run", new { }, TestJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
