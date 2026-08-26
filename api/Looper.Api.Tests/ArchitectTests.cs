using System.Net;
using System.Net.Http.Json;
using Looper.Api.Features.Architect;
using Microsoft.AspNetCore.Hosting;

namespace Looper.Api.Tests;

/// <summary>
/// The architect prompt is the contract handed to the builder AI: it must carry the user's
/// request, the workspace inventory, the API toolset, and the governance rules verbatim.
/// </summary>
public class ArchitectPromptTests
{
    private const string Description = "Set up a nightly docs sync workflow with a reviewer gate.";
    private const string InventoryJson = """{"resources":[{"id":"abc-123","name":"Docs folder"}],"agents":[]}""";
    private const string ApiUrl = "http://looper.test:5210";

    private static string Prompt() => BuildWorkflowHandler.BuildArchitectPrompt(Description, InventoryJson, ApiUrl);

    [Fact]
    public void Carries_the_users_description_verbatim()
    {
        Assert.Contains(Description, Prompt());
    }

    [Fact]
    public void Embeds_the_workspace_inventory_json_verbatim()
    {
        Assert.Contains(InventoryJson, Prompt());
    }

    [Fact]
    public void Points_the_curl_examples_at_the_configured_api_url()
    {
        var prompt = Prompt();

        Assert.Contains($"curl -s {ApiUrl}/api/resource-types", prompt);
        Assert.Contains($"curl -s -X POST {ApiUrl}/api/resources", prompt);
        Assert.Contains($"curl -s -X POST {ApiUrl}/api/agents", prompt);
        Assert.Contains($"curl -s -X POST {ApiUrl}/api/resource-types/generate", prompt);
    }

    [Theory]
    [InlineData("RuleSet")]
    [InlineData("Reviewer")]
    [InlineData("WorkspacePool")]
    [InlineData("TestingAction")]
    [InlineData("FileLocation")]
    public void Documents_the_built_in_config_shape_for(string typeKey)
    {
        Assert.Contains(typeKey, Prompt());
    }

    [Fact]
    public void Governance_mandates_dry_run_and_disabled_agents()
    {
        var prompt = Prompt();

        Assert.Contains("\"dryRun\": true", prompt);
        Assert.Contains("DISABLED", prompt);
    }

    [Fact]
    public void Governance_forbids_deletions_and_real_credentials()
    {
        var prompt = Prompt();

        // Additive-only toolset: creation is allowed, removal never is.
        Assert.Contains("never", prompt);
        Assert.Contains("delete", prompt);
        // Credential resources are stubbed, not invented.
        Assert.Contains("placeholder", prompt);
    }
}

public class BuildWorkflowValidatorTests
{
    [Fact]
    public void A_description_under_ten_characters_fails()
    {
        var result = new BuildWorkflowValidator().Validate(new BuildWorkflowCommand("short"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(BuildWorkflowCommand.Description));
    }

    [Fact]
    public void A_real_sentence_passes()
    {
        var result = new BuildWorkflowValidator().Validate(
            new BuildWorkflowCommand("Set up a docs sync workflow please"));

        Assert.True(result.IsValid);
    }
}

/// <summary>The architect endpoint with the Claude CLI pointed at a command that cannot start.</summary>
public sealed class ArchitectApiFactory : LooperApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Looper:ClaudeCommand", "definitely-not-a-real-command-xyz");
    }
}

public class ArchitectApiTests(ArchitectApiFactory factory) : IClassFixture<ArchitectApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record CreatedItem(Guid Id, string Name, string Detail);
    private sealed record ArchitectResult(
        bool Success,
        string Report,
        decimal CostUsd,
        List<CreatedItem> CreatedResources,
        List<CreatedItem> CreatedAgents,
        string? Error);
    private sealed record ResourceResponse(Guid Id, string Name);

    [Fact]
    public async Task Missing_cli_yields_a_clean_failure_result_not_an_error_status()
    {
        var response = await _client.PostAsJsonAsync("/api/architect/build",
            new { description = "Set up a docs sync workflow please" }, TestJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ArchitectResult>(TestJson.Options);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.NotNull(result.Error);
        Assert.Contains("definitely-not-a-real-command-xyz", result.Error);
        Assert.Contains("Install", result.Error);
        Assert.Empty(result.CreatedResources);
        Assert.Empty(result.CreatedAgents);
        Assert.Equal(0m, result.CostUsd);
    }

    [Fact]
    public async Task Too_short_a_description_is_rejected_by_the_validation_decorator()
    {
        var response = await _client.PostAsJsonAsync("/api/architect/build",
            new { description = "short" }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_diff_reports_only_items_created_during_the_run()
    {
        // A resource that exists BEFORE the architect runs must never show up as its work.
        var create = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "Pre-existing rule",
            type = "Rule",
            description = "created before the architect ran",
            configJson = """{"text":"Never push to main."}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var existing = (await create.Content.ReadFromJsonAsync<ResourceResponse>(TestJson.Options))!;

        try
        {
            var response = await _client.PostAsJsonAsync("/api/architect/build",
                new { description = "Set up a docs sync workflow please" }, TestJson.Options);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<ArchitectResult>(TestJson.Options);

            Assert.NotNull(result);
            Assert.False(result!.Success); // the stub CLI fails instantly, creating nothing
            Assert.DoesNotContain(result.CreatedResources, r => r.Id == existing.Id);
            Assert.Empty(result.CreatedResources);
        }
        finally
        {
            await _client.DeleteAsync($"/api/resources/{existing.Id}");
        }
    }
}
