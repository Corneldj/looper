using System.Net;
using System.Net.Http.Json;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;

namespace Looper.Api.Tests;

// ============================================================================
// Spec-to-PR traceability: the Specification resource carries REQ/AC identifiers,
// and the PR-registration gate verifies every citation against them.
// ============================================================================

public class SpecificationParserTests
{
    [Fact]
    public void Extracts_distinct_acceptance_criteria_in_declaration_order()
    {
        var acs = SpecificationTraceability.ExtractAcs(
            "REQ-1 The cart persists.\n  AC-2 survives restart\n  ac-1 works logged out\nAC-2 again\nAC-10 bulk");

        Assert.Equal(["AC-2", "AC-1", "AC-10"], acs);
    }

    [Fact]
    public void Citations_parse_from_loose_agent_input()
    {
        Assert.Equal(["AC-1", "AC-3"], SpecificationTraceability.ParseCitations("ac-1, AC-3 AC-3"));
        Assert.Empty(SpecificationTraceability.ParseCitations(null));
        Assert.Empty(SpecificationTraceability.ParseCitations("REQ-1"));   // requirements are not acceptance criteria
        Assert.Empty(SpecificationTraceability.ParseCitations("AC-"));     // an id needs a number
    }

    [Fact]
    public void A_spec_file_on_disk_contributes_identifiers_too()
    {
        var file = Path.Combine(Path.GetTempPath(), $"looper-spec-{Guid.NewGuid():N}.md");
        File.WriteAllText(file, "## Checkout\nAC-7: totals match the cart.");
        try
        {
            var config = new SpecificationConfig("SPEC-1", "AC-1 inline criterion", file, Advisory: false);
            Assert.Equal(["AC-1", "AC-7"], SpecificationTraceability.AcceptanceCriteria(config));
        }
        finally
        {
            File.Delete(file);
        }
    }
}

public class SpecificationModuleTests
{
    private static ResourceContribution Contribute(string json) =>
        new SpecificationModule().Contribute(new ResourceModuleContext(json));

    [Fact]
    public void An_enforced_spec_injects_the_contract_and_the_citation_mandate()
    {
        var contribution = Contribute(
            """{"specId":"SPEC-CHECKOUT-1","content":"REQ-1 Cart persists.\nAC-1 Survives restart.\nAC-2 Works logged out."}""");

        var section = Assert.Single(contribution.PromptSections);
        Assert.Contains("SPECIFICATION SPEC-CHECKOUT-1", section);
        Assert.Contains("AC-1, AC-2", section);                 // the parsed identifier inventory
        Assert.Contains("TRACEABILITY (enforced)", section);
        Assert.Contains("\\\"satisfies\\\"", section);          // the extended registration curl
        Assert.Contains("REJECTS", section);
        Assert.Contains("spec gap", section);                   // uncovered work is raised, not mis-cited
    }

    [Fact]
    public void An_advisory_spec_recommends_without_the_rejection_clause()
    {
        var section = Contribute("""{"specId":"SPEC-2","content":"AC-1 x","advisory":true}""").PromptSections[0];

        Assert.Contains("TRACEABILITY (advisory)", section);
        Assert.DoesNotContain("REJECTS", section);
    }

    [Fact]
    public void A_spec_path_grants_read_access_and_the_env_var()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"looper-specdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var contribution = Contribute($$"""{"specId":"SPEC-3","path":"{{dir.Replace("\\", "\\\\")}}"}""");
            Assert.Equal(dir, contribution.EnvironmentVariables["LOOPER_SPEC_PATH"]);
            Assert.Contains(dir, contribution.AdditionalDirectories);
        }
        finally
        {
            Directory.Delete(dir);
        }
    }

    [Fact]
    public void A_spec_without_declared_criteria_tells_the_agent_to_flag_it()
    {
        var section = Contribute("""{"specId":"SPEC-4","content":"Some prose without identifiers."}""").PromptSections[0];
        Assert.Contains("declares no AC-n identifiers", section);
    }

    [Fact]
    public void An_empty_config_contributes_nothing()
    {
        var contribution = Contribute("{}");
        Assert.Empty(contribution.PromptSections);
        Assert.Empty(contribution.EnvironmentVariables);
    }
}

public class SpecificationGateApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record IdResponse(Guid Id);
    private sealed record PrResponse(Guid Id, string? SatisfiesAcs);

    private async Task<Guid> CreateSpec(string configJson, string name)
    {
        var response = await _client.PostAsJsonAsync("/api/resources", new
        {
            name,
            type = "Custom",
            customTypeKey = "Specification",
            description = "",
            configJson
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
    }

    private async Task<Guid> CreateAgent(string name, params Guid[] resourceIds)
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name,
            description = "",
            prompt = "Build to spec.",
            model = "claude-haiku-4-5",
            effort = "Low",
            intervalMinutes = 60,
            triggerMode = "Scheduled",
            triggerTopics = (string?)null,
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
        return (await response.Content.ReadFromJsonAsync<IdResponse>(TestJson.Options))!.Id;
    }

    private Task<HttpResponseMessage> Register(Guid agentId, string? satisfies, string? url = null) =>
        _client.PostAsJsonAsync("/api/delivery/prs", new
        {
            agentId,
            title = "Implement cart persistence",
            url,
            satisfies
        }, TestJson.Options);

    [Fact]
    public async Task Specification_is_a_built_in_type_in_the_catalog()
    {
        var types = await _client.GetFromJsonAsync<List<Dictionary<string, object>>>("/api/resource-types", TestJson.Options);
        Assert.Contains(types!, t => t["typeKey"]?.ToString() == "Specification");
    }

    [Fact]
    public async Task The_gate_rejects_uncited_and_uncovered_citations_and_accepts_valid_ones()
    {
        var specId = await CreateSpec(
            """{"specId":"SPEC-CART-1","content":"REQ-1 Cart persists.\nAC-1 Survives restart.\nAC-2 Works logged out."}""",
            "Cart spec");
        var agentId = await CreateAgent("Spec-bound agent", specId);
        try
        {
            // No citation: rejected, and the error teaches the valid vocabulary.
            var uncited = await Register(agentId, null);
            Assert.Equal(HttpStatusCode.BadRequest, uncited.StatusCode);
            var uncitedBody = await uncited.Content.ReadAsStringAsync();
            Assert.Contains("SPEC-CART-1", uncitedBody);
            Assert.Contains("AC-1", uncitedBody);

            // A citation the spec never declared: rejected, named.
            var invented = await Register(agentId, "AC-9");
            Assert.Equal(HttpStatusCode.BadRequest, invented.StatusCode);
            Assert.Contains("AC-9", await invented.Content.ReadAsStringAsync());

            // Valid citations: accepted, normalized, stored.
            var ok = await Register(agentId, "ac-2, AC-1", "https://github.com/x/y/pull/1");
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
            var pr = await ok.Content.ReadFromJsonAsync<PrResponse>(TestJson.Options);
            Assert.Equal("AC-2, AC-1", pr!.SatisfiesAcs);

            // Re-registering the same PR unions the citations instead of overwriting.
            var again = await Register(agentId, "AC-1", "https://github.com/x/y/pull/1");
            Assert.Equal(HttpStatusCode.Created, again.StatusCode);
            var updated = await again.Content.ReadFromJsonAsync<PrResponse>(TestJson.Options);
            Assert.Equal(pr.Id, updated!.Id);
            Assert.Equal("AC-2, AC-1", updated.SatisfiesAcs);
        }
        finally
        {
            await _client.DeleteAsync($"/api/agents/{agentId}");
            await _client.DeleteAsync($"/api/resources/{specId}");
        }
    }

    [Fact]
    public async Task An_advisory_spec_lets_uncited_prs_through()
    {
        var specId = await CreateSpec("""{"specId":"SPEC-ADV-1","content":"AC-1 x.","advisory":true}""", "Advisory spec");
        var agentId = await CreateAgent("Advisory agent", specId);
        try
        {
            var response = await Register(agentId, null);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            // But citations, when given, are still verified.
            var invented = await Register(agentId, "AC-5");
            Assert.Equal(HttpStatusCode.BadRequest, invented.StatusCode);
        }
        finally
        {
            await _client.DeleteAsync($"/api/agents/{agentId}");
            await _client.DeleteAsync($"/api/resources/{specId}");
        }
    }

    [Fact]
    public async Task An_agent_without_a_spec_is_unaffected()
    {
        var agentId = await CreateAgent("Free agent");
        try
        {
            var response = await Register(agentId, null);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally
        {
            await _client.DeleteAsync($"/api/agents/{agentId}");
        }
    }
}
