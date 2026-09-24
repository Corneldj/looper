using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentValidation;
using Looper.Api.Domain;
using Looper.Api.Features.Architecture;
using Looper.Api.Features.Boards;
using Looper.Api.Features.Resources;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

// ============================================================================
// The one-off prompt's box and the Jira issue chooser live on the resources' workbench cards.
// Each card writes only the keys it owns, the map serves what the cards show, and the edit form
// never writes those keys back from a stale copy.
// ============================================================================

public sealed class BoardCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-cards-{Guid.NewGuid():N}");
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly ResourceModuleRegistry _registry = new(NullLogger<ResourceModuleRegistry>.Instance);

    public BoardCardTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "cards.db")}").Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
        _registry.RegisterBuiltIn(new OneOffPromptModule());
        _registry.RegisterBuiltIn(new JiraTimeTrackingModule());
        _registry.RegisterBuiltIn(new FileModule());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<Resource> Store(string typeKey, string configJson, string name = "Card")
    {
        var resource = new Resource { Name = name, Type = ResourceType.Custom, CustomTypeKey = typeKey, ConfigJson = configJson };
        await using var db = new LooperDbContext(_options);
        db.Resources.Add(resource);
        await db.SaveChangesAsync();
        return resource;
    }

    private async Task<JsonObject> Stored(Guid id)
    {
        await using var db = new LooperDbContext(_options);
        return JsonNode.Parse((await db.Resources.AsNoTracking().SingleAsync(r => r.Id == id)).ConfigJson)!.AsObject();
    }

    [Fact]
    public async Task The_prompt_box_writes_the_text_and_nothing_else()
    {
        var prompt = await Store(OneOffPromptModule.TypeKey_, """{"text":"Old note.","keep":"me"}""");
        await using var db = new LooperDbContext(_options);

        var saved = await new SetOneOffPromptHandler(db, _registry).Handle(new SetOneOffPromptCommand(prompt.Id, "Use the new pricing API."), default);

        Assert.Equal("Use the new pricing API.", OneOffPromptResources.Text(saved.ConfigJson));
        var stored = await Stored(prompt.Id);
        Assert.Equal(("Use the new pricing API.", "me"), (stored["text"]!.GetValue<string>(), stored["keep"]!.GetValue<string>()));

        var jira = await Store(JiraTimeTrackingModule.TypeKey_, "{}");
        var wrongType = await Assert.ThrowsAsync<ValidationException>(() =>
            new SetOneOffPromptHandler(db, _registry).Handle(new SetOneOffPromptCommand(jira.Id, "x"), default));
        Assert.Contains("is not a OneOffPrompt resource", wrongType.Message);

        var validator = new SetOneOffPromptValidator();
        Assert.True(validator.Validate(new SetOneOffPromptCommand(prompt.Id, new string('x', OneOffPromptModule.MaxLength))).IsValid);
        Assert.False(validator.Validate(new SetOneOffPromptCommand(prompt.Id, new string('x', OneOffPromptModule.MaxLength + 1))).IsValid);
    }

    [Fact]
    public async Task The_issue_chooser_writes_key_and_title_and_an_empty_key_clears_both()
    {
        var tracker = await Store(JiraTimeTrackingModule.TypeKey_,
            """{"baseUrl":"https://jira.test","pat":"jira-secret","booking":"After successful runs"}""");
        await using var db = new LooperDbContext(_options);
        var handler = new SetJiraIssueHandler(db, _registry);

        var saved = await handler.Handle(new SetJiraIssueCommand(tracker.Id, " cost-120 ", " Inventory costing "), default);

        var stored = await Stored(tracker.Id);
        Assert.Equal(("COST-120", "Inventory costing"), (stored["issueKey"]!.GetValue<string>(), stored["issueSummary"]!.GetValue<string>()));
        Assert.Equal(("jira-secret", "After successful runs"), (stored["pat"]!.GetValue<string>(), stored["booking"]!.GetValue<string>()));
        Assert.DoesNotContain("jira-secret", saved.ConfigJson);                     // the answer is masked like every read

        await handler.Handle(new SetJiraIssueCommand(tracker.Id, "", "left behind"), default);
        stored = await Stored(tracker.Id);
        Assert.Equal(("", ""), (stored["issueKey"]!.GetValue<string>(), stored["issueSummary"]!.GetValue<string>()));

        var validator = new SetJiraIssueValidator();
        Assert.False(validator.Validate(new SetJiraIssueCommand(tracker.Id, "not a key", null)).IsValid);
        Assert.True(validator.Validate(new SetJiraIssueCommand(tracker.Id, "proj-9", null)).IsValid);
        Assert.True(validator.Validate(new SetJiraIssueCommand(tracker.Id, null, null)).IsValid);
    }

    [Fact]
    public async Task Saving_the_edit_form_keeps_the_card_keys_it_leaves_out()
    {
        var prompt = await Store(OneOffPromptModule.TypeKey_, """{"text":"Waiting for the next run."}""");
        var tracker = await Store(JiraTimeTrackingModule.TypeKey_,
            """{"baseUrl":"https://jira.test","pat":"jira-secret","issueKey":"COST-120","issueSummary":"Inventory costing"}""");
        await using (var db = new LooperDbContext(_options))
        {
            var update = new UpdateResourceHandler(db, _registry);
            await update.Handle(new UpdateResourceCommand(prompt.Id, "Next run", "", "{}"), default);
            await update.Handle(new UpdateResourceCommand(tracker.Id, "Costing time", "",
                $$"""{"baseUrl":"https://jira.test","pat":"{{SecretMasker.Sentinel}}","booking":"Never"}"""), default);
        }

        Assert.Equal("Waiting for the next run.", (await Stored(prompt.Id))["text"]!.GetValue<string>());
        var stored = await Stored(tracker.Id);
        Assert.Equal(("COST-120", "Inventory costing"), (stored["issueKey"]!.GetValue<string>(), stored["issueSummary"]!.GetValue<string>()));
        Assert.Equal(("jira-secret", "Never"), (stored["pat"]!.GetValue<string>(), stored["booking"]!.GetValue<string>()));

        // A key the update does carry is taken as written — the API and imports can still set them.
        var explicitKeys = CardFields.KeepOmitted(new Resource { Type = ResourceType.Custom, CustomTypeKey = "OneOffPrompt", ConfigJson = """{"text":"old"}""" },
            """{"text":"new"}""");
        Assert.Equal("new", JsonNode.Parse(explicitKeys)!["text"]!.GetValue<string>());
        var file = new Resource { Type = ResourceType.Custom, CustomTypeKey = "File", ConfigJson = """{"path":"C:\\a.md","inline":true}""" };
        Assert.Equal("{}", CardFields.KeepOmitted(file, "{}"));                       // other types are untouched
    }

    [Fact]
    public void The_map_serves_what_each_card_shows_and_nothing_for_other_types()
    {
        var prompt = GetArchitectureMapHandler.Card(ResourceType.Custom, "OneOffPrompt", """{"text":"  Use the new API.  "}""");
        Assert.Equal("Use the new API.", prompt!.Text);

        var picked = GetArchitectureMapHandler.Card(ResourceType.Custom, "JiraTimeTracking",
            """{"baseUrl":"https://jira.test","pat":"jira-secret","issueKey":"cost-120","issueSummary":"Inventory costing"}""");
        Assert.Equal(("COST-120", "Inventory costing", true), (picked!.IssueKey, picked.IssueSummary, picked.Connected));
        Assert.DoesNotContain("jira-secret", System.Text.Json.JsonSerializer.Serialize(picked));

        var unconnected = GetArchitectureMapHandler.Card(ResourceType.Custom, "JiraTimeTracking", """{"baseUrl":"https://jira.test"}""");
        Assert.False(unconnected!.Connected);
        Assert.Equal("", unconnected.IssueKey);

        Assert.Null(GetArchitectureMapHandler.Card(ResourceType.Custom, "File", """{"path":"C:\\a.md"}"""));
        Assert.Null(GetArchitectureMapHandler.Card(ResourceType.Rule, null, """{"text":"not a prompt"}"""));
    }

    [Fact]
    public async Task The_card_searches_jira_with_the_resource_as_stored()
    {
        var tracker = await Store(JiraTimeTrackingModule.TypeKey_, """{"baseUrl":"https://jira.test","pat":"jira-secret"}""");
        var fake = new FakeBoards();
        fake.Issues.Add(("COST-120", "Inventory costing"));
        await using var db = new LooperDbContext(_options);
        var search = new SearchJiraIssuesHandler(db, _registry, fake.Jira());

        var found = await search.Handle(new SearchJiraIssuesQuery(tracker.Id, null, "costing"), default);

        Assert.Equal([new JiraIssueDto("COST-120", "Inventory costing")], found);
        Assert.Equal("Bearer jira-secret", fake.Requests.Single().Authorization);
        var nothing = await Assert.ThrowsAsync<ValidationException>(() => search.Handle(new SearchJiraIssuesQuery(null, null, "costing"), default));
        Assert.Contains("Give the resource", nothing.Message);
    }
}
