using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Features.Resources;
using Looper.Api.Features.Workflows;
using Looper.Api.Modules;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

// ============================================================================
// Export / import: a workflow travels as one JSON package — resources (secrets
// stripped), agents, wiring, and the dynamic resource types with source + DLL —
// and comes back as a new workflow on any Looper, all or nothing.
// ============================================================================

public sealed class WorkflowPackageUnitTests
{
    [Theory]
    [InlineData("Marketing campaigns", "marketing-campaigns.looper-workflow.json")]
    [InlineData("  Support / Tier 2 ", "support-tier-2.looper-workflow.json")]
    [InlineData("***", "workflow.looper-workflow.json")]
    public void Package_file_names_are_slugs(string name, string expected) =>
        Assert.Equal(expected, WorkflowPackage.FileNameFor(name));

    [Fact]
    public void Redaction_blanks_secrets_and_reports_them_but_leaves_other_fields()
    {
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        var resource = new Resource { Type = ResourceType.PatToken, ConfigJson = """{"token":"ghp_secret","host":"github.com","value":""}""" };

        var (json, redacted) = SecretMasker.Redact(resource, registry);

        Assert.Equal(["token"], redacted);
        Assert.Contains("\"token\":\"\"", json);
        Assert.Contains("\"host\":\"github.com\"", json);
        Assert.DoesNotContain("ghp_secret", json);
    }
}

public class WorkflowPortabilityApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static string Module(string typeKey) =>
        ResourceModuleCompilerTests.SampleModuleSource.Replace("\"SlackWebhook\"", $"\"{typeKey}\"").Replace("SlackWebhookModule", typeKey + "Module");

    private async Task<Guid> PostId(string path, object body)
    {
        var response = await _client.PostAsJsonAsync(path, body, TestJson.Options);
        Assert.True(response.IsSuccessStatusCode, $"{path}: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestJson.Options)).GetProperty("id").GetGuid();
    }

    /// <summary>Builds a workflow that uses every kind of thing a package has to carry, and exports it.</summary>
    private async Task<(Guid WorkflowId, WorkflowPackage Package)> BuildAndExport(string typeKey, string workflowName)
    {
        var typeInstall = await _client.PostAsJsonAsync("/api/resource-types", new { sourceCode = Module(typeKey) }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, typeInstall.StatusCode);

        var workflowId = await PostId("/api/workflows", new { name = workflowName, description = "Newsletter loop" });
        var token = await PostId("/api/resources", new { workflowId, name = "GitHub token", type = "PatToken", description = "", configJson = """{"token":"ghp_verysecret","host":"github.com"}""" });
        var script = await PostId("/api/resources", new { workflowId, name = "Fetch signups", type = "Custom", customTypeKey = "Script", description = "", configJson = """{"language":"python","code":"print('@metric signups=3')","trigger":"before"}""" });
        var metric = await PostId("/api/resources", new { workflowId, name = "Sign-ups", type = "Custom", customTypeKey = "Metric", description = "", configJson = """{"unit":"sign-ups","aggregation":"sum","direction":"higher"}""" });
        var hook = await PostId("/api/resources", new { workflowId, name = "Team channel", type = "Custom", customTypeKey = typeKey, description = "", configJson = """{"webhookUrl":"https://hooks.example/abc","channel":"#growth"}""" });
        var folder = await PostId("/api/resources", new { workflowId, name = "Campaign files", type = "FileLocation", description = "", configJson = """{"path":"/definitely/not/here/campaigns"}""" });
        await PostId("/api/agents", new
        {
            workflowId, name = "Campaign loop", description = "", prompt = "Send the newsletter.", model = "claude-opus-5", effort = "High",
            intervalMinutes = 120, triggerMode = "Scheduled", triggerTopics = (string?)null, maxTurns = 20, maxBudgetUsd = 2.5m,
            workingDirectory = "/definitely/not/here/work", allowedTools = (string?)null, bypassPermissions = true, dryRun = true, autonomyLevel = 2,
            resourceIds = new[] { token, script, metric, hook, folder }
        });
        await PostId("/api/agents", new
        {
            workflowId, name = "Follow-up", description = "", prompt = "Follow up.", model = "claude-sonnet-5", effort = "Medium",
            intervalMinutes = 60, triggerMode = "Event", triggerTopics = "agent.campaign-loop.succeeded", maxTurns = 10, maxBudgetUsd = (decimal?)null,
            workingDirectory = (string?)null, allowedTools = "Read,Grep", bypassPermissions = false, dryRun = true, autonomyLevel = 1,
            resourceIds = new[] { metric }
        });

        var export = await _client.GetAsync($"/api/workflows/{workflowId}/export?format=json");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal($"{workflowName.ToLowerInvariant().Replace(' ', '-')}.looper-workflow.json", export.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var package = await export.Content.ReadFromJsonAsync<WorkflowPackage>(TestJson.Options);
        return (workflowId, package!);
    }

    [Fact]
    public async Task Export_carries_everything_but_secrets_and_history()
    {
        var (_, package) = await BuildAndExport("PortableHook", "Growth export");

        Assert.Equal("looper-workflow", package.Format);
        Assert.Equal(1, package.Version);
        Assert.Equal("Growth export", package.Workflow.Name);
        Assert.Equal(5, package.Resources.Count);
        Assert.Equal(2, package.Agents.Count);

        // The dynamic type travels with source and compiled DLL; built-ins (Script, Metric) do not.
        var type = Assert.Single(package.ResourceTypes);
        Assert.Equal("PortableHook", type.TypeKey);
        Assert.Contains("PortableHookModule", type.SourceCode);
        Assert.False(string.IsNullOrEmpty(type.DllBase64));
        Assert.NotEmpty(Convert.FromBase64String(type.DllBase64!));

        // Secrets are stripped and listed — the PAT and the module's Password field.
        var raw = JsonSerializer.Serialize(package, WorkflowPackage.JsonOptions);
        Assert.DoesNotContain("ghp_verysecret", raw);
        Assert.DoesNotContain("hooks.example", raw);
        Assert.Contains(package.RedactedSecrets, s => s.ResourceName == "GitHub token" && s.Field == "token");
        Assert.Contains(package.RedactedSecrets, s => s.ResourceName == "Team channel" && s.Field == "webhookUrl");
        Assert.Contains("\"channel\":\"#growth\"", package.Resources.Single(r => r.Name == "Team channel").ConfigJson);

        // Wiring by ref, not by id.
        var loop = package.Agents.Single(a => a.Name == "Campaign loop");
        Assert.Equal(5, loop.ResourceRefs.Count);
        Assert.All(loop.ResourceRefs, r => Assert.Contains(package.Resources, p => p.Ref == r));
        Assert.Equal(2.5m, loop.MaxBudgetUsd);
        var follow = package.Agents.Single(a => a.Name == "Follow-up");
        Assert.Equal(TriggerMode.Event, follow.TriggerMode);
        Assert.Equal("agent.campaign-loop.succeeded", follow.TriggerTopics);
        Assert.Equal("Read,Grep", follow.AllowedTools);
    }

    [Fact]
    public async Task Import_on_the_same_instance_reuses_the_type_and_creates_a_paused_copy_with_warnings()
    {
        var (original, package) = await BuildAndExport("ReusedHook", "Growth reuse");

        var response = await _client.PostAsJsonAsync("/api/workflows/import", new { package, name = "Growth copy" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<WorkflowImportResultDto>(TestJson.Options);

        Assert.NotEqual(original, result!.Workflow.Id);
        Assert.Equal("Growth copy", result.Workflow.Name);
        Assert.Equal(5, result.ResourcesCreated);
        Assert.Equal(2, result.AgentsCreated);
        Assert.Equal(["ReusedHook"], result.ResourceTypesReused);
        Assert.Empty(result.ResourceTypesInstalled);
        Assert.Contains(result.Warnings, w => w.Contains("'GitHub token' needs its 'token'"));
        Assert.Contains(result.Warnings, w => w.Contains("/definitely/not/here/campaigns"));
        Assert.Contains(result.Warnings, w => w.Contains("working directory '/definitely/not/here/work'"));
        Assert.Contains(result.Warnings, w => w.Contains("2 agents are paused"));

        var agents = await _client.GetFromJsonAsync<List<JsonElement>>($"/api/agents?workflowId={result.Workflow.Id}", TestJson.Options);
        Assert.Equal(2, agents!.Count);
        Assert.All(agents, a => Assert.False(a.GetProperty("enabled").GetBoolean()));
        var loopId = agents.Single(a => a.GetProperty("name").GetString() == "Campaign loop").GetProperty("id").GetGuid();
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/agents/{loopId}", TestJson.Options);
        Assert.Equal(5, detail.GetProperty("resourceIds").GetArrayLength());

        var resources = await _client.GetFromJsonAsync<List<JsonElement>>($"/api/resources?workflowId={result.Workflow.Id}", TestJson.Options);
        Assert.Equal(5, resources!.Count);
        var token = resources.Single(r => r.GetProperty("name").GetString() == "GitHub token");
        Assert.DoesNotContain("ghp_verysecret", token.GetProperty("configJson").GetString());
        Assert.DoesNotContain("__SECRET_UNCHANGED__", token.GetProperty("configJson").GetString()); // blank, not a sentinel stored as the secret
    }

    [Fact]
    public async Task Import_into_a_fresh_instance_installs_the_packaged_type()
    {
        var (_, package) = await BuildAndExport("TravellingHook", "Growth travel");

        using var other = new LooperApiFactory();
        var otherClient = other.CreateClient();
        var response = await otherClient.PostAsJsonAsync("/api/workflows/import", new { package }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<WorkflowImportResultDto>(TestJson.Options);

        Assert.Equal("Growth travel", result!.Workflow.Name);
        Assert.Equal(["TravellingHook"], result.ResourceTypesInstalled);
        var types = await otherClient.GetFromJsonAsync<List<JsonElement>>("/api/resource-types", TestJson.Options);
        Assert.Contains(types!, t => t.GetProperty("typeKey").GetString() == "TravellingHook" && !t.GetProperty("builtIn").GetBoolean());
        var resources = await otherClient.GetFromJsonAsync<List<JsonElement>>($"/api/resources?workflowId={result.Workflow.Id}", TestJson.Options);
        Assert.Contains(resources!, r => r.GetProperty("customTypeKey").GetString() == "TravellingHook");
    }

    [Fact]
    public async Task A_type_that_exists_here_with_different_code_is_refused()
    {
        var (_, package) = await BuildAndExport("ClashingHook", "Growth clash");
        var altered = package with
        {
            ResourceTypes = [package.ResourceTypes[0] with { SourceCode = package.ResourceTypes[0].SourceCode.Replace("#growth", "#other").Replace("Lets the agent", "Now lets the agent") }]
        };

        var response = await _client.PostAsJsonAsync("/api/workflows/import", new { package = altered }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("already exists here with different code", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_broken_package_is_refused_with_a_reason()
    {
        var notAPackage = await _client.PostAsJsonAsync("/api/workflows/import", new { package = new { format = "something-else", version = 1, workflow = new { name = "x" }, resources = Array.Empty<object>(), agents = Array.Empty<object>(), resourceTypes = Array.Empty<object>() } }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, notAPackage.StatusCode);
        Assert.Contains("not a Looper workflow package", await notAPackage.Content.ReadAsStringAsync());

        var dangling = new
        {
            format = "looper-workflow", version = 1, workflow = new { name = "Dangling" },
            resourceTypes = Array.Empty<object>(),
            resources = new[] { new { @ref = "r1", name = "Rules", type = "RuleSet", description = "", configJson = "{}" } },
            agents = new[] { new { @ref = "a1", name = "Loop", description = "", prompt = "Go.", model = "claude-opus-5", effort = "High", intervalMinutes = 60, triggerMode = "Scheduled", maxTurns = 10, bypassPermissions = true, dryRun = true, autonomyLevel = 2, resourceRefs = new[] { "r9" } } }
        };
        var danglingResponse = await _client.PostAsJsonAsync("/api/workflows/import", new { package = dangling }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, danglingResponse.StatusCode);
        Assert.Contains("resource ref that is not in the package", await danglingResponse.Content.ReadAsStringAsync());

        var unknownType = new
        {
            format = "looper-workflow", version = 1, workflow = new { name = "Unknown type" },
            resourceTypes = Array.Empty<object>(),
            resources = new[] { new { @ref = "r1", name = "Thing", type = "Custom", customTypeKey = "NotInstalledAnywhere", description = "", configJson = "{}" } },
            agents = Array.Empty<object>()
        };
        var unknownResponse = await _client.PostAsJsonAsync("/api/workflows/import", new { package = unknownType }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, unknownResponse.StatusCode);
        Assert.Contains("neither in the package nor installed here", await unknownResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_failure_half_way_leaves_nothing_behind_not_even_the_installed_type()
    {
        var package = new
        {
            format = "looper-workflow", version = 1, workflow = new { name = "Half-way" },
            resourceTypes = new[] { new { typeKey = "RollbackHook", displayName = "Rollback", icon = "↩", blurb = "", sourceCode = Module("RollbackHook"), dllBase64 = (string?)null, generationPrompt = (string?)null } },
            resources = new[] { new { @ref = "r1", name = "Hook", type = "Custom", customTypeKey = "RollbackHook", description = "", configJson = """{"channel":"#x"}""" } },
            // intervalMinutes 0 fails agent validation after the workflow and resource were created.
            agents = new[] { new { @ref = "a1", name = "Bad loop", description = "", prompt = "Go.", model = "claude-opus-5", effort = "High", intervalMinutes = 0, triggerMode = "Scheduled", maxTurns = 10, bypassPermissions = true, dryRun = true, autonomyLevel = 2, resourceRefs = new[] { "r1" } } }
        };
        var before = (await _client.GetFromJsonAsync<List<JsonElement>>("/api/workflows", TestJson.Options))!.Count;

        var response = await _client.PostAsJsonAsync("/api/workflows/import", new { package }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var workflows = await _client.GetFromJsonAsync<List<JsonElement>>("/api/workflows", TestJson.Options);
        Assert.Equal(before, workflows!.Count);
        Assert.DoesNotContain(workflows, w => w.GetProperty("name").GetString() == "Half-way");
        var types = await _client.GetFromJsonAsync<List<JsonElement>>("/api/resource-types", TestJson.Options);
        Assert.DoesNotContain(types!, t => t.GetProperty("typeKey").GetString() == "RollbackHook");
    }

    [Fact]
    public async Task When_the_source_does_not_compile_here_the_packaged_dll_is_used()
    {
        var compiled = new ResourceModuleCompiler().Compile(Module("PrebuiltHook"), "Looper.Module.PrebuiltHookTest");
        Assert.True(compiled.Success, string.Join("\n", compiled.Errors));
        var package = new
        {
            format = "looper-workflow", version = 1, workflow = new { name = "Prebuilt" },
            resourceTypes = new[] { new { typeKey = "PrebuiltHook", displayName = "Prebuilt", icon = "📦", blurb = "", sourceCode = "public class Broken {", dllBase64 = Convert.ToBase64String(compiled.Assembly!), generationPrompt = (string?)null } },
            resources = new[] { new { @ref = "r1", name = "Hook", type = "Custom", customTypeKey = "PrebuiltHook", description = "", configJson = """{"channel":"#x"}""" } },
            agents = Array.Empty<object>()
        };

        var response = await _client.PostAsJsonAsync("/api/workflows/import", new { package }, TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<WorkflowImportResultDto>(TestJson.Options);
        Assert.Equal(["PrebuiltHook"], result!.ResourceTypesInstalled);
        Assert.Contains(result.Warnings, w => w.Contains("installed from the packaged DLL"));
        var types = await _client.GetFromJsonAsync<List<JsonElement>>("/api/resource-types", TestJson.Options);
        Assert.Contains(types!, t => t.GetProperty("typeKey").GetString() == "PrebuiltHook");
    }
}

// ---------- the .workflow container ----------

public sealed class WorkflowContainerTests
{
    private static WorkflowPackage Sample(byte[]? dll = null) => new(
        WorkflowPackage.FormatName, WorkflowPackage.CurrentVersion, new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc), "Looper 1.0.0",
        new PackagedWorkflow("Growth", "Newsletter loop"),
        [new PackagedResourceType("SlackHook", "Slack Hook", "💬", "Posts.", "public class SlackHookModule {}", dll is null ? null : Convert.ToBase64String(dll), null)],
        [new PackagedResource("r1", "Channel", ResourceType.Custom, "SlackHook", "", """{"channel":"#growth"}""")],
        [new PackagedAgent("a1", "Loop", "", "Go.", "claude-opus-5", EffortLevel.High, 60, TriggerMode.Scheduled, null, 10, null, null, null, true, true, 2, ["r1"])],
        [new RedactedSecret("r1", "Channel", "webhookUrl")]);

    [Fact]
    public void Pack_and_unpack_round_trip_including_the_dll_bytes()
    {
        var dll = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        var bytes = WorkflowContainer.Pack(Sample(dll));

        // It is a real zip with the documented layout, so any archive tool can open it.
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)))
        {
            Assert.Equal(["manifest.json", "workflow.json", "types/SlackHook/module.cs", "types/SlackHook/module.dll"], zip.Entries.Select(e => e.FullName));
        }

        var unpacked = WorkflowContainer.Unpack(bytes);

        Assert.Equal("Growth", unpacked.Workflow.Name);
        Assert.Equal("Looper 1.0.0", unpacked.ExportedFrom);
        var type = Assert.Single(unpacked.ResourceTypes);
        Assert.Equal("public class SlackHookModule {}", type.SourceCode);
        Assert.Equal(dll, Convert.FromBase64String(type.DllBase64!));
        Assert.Equal("#growth", JsonDocument.Parse(unpacked.Resources.Single().ConfigJson).RootElement.GetProperty("channel").GetString());
        Assert.Equal(["r1"], unpacked.Agents.Single().ResourceRefs);
        Assert.Single(unpacked.RedactedSecrets);
        Assert.Equal("growth.workflow", WorkflowContainer.FileNameFor("Growth"));
    }

    [Fact]
    public void Packing_is_deterministic_for_identical_content()
    {
        Assert.Equal(WorkflowContainer.Pack(Sample()), WorkflowContainer.Pack(Sample()));
    }

    [Fact]
    public void A_modified_entry_is_refused_by_its_checksum()
    {
        var original = WorkflowContainer.Pack(Sample());
        var tampered = new MemoryStream();
        using (var source = new System.IO.Compression.ZipArchive(new MemoryStream(original)))
        using (var target = new System.IO.Compression.ZipArchive(tampered, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                if (entry.FullName == "workflow.json") text = text.Replace("Go.", "Delete everything.");
                using var writer = new StreamWriter(target.CreateEntry(entry.FullName).Open());
                writer.Write(text);
            }
        }

        var ex = Assert.Throws<WorkflowContainerException>(() => WorkflowContainer.Unpack(tampered.ToArray()));
        Assert.Contains("does not match its checksum", ex.Message);
    }

    [Fact]
    public void Things_that_are_not_packages_are_refused_with_a_reason()
    {
        Assert.Contains("not a valid container", Assert.Throws<WorkflowContainerException>(() => WorkflowContainer.Unpack("{\"format\":\"looper-workflow\"}"u8.ToArray())).Message);

        var noManifest = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(noManifest, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("readme.txt").Open())) writer.Write("hello");
        Assert.Contains("no manifest", Assert.Throws<WorkflowContainerException>(() => WorkflowContainer.Unpack(noManifest.ToArray())).Message);

        var extra = new MemoryStream();
        using (var source = new System.IO.Compression.ZipArchive(new MemoryStream(WorkflowContainer.Pack(Sample()))))
        using (var target = new System.IO.Compression.ZipArchive(extra, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                using var input = entry.Open();
                using var output = target.CreateEntry(entry.FullName).Open();
                input.CopyTo(output);
            }
            using var writer = new StreamWriter(target.CreateEntry("types/SlackHook/extra.dll").Open());
            writer.Write("sneaky");
        }
        Assert.Contains("manifest does not list", Assert.Throws<WorkflowContainerException>(() => WorkflowContainer.Unpack(extra.ToArray())).Message);
    }
}

public class WorkflowFileApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private static MultipartFormDataContent Upload(byte[] bytes, string? name = null)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(WorkflowContainer.MediaType);
        form.Add(file, "file", "growth.workflow");
        if (name is not null) form.Add(new StringContent(name), "name");
        return form;
    }

    [Fact]
    public async Task Export_produces_a_workflow_file_that_inspects_and_imports_as_a_new_workflow()
    {
        var created = await _client.PostAsJsonAsync("/api/workflows", new { name = "File round trip", description = "" }, TestJson.Options);
        var workflowId = (await created.Content.ReadFromJsonAsync<JsonElement>(TestJson.Options)).GetProperty("id").GetGuid();
        await _client.PostAsJsonAsync("/api/resources", new { workflowId, name = "Token", type = "PatToken", description = "", configJson = """{"token":"ghp_secret"}""" }, TestJson.Options);

        var export = await _client.GetAsync($"/api/workflows/{workflowId}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(WorkflowContainer.MediaType, export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("file-round-trip.workflow", export.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await export.Content.ReadAsByteArrayAsync();
        Assert.Equal((byte)'P', bytes[0]); // a zip
        Assert.DoesNotContain("ghp_secret", System.Text.Encoding.UTF8.GetString(bytes));

        var asJson = await _client.GetAsync($"/api/workflows/{workflowId}/export?format=json");
        Assert.Equal("application/json", asJson.Content.Headers.ContentType?.MediaType);

        var inspect = await _client.PostAsync("/api/workflows/import/inspect", Upload(bytes));
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
        var summary = await inspect.Content.ReadFromJsonAsync<WorkflowPackageSummaryDto>(TestJson.Options);
        Assert.Equal("File round trip", summary!.Name);
        Assert.Equal(1, summary.Resources);
        Assert.Contains(summary.RedactedSecrets, s => s.Field == "token");
        Assert.Equal(1, (await _client.GetFromJsonAsync<List<JsonElement>>("/api/workflows", TestJson.Options))!.Count(w => w.GetProperty("name").GetString() == "File round trip")); // inspect created nothing

        var import = await _client.PostAsync("/api/workflows/import/file", Upload(bytes, "From file"));
        Assert.Equal(HttpStatusCode.Created, import.StatusCode);
        var result = await import.Content.ReadFromJsonAsync<WorkflowImportResultDto>(TestJson.Options);
        Assert.Equal("From file", result!.Workflow.Name);
        Assert.Equal(1, result.ResourcesCreated);

        var garbage = await _client.PostAsync("/api/workflows/import/file", Upload("not a package"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);
        Assert.Contains("not a valid container", await garbage.Content.ReadAsStringAsync());
    }
}
