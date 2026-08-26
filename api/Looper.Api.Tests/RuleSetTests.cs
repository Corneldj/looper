using System.Net;
using System.Net.Http.Json;
using Looper.Api.Domain;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;

namespace Looper.Api.Tests;

public class RuleSetTests
{
    private static Resource RuleSet(string configJson) =>
        new() { Type = ResourceType.RuleSet, ConfigJson = configJson };

    [Fact]
    public void Enabled_rules_join_the_system_prompt_in_order_and_disabled_ones_stay_out()
    {
        var resources = new List<Resource>
        {
            new() { Type = ResourceType.Rule, ConfigJson = """{"text":"Always write tests."}""" },
            RuleSet("""
                {"rules":[
                  {"text":"Never push to main.","enabled":true},
                  {"text":"Benched rule.","enabled":false},
                  {"text":"Keep commits small.","enabled":true}
                ]}
                """),
        };

        var rules = ClaudeCliExecutor.CollectRules(resources, []).ToList();

        Assert.Equal(["Always write tests.", "Never push to main.", "Keep commits small."], rules);
    }

    [Fact]
    public void Blank_and_malformed_entries_are_skipped_gracefully()
    {
        var resources = new List<Resource>
        {
            RuleSet("""{"rules":[{"text":"  ","enabled":true},{"text":"Real rule.","enabled":true}]}"""),
            RuleSet("not even json"),
            RuleSet("{}"),
        };

        Assert.Equal(["Real rule."], ClaudeCliExecutor.CollectRules(resources, []).ToList());
    }

    [Fact]
    public void Module_contributions_still_append_after_resource_rules()
    {
        var contribution = new ResourceContribution();
        contribution.SystemPromptRules.Add("From a module.");

        var rules = ClaudeCliExecutor.CollectRules(
            [RuleSet("""{"rules":[{"text":"First.","enabled":true}]}""")], [contribution]).ToList();

        Assert.Equal(["First.", "From a module."], rules);
    }
}

public class RuleSetApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record TypeResponse(string TypeKey, string Icon, bool BuiltIn);
    private sealed record ResourceResponse(Guid Id, string Type, string ConfigJson);

    [Fact]
    public async Task Rule_set_is_a_built_in_type_and_round_trips_its_rules()
    {
        var types = await _client.GetFromJsonAsync<List<TypeResponse>>("/api/resource-types", TestJson.Options);
        var ruleSet = Assert.Single(types!, t => t.TypeKey == "RuleSet");
        Assert.True(ruleSet.BuiltIn);
        Assert.Equal("📋", ruleSet.Icon);

        var create = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = "House rules",
            type = "RuleSet",
            description = "",
            configJson = """{"rules":[{"text":"Small commits.","enabled":true},{"text":"Old habit.","enabled":false}]}"""
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var resource = await create.Content.ReadFromJsonAsync<ResourceResponse>(TestJson.Options);
        Assert.Equal("RuleSet", resource!.Type);
        Assert.Contains("Small commits.", resource.ConfigJson);
        Assert.Contains("Old habit.", resource.ConfigJson); // disabled rules persist — benched, not deleted

        (await _client.DeleteAsync($"/api/resources/{resource.Id}")).EnsureSuccessStatusCode();
    }
}
