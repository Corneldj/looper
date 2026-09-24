using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using FluentValidation;
using Looper.Api.Domain;
using Looper.Api.Features.Boards;
using Looper.Api.Features.Resources;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Boards;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Infrastructure.Mcp;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Looper.Api.Tests;

// ============================================================================
// The board resources — Ticket Filler's Board page as Looper resources. Azure DevOps tickets
// are read fresh into every real run; a one-off prompt is taken by the next run and cleared;
// a Jira issue gets each run's time booked to Tempo around the timesheet. Tokens reach the
// request headers and nothing else.
// ============================================================================

/// <summary>An IHttpClientFactory whose clients all go through one handler — a fake, or one that refuses to be called.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler ?? new NoNetwork(), disposeHandler: false);

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"This test expects no network calls: {request.Method} {request.RequestUri}");
    }
}

/// <summary>A scripted Azure DevOps and Jira/Tempo: records every request, answers from the test's board and timesheet.</summary>
internal sealed class FakeBoards : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Url, string? Authorization, string Body)> Requests { get; } = [];

    /// <summary>Every work item that exists, by id.</summary>
    public Dictionary<int, JsonObject> WorkItems { get; } = [];

    /// <summary>What the board query answers, in board order.</summary>
    public List<int> Board { get; } = [];

    /// <summary>Worklogs already in the timesheet: Tempo's "started" text and a length.</summary>
    public List<(string Started, int Seconds)> Timesheet { get; } = [];

    /// <summary>The bodies of the worklogs Looper booked.</summary>
    public List<JsonObject> Booked { get; } = [];

    public List<(string Key, string Summary)> Issues { get; } = [];

    /// <summary>Answers instead of the fake when it returns a response — to script failures.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public static JsonObject Item(int id, string title, string description = "", string acceptance = "", string state = "Active") => new()
    {
        ["id"] = id,
        ["fields"] = new JsonObject
        {
            ["System.Title"] = title,
            ["System.WorkItemType"] = "Bug",
            ["System.State"] = state,
            ["System.Description"] = description,
            ["Microsoft.VSTS.Common.AcceptanceCriteria"] = acceptance
        }
    };

    public AzureDevOpsClient Azure() => new(new HttpClient(this, disposeHandler: false));

    public JiraTempoClient Jira() => new(new HttpClient(this, disposeHandler: false));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        if (Override?.Invoke(request) is { } scripted) return scripted;

        var url = request.RequestUri!;
        var path = url.AbsolutePath;
        if (path.EndsWith("/_apis/wit/wiql"))
        {
            return Reply(new JsonObject { ["workItems"] = new JsonArray(Board.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()) });
        }
        if (path.EndsWith("/_apis/wit/workitems"))
        {
            var ids = HttpUtility.ParseQueryString(url.Query)["ids"]!.Split(',').Select(int.Parse);
            return Reply(new JsonObject { ["value"] = new JsonArray(ids.Select(id => WorkItems.TryGetValue(id, out var item) ? item.DeepClone() : null).ToArray()) });
        }
        if (path.EndsWith("/rest/api/2/myself")) return Reply(new JsonObject { ["key"] = "jdoe", ["name"] = "John" });
        if (path.EndsWith("/rest/api/2/issue/picker"))
        {
            var issues = Issues.Select(i => (JsonNode)new JsonObject { ["key"] = i.Key, ["summaryText"] = i.Summary }).ToArray();
            return Reply(new JsonObject { ["sections"] = new JsonArray(new JsonObject { ["issues"] = new JsonArray(issues) }) });
        }
        if (path.EndsWith("/worklogs/search"))
        {
            return Reply(new JsonArray(Timesheet.Select(t => (JsonNode)new JsonObject { ["started"] = t.Started, ["timeSpentSeconds"] = t.Seconds }).ToArray()));
        }
        if (path.EndsWith("/rest/tempo-timesheets/4/worklogs/") && request.Method == HttpMethod.Post)
        {
            Booked.Add(JsonNode.Parse(body)!.AsObject());
            return Reply(new JsonArray());
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Reply(JsonNode node) => Json(HttpStatusCode.OK, node.ToJsonString());
}

public sealed class AzureDevOpsClientTests
{
    private static readonly AzureDevOpsConnection Contoso = new("contoso", "Fab Rikam", "pat-secret");

    [Fact]
    public async Task The_board_query_applies_the_filters_and_lists_in_board_order_without_the_long_text()
    {
        var fake = new FakeBoards();
        fake.Board.AddRange([12, 7]);
        fake.WorkItems[7] = FakeBoards.Item(7, "Older");
        fake.WorkItems[12] = FakeBoards.Item(12, "Newer");

        var tickets = await fake.Azure().QueryAsync(Contoso,
            new TicketFilter(TicketAssignee.Me, null, "costing", ["Bug", "User Story"], ["Closed", "Done"]), default);

        Assert.Equal([12, 7], tickets.Select(t => t.Id));
        var (method, url, auth, body) = fake.Requests[0];
        Assert.Equal(HttpMethod.Post, method);
        Assert.StartsWith("https://dev.azure.com/contoso/Fab%20Rikam/_apis/wit/wiql?api-version=7.1", url.AbsoluteUri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":pat-secret")), auth);
        var wiql = JsonNode.Parse(body)!["query"]!.GetValue<string>();
        Assert.Contains("[System.AssignedTo] = @me", wiql);
        Assert.Contains("([System.WorkItemType] = 'Bug' OR [System.WorkItemType] = 'User Story')", wiql);
        Assert.Contains("([System.State] <> 'Closed' AND [System.State] <> 'Done')", wiql);
        Assert.Contains("[System.Tags] CONTAINS 'costing'", wiql);
        Assert.EndsWith("ORDER BY [System.ChangedDate] DESC", wiql);

        var read = fake.Requests[1].Url.ToString();
        Assert.Contains("_apis/wit/workitems?ids=12,7&errorPolicy=Omit", read);
        Assert.Contains("fields=System.Id,System.Title", read);               // a listing never pulls descriptions
        Assert.Equal("https://dev.azure.com/contoso/Fab%20Rikam/_workitems/edit/12", tickets[0].Url);
    }

    [Fact]
    public async Task A_team_filters_by_its_area_paths_and_falls_back_to_its_iterations()
    {
        var fake = new FakeBoards
        {
            Override = request => request.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/Platform/_apis/work/teamsettings/teamfieldvalues") => FakeBoards.Json(HttpStatusCode.OK,
                    """{"values":[{"value":"Fab Rikam\\Platform","includeChildren":true},{"value":"Fab Rikam","includeChildren":false}]}"""),
                var p when p.EndsWith("/Root/_apis/work/teamsettings/teamfieldvalues") => FakeBoards.Json(HttpStatusCode.OK,
                    """{"values":[{"value":"Fab Rikam","includeChildren":true}]}"""),
                var p when p.EndsWith("/Root/_apis/work/teamsettings/iterations") => FakeBoards.Json(HttpStatusCode.OK,
                    """{"value":[{"path":"Fab Rikam\\Sprints\\S1"},{"path":"Fab Rikam\\Sprints\\S2"}]}"""),
                _ => null
            }
        };
        var client = fake.Azure();

        await client.QueryAsync(Contoso, new TicketFilter(TicketAssignee.Anyone, "Platform", null, [], []), default);
        var byArea = JsonNode.Parse(fake.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/wiql")).Body)!["query"]!.GetValue<string>();
        Assert.Contains(@"([System.AreaPath] UNDER 'Fab Rikam\Platform')", byArea);   // the project root itself is not a team filter
        Assert.DoesNotContain("@me", byArea);

        fake.Requests.Clear();
        await client.QueryAsync(Contoso, new TicketFilter(TicketAssignee.Anyone, "Root", null, [], []), default);
        var byIteration = JsonNode.Parse(fake.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/wiql")).Body)!["query"]!.GetValue<string>();
        Assert.Contains(@"([System.IterationPath] UNDER 'Fab Rikam\Sprints')", byIteration);
    }

    [Fact]
    public async Task Work_items_read_as_plain_text_and_a_missing_one_comes_back_as_null()
    {
        var fake = new FakeBoards();
        var item = FakeBoards.Item(1234, "Fix the costing",
            "<div>Costs are <b>wrong</b>.</div><ul><li>one</li><li>two</li></ul>", "<p>Totals match &amp; round&nbsp;up.</p>");
        item["fields"]!["System.AssignedTo"] = new JsonObject { ["displayName"] = "Ann Dev" };
        item["fields"]!["System.Tags"] = "costing; urgent";
        item["fields"]!["Microsoft.VSTS.Common.Priority"] = 2;
        item["fields"]!["System.ChangedDate"] = "2026-09-20T10:15:30.123Z";
        fake.WorkItems[1234] = item;
        var legacy = FakeBoards.Item(55, "Old identity format");
        legacy["fields"]!["System.AssignedTo"] = "Bob <bob@example.com>";
        fake.WorkItems[55] = legacy;

        var read = await fake.Azure().GetAsync(Contoso, [1234, 999, 55], default);

        var ticket = read[0]!;
        Assert.Null(read[1]);
        Assert.Equal("Bob <bob@example.com>", read[2]!.AssignedTo);
        Assert.Equal("Costs are wrong.\n- one\n- two", ticket.Description);
        Assert.Equal("Totals match & round up.", ticket.AcceptanceCriteria);
        Assert.Equal(["costing", "urgent"], ticket.Tags);
        Assert.Equal(("Ann Dev", (int?)2), (ticket.AssignedTo, ticket.Priority));
        Assert.Equal(new DateTime(2026, 9, 20, 10, 15, 30, 123, DateTimeKind.Utc), ticket.ChangedAtUtc);
        Assert.DoesNotContain("fields=", fake.Requests.Single().Url.ToString());    // a run reads every field
    }

    [Fact]
    public async Task Refusals_say_what_is_wrong_and_never_carry_the_token()
    {
        var signIn = new FakeBoards
        {
            Override = _ => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
            {
                Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html")
            }
        };
        var expired = await Assert.ThrowsAsync<AzureDevOpsException>(() => signIn.Azure().GetAsync(Contoso, [1], default));
        Assert.Contains("rejected the token", expired.Message);
        Assert.Contains("Work Items (Read)", expired.Message);
        Assert.DoesNotContain("pat-secret", expired.Message);

        var unknown = new FakeBoards
        {
            Override = _ => FakeBoards.Json(HttpStatusCode.NotFound, """{"message":"TF200016: The project Fab Rikam does not exist."}""")
        };
        var missing = await Assert.ThrowsAsync<AzureDevOpsException>(() => unknown.Azure().QueryAsync(Contoso,
            new TicketFilter(TicketAssignee.Me, null, null, [], []), default));
        Assert.Contains("TF200016: The project Fab Rikam does not exist.", missing.Message);
    }

    [Fact]
    public void A_pasted_organization_url_is_used_as_it_is()
    {
        Assert.Equal("https://dev.azure.com/contoso", new AzureDevOpsConnection("contoso", "p", "t").BaseUrl);
        Assert.Equal("https://dev.azure.com/contoso", new AzureDevOpsConnection(" https://dev.azure.com/contoso/ ", "p", "t").BaseUrl);
        Assert.Equal("https://tfs.example.com/Collection", new AzureDevOpsConnection("https://tfs.example.com/Collection", "p", "t").BaseUrl);
    }
}

public sealed class JiraTempoClientTests
{
    private static readonly JiraConnection Jira = new("https://jira.test/", "jira-secret");

    [Fact]
    public async Task Issue_search_uses_the_bearer_token_and_returns_each_issue_once_in_plain_text()
    {
        var fake = new FakeBoards
        {
            Override = _ => FakeBoards.Json(HttpStatusCode.OK,
                """{"sections":[{"issues":[{"key":"PROJ-1","summaryText":"Inventory costing"},{"key":"PROJ-2","summary":"<b>Inventory</b> listing"}]},{"issues":[{"key":"PROJ-1","summaryText":"Inventory costing"}]}]}""")
        };

        var issues = await fake.Jira().SearchIssuesAsync(Jira, "inventory co", 10, default);

        Assert.Equal([new JiraIssue("PROJ-1", "Inventory costing"), new JiraIssue("PROJ-2", "Inventory listing")], issues);
        var sent = fake.Requests.Single();
        Assert.Equal("https://jira.test/rest/api/2/issue/picker?query=inventory%20co&currentJQL=", sent.Url.AbsoluteUri);
        Assert.Equal("Bearer jira-secret", sent.Authorization);
    }

    [Fact]
    public async Task Worklogs_read_as_local_spans_and_book_with_the_activity_and_devops_reference()
    {
        var fake = new FakeBoards();
        fake.Timesheet.Add(("2026-09-24 09:00:00.000", 1800));
        fake.Timesheet.Add(("not a time", 600));
        var client = fake.Jira();

        var busy = await client.SearchWorklogsAsync(Jira, "jdoe", new DateOnly(2026, 9, 24), new DateOnly(2026, 9, 24), default);
        Assert.Equal(new[] { (new DateTime(2026, 9, 24, 9, 0, 0), new DateTime(2026, 9, 24, 9, 30, 0)) }, busy);
        var search = JsonNode.Parse(fake.Requests.Single().Body)!;
        Assert.Equal(("2026-09-24", "2026-09-24", "jdoe"),
            (search["from"]!.GetValue<string>(), search["to"]!.GetValue<string>(), search["worker"]![0]!.GetValue<string>()));

        await client.CreateWorklogAsync(Jira, "jdoe",
            new TempoWorklog("PROJ-7", "Developing", "1234", "Development work on 1234", new DateTime(2026, 9, 24, 10, 5, 0), 900), default);
        await client.CreateWorklogAsync(Jira, "jdoe",
            new TempoWorklog("PROJ-7", "Test", null, "Testing and review of loop", new DateTime(2026, 9, 24, 11, 0, 0), 300), default);

        var first = fake.Booked[0];
        Assert.Equal("PROJ-7", first["originTaskId"]!.GetValue<string>());
        Assert.Equal("2026-09-24 10:05:00.000", first["started"]!.GetValue<string>());
        Assert.Equal((900, 900), (first["timeSpentSeconds"]!.GetValue<int>(), first["billableSeconds"]!.GetValue<int>()));
        Assert.Equal(("jdoe", "Development work on 1234"), (first["worker"]!.GetValue<string>(), first["comment"]!.GetValue<string>()));
        Assert.Equal("Developing", first["attributes"]!["_Activity_"]!["value"]!.GetValue<string>());
        Assert.Equal(1, first["attributes"]!["_Activity_"]!["workAttributeId"]!.GetValue<int>());
        Assert.Equal("1234", first["attributes"]!["_MarelDevOpsReference_"]!["value"]!.GetValue<string>());
        Assert.Null(fake.Booked[1]["attributes"]!["_MarelDevOpsReference_"]);        // no ticket, no reference
    }

    [Fact]
    public async Task Jira_refusals_are_readable_and_a_bare_host_is_not_an_address()
    {
        var refusing = new FakeBoards { Override = _ => FakeBoards.Json(HttpStatusCode.BadRequest, """{"errorMessages":["Issue does not exist"],"errors":{}}""") };
        var refused = await Assert.ThrowsAsync<JiraException>(() => refusing.Jira().GetWorkerKeyAsync(Jira, default));
        Assert.Contains("Issue does not exist", refused.Message);

        var locked = new FakeBoards { Override = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        var rejected = await Assert.ThrowsAsync<JiraException>(() => locked.Jira().GetWorkerKeyAsync(Jira, default));
        Assert.Contains("rejected the token", rejected.Message);
        Assert.DoesNotContain("jira-secret", rejected.Message);

        var bare = await Assert.ThrowsAsync<JiraException>(() =>
            new FakeBoards().Jira().GetWorkerKeyAsync(new JiraConnection("jira.test", "t"), default));
        Assert.Contains("is not a Jira address", bare.Message);

        Assert.Equal("Worklog overlaps name: taken",
            JiraTempoClient.Describe("""{"errors":{"name":"taken"},"errorMessages":["Worklog overlaps"]}"""));
        Assert.Equal("Invalid activity", JiraTempoClient.Describe("""{"errors":[{"message":"Invalid activity"}]}"""));
    }
}

public sealed class TempoTimeTests
{
    private static DateTime At(int hour, int minute, int second = 0) => new(2026, 9, 24, hour, minute, second);

    [Fact]
    public void A_run_becomes_one_block_on_the_five_minute_grid_of_at_least_five_minutes()
    {
        Assert.Equal((At(10, 5), At(10, 35)), TempoTime.Block(At(10, 2, 40), At(10, 34, 10)));
        Assert.Equal((At(10, 0), At(10, 5)), TempoTime.Block(At(10, 1), At(10, 2)));      // a one-minute run still books five
        Assert.Equal((At(10, 5), At(10, 20)), TempoTime.Block(At(10, 2, 30), At(10, 15)));  // halves round up
    }

    [Fact]
    public void Existing_worklogs_split_shorten_or_swallow_the_block()
    {
        var hour = (At(10, 0), At(11, 0));

        Assert.Equal([(At(10, 0), At(10, 20)), (At(10, 30), At(11, 0))], TempoTime.AroundBusy(hour, [(At(10, 20), At(10, 30))]));
        Assert.Equal([(At(10, 3), At(11, 0))], TempoTime.AroundBusy(hour, [(At(9, 50), At(10, 3))]));
        Assert.Empty(TempoTime.AroundBusy(hour, [(At(9, 0), At(12, 0))]));
        Assert.Empty(TempoTime.AroundBusy(hour, [(At(10, 0), At(10, 57))]));                // three minutes left: not worth a worklog
        Assert.Equal([hour], TempoTime.AroundBusy(hour, [(At(11, 0), At(12, 0)), (At(9, 0), At(10, 0))]));
    }

    [Fact]
    public void Descriptions_stay_plain_and_short()
    {
        Assert.Equal("Development work on 1234 1235", TempoTime.Comment("Development work on #1234  #1235!"));
        Assert.Equal(250, TempoTime.Comment(new string('a', 400)).Length);
        Assert.Equal("Development work on", TempoTime.Lead("Developing"));
        Assert.Equal("Work on", TempoTime.Lead("Something else"));
    }
}

public sealed class BoardModuleTests
{
    private static ResourceModuleContext Saving(string json) => new(json, null, null, Guid.NewGuid(), "Board", "");

    private static ResourceModuleContext Running(string json) => new(json, Guid.NewGuid(), "Agent", Guid.NewGuid(), "Board", "");

    [Fact]
    public void The_board_types_mask_their_tokens_and_the_one_off_prompt_has_nothing_to_hide()
    {
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new AzureDevOpsTicketsModule());
        registry.RegisterBuiltIn(new JiraTimeTrackingModule());
        registry.RegisterBuiltIn(new OneOffPromptModule());

        var tickets = new Resource
        {
            Type = ResourceType.Custom, CustomTypeKey = "AzureDevOpsTickets",
            ConfigJson = """{"organization":"contoso","pat":"ado-secret","ticketIds":"12"}"""
        };
        var jira = new Resource { Type = ResourceType.Custom, CustomTypeKey = "JiraTimeTracking", ConfigJson = """{"pat":"jira-secret","issueKey":"PROJ-1"}""" };
        var note = new Resource { Type = ResourceType.Custom, CustomTypeKey = "OneOffPrompt", ConfigJson = """{"text":"Use the new API"}""" };

        Assert.DoesNotContain("ado-secret", SecretMasker.Mask(tickets, registry));
        Assert.Contains("\"ticketIds\":\"12\"", SecretMasker.Mask(tickets, registry));
        Assert.DoesNotContain("jira-secret", SecretMasker.Mask(jira, registry));
        Assert.Equal(note.ConfigJson, SecretMasker.Mask(note, registry));
        var exported = SecretMasker.Redact(tickets, registry);                     // a workflow export leaves the token behind
        Assert.Equal(["pat"], exported.RedactedKeys);
        Assert.DoesNotContain("ado-secret", exported.Json);
    }

    [Fact]
    public void Ticket_ids_parse_leniently_a_typo_fails_the_save_and_a_run_needs_a_selection()
    {
        var (ids, invalid) = AzureDevOpsTicketsResources.ParseIds("12345, #12346 12347;12345");
        Assert.Equal([12345, 12346, 12347], ids);
        Assert.Empty(invalid);

        var module = new AzureDevOpsTicketsModule();
        var typo = Assert.Throws<InvalidOperationException>(() => module.PrepareRun(Saving("""{"ticketIds":"12345, 12x"}""")));
        Assert.Contains("'12x' is not a work item id", typo.Message);

        module.PrepareRun(Saving("""{"organization":"contoso"}"""));                 // saving without a selection is fine
        var none = Assert.Throws<InvalidOperationException>(() =>
            module.PrepareRun(Running("""{"organization":"contoso","project":"p","pat":"t"}""")));
        Assert.Contains("No tickets are selected", none.Message);
        var tokenless = Assert.Throws<InvalidOperationException>(() => module.PrepareRun(Running("""{"ticketIds":"1"}""")));
        Assert.Contains("personal access token", tokenless.Message);
        module.PrepareRun(Running("""{"organization":"contoso","project":"p","pat":"t","ticketIds":"1"}"""));

        var config = AzureDevOpsTicketsResources.Parse(Saving(
            """{"assignee":"Assigned to me or unassigned","workItemTypes":"Bug, User Story","team":"  ","clearAfterSuccess":true}"""));
        Assert.Equal(TicketAssignee.MeOrUnassigned, config.Assignee);
        Assert.Equal(["Bug", "User Story"], config.WorkItemTypes);
        Assert.Equal(AzureDevOpsTicketsModule.DefaultExcludedStates, config.ExcludedStates);
        Assert.Null(config.Team);
        Assert.True(config.ClearAfterSuccess);
    }

    [Fact]
    public void The_jira_resource_checks_its_address_and_key_and_reads_its_booking_choice()
    {
        var module = new JiraTimeTrackingModule();
        Assert.Contains("is not a Jira address", Assert.Throws<InvalidOperationException>(() =>
            module.PrepareRun(Saving("""{"baseUrl":"jira.test"}"""))).Message);
        Assert.Contains("is not a Jira issue key", Assert.Throws<InvalidOperationException>(() =>
            module.PrepareRun(Saving("""{"issueKey":"not a key"}"""))).Message);
        module.PrepareRun(Saving("""{"baseUrl":"https://jira.test","issueKey":"proj-12"}"""));
        Assert.Contains("No Jira issue is selected", Assert.Throws<InvalidOperationException>(() =>
            module.PrepareRun(Running("""{"baseUrl":"https://jira.test","pat":"t"}"""))).Message);
        module.PrepareRun(Running("""{"booking":"Never"}"""));                       // never books, so nothing to require

        var config = JiraTimeTrackingResources.Parse(Saving(
            """{"baseUrl":"https://jira.test/","issueKey":" proj-12 ","issueSummary":"Costing","booking":"After successful runs"}"""));
        Assert.Equal(("https://jira.test", "PROJ-12", TimeBooking.SuccessfulRuns, "Developing"),
            (config.BaseUrl, config.IssueKey, config.Booking, config.Activity));
        Assert.Equal("PROJ-12 — Costing", config.IssueLabel);
        Assert.Equal(TimeBooking.EveryRun, JiraTimeTrackingResources.Parse(Saving("{}")).Booking);
        Assert.Equal(TimeBooking.Never, JiraTimeTrackingResources.Parse(Saving("""{"booking":"Never"}""")).Booking);
    }

    [Fact]
    public void The_ticket_section_carries_each_ticket_and_stays_inside_its_budget()
    {
        WorkItem Ticket(int id, string title, string description, string acceptance = "") =>
            new(id, $"https://dev.azure.com/contoso/p/_workitems/edit/{id}", title, "Bug", "Active", "Ann", ["costing"], "", "", 2,
                description, acceptance, "", null);

        var one = AzureDevOpsTicketsResources.PromptSection("Sprint board", [Ticket(1234, "Fix the costing", "Costs are wrong.", "Totals match.")]);
        Assert.StartsWith("WORK ITEM — this run is about Azure DevOps work item #1234 (selected in \"Sprint board\").", one);
        Assert.Contains("Reference it as #1234 in commit messages", one);
        Assert.Contains("## Work item #1234: Fix the costing", one);
        Assert.Contains("- Type: Bug · State: Active · Priority: 2", one);
        Assert.Contains("### Description\nCosts are wrong.", one.ReplaceLineEndings("\n"));
        Assert.Contains("### Acceptance criteria\nTotals match.", one.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("Repro steps", one);                                  // absent text is not padded in
        Assert.DoesNotContain("cut short", one);

        var huge = new string('x', 50_000);
        var many = AzureDevOpsTicketsResources.PromptSection("Sprint board",
            [Ticket(1, "First", huge), Ticket(2, "Second", huge, huge), Ticket(3, "Third", "")]);
        Assert.StartsWith("WORK ITEMS — this run is about the following 3 Azure DevOps work items", many);
        Assert.Contains("Reference them as #1 #2 #3", many);
        Assert.Contains("[… cut short — get_work_item(2) has the rest]", many);
        Assert.Contains("the Looper tool get_work_item reads any work item in full", many);
        Assert.Contains("### Description\n(none given)", many.ReplaceLineEndings("\n"));
        Assert.True(many.Length < AzureDevOpsTicketsResources.TextBudget + 2_000, $"section is {many.Length:N0} characters");
    }

    [Fact]
    public void The_one_off_section_leads_with_its_authority_and_names_each_source_when_there_are_several()
    {
        var one = OneOffPromptResources.PromptSection([("Note", "Use the new API.")]);
        Assert.StartsWith("INSTRUCTIONS FOR THIS RUN", one);
        Assert.EndsWith("\n\nUse the new API.", one);

        var two = OneOffPromptResources.PromptSection([("Note", "A."), ("Review", "B.")]);
        Assert.Contains("From \"Note\":\nA.", two);
        Assert.Contains("From \"Review\":\nB.", two);
    }

    [Fact]
    public void The_run_prompt_puts_the_work_right_after_its_trigger()
    {
        var agent = new LoopAgent { Name = "Loop", Prompt = "Implement the tickets." };
        var prompt = ClaudeCliExecutor.BuildPrompt(agent, [], [], triggerEvents: "- ticket.assigned",
            memoryContext: ["MEMORY: what we know"], scriptOutputs: "SCRIPT OUTPUT: data",
            briefing: ["WORK ITEM — #1234", "INSTRUCTIONS FOR THIS RUN — use the new API"]);

        var order = new[] { "Implement the tickets.", "TRIGGERING EVENT", "WORK ITEM — #1234", "INSTRUCTIONS FOR THIS RUN", "MEMORY:", "SCRIPT OUTPUT:" }
            .Select(marker => prompt.IndexOf(marker, StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
    }
}

/// <summary>The harness around a run, against a scripted Azure DevOps and Tempo and a real database.</summary>
public sealed class BoardHarnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-board-{Guid.NewGuid():N}");
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly FakeBoards _fake = new();
    private readonly List<(string Level, string Message)> _log = [];

    public BoardHarnessTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "board.db")}").Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
        _fake.WorkItems[1234] = FakeBoards.Item(1234, "Fix the costing", "<p>Costs are wrong.</p>", "<p>Totals match.</p>");
        _fake.WorkItems[1235] = FakeBoards.Item(1235, "Round the totals", "<p>Round half up.</p>");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Factory(DbContextOptions<LooperDbContext> options) : IDbContextFactory<LooperDbContext>
    {
        public LooperDbContext CreateDbContext() => new(options);
    }

    private BoardHarness Harness() =>
        new(new Factory(_options), new StubHttpClientFactory(_fake), NullLogger<BoardHarness>.Instance) { TimeZone = TimeZoneInfo.Utc };

    private Task Log(string level, string message)
    {
        _log.Add((level, message));
        return Task.CompletedTask;
    }

    internal static Resource Tickets(string ids = "1234, 1235", bool clearAfterSuccess = false) => new()
    {
        Name = "Sprint board", Type = ResourceType.Custom, CustomTypeKey = AzureDevOpsTicketsModule.TypeKey_,
        ConfigJson = new JsonObject
        {
            ["organization"] = "contoso", ["project"] = "Fabrikam", ["pat"] = "ado-secret", ["ticketIds"] = ids,
            ["clearAfterSuccess"] = clearAfterSuccess
        }.ToJsonString()
    };

    internal static Resource Note(string text) => new()
    {
        Name = "Next run", Type = ResourceType.Custom, CustomTypeKey = OneOffPromptModule.TypeKey_,
        ConfigJson = new JsonObject { ["text"] = text, ["keep"] = "me" }.ToJsonString()
    };

    internal static Resource Time(string booking = "After every run") => new()
    {
        Name = "Costing time", Type = ResourceType.Custom, CustomTypeKey = JiraTimeTrackingModule.TypeKey_,
        ConfigJson = new JsonObject
        {
            ["baseUrl"] = "https://jira.test", ["pat"] = "jira-secret", ["issueKey"] = "PROJ-7", ["issueSummary"] = "Inventory costing",
            ["booking"] = booking
        }.ToJsonString()
    };

    private async Task<List<Resource>> Store(params Resource[] resources)
    {
        await using var db = new LooperDbContext(_options);
        db.Resources.AddRange(resources);
        await db.SaveChangesAsync();
        return [.. resources];
    }

    private async Task<string> StoredConfig(Guid id)
    {
        await using var db = new LooperDbContext(_options);
        return (await db.Resources.AsNoTracking().SingleAsync(r => r.Id == id)).ConfigJson;
    }

    private async Task Rewrite(Guid id, string configJson)
    {
        await using var db = new LooperDbContext(_options);
        var resource = await db.Resources.SingleAsync(r => r.Id == id);
        resource.ConfigJson = configJson;
        await db.SaveChangesAsync();
    }

    private async Task<(LoopAgent Agent, Guid RunId)> FinishedRun(RunStatus status, int turns, DateTime startedUtc, DateTime completedUtc)
    {
        await using var db = new LooperDbContext(_options);
        var agent = new LoopAgent { Name = "Costing loop", Prompt = "Work.", Model = "claude-opus-5", WorkingDirectory = _dir };
        db.Agents.Add(agent);
        var run = new AgentRun
        {
            AgentId = agent.Id, Trigger = RunTrigger.Manual, Model = agent.Model, Status = status, NumTurns = turns,
            StartedAtUtc = startedUtc, CompletedAtUtc = completedUtc
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return (agent, run.Id);
    }

    [Fact]
    public async Task Before_a_run_the_tickets_are_read_fresh_and_the_one_off_prompt_is_taken_and_cleared()
    {
        var resources = await Store(Tickets(), Note("Use the new pricing API."));

        var briefing = await Harness().PrepareAsync(resources, Log, default);

        Assert.Null(briefing.Refusal);
        Assert.Equal(2, briefing.Sections.Count);
        Assert.Contains("## Work item #1234: Fix the costing", briefing.Sections[0]);
        Assert.Contains("Costs are wrong.", briefing.Sections[0]);
        Assert.Contains("## Work item #1235: Round the totals", briefing.Sections[0]);
        Assert.StartsWith("INSTRUCTIONS FOR THIS RUN", briefing.Sections[1]);
        Assert.EndsWith("Use the new pricing API.", briefing.Sections[1]);

        var stored = JsonNode.Parse(await StoredConfig(resources[1].Id))!;
        Assert.Equal("", stored["text"]!.GetValue<string>());
        Assert.Equal("me", stored["keep"]!.GetValue<string>());                       // only the text is cleared
        Assert.Contains(_log, l => l.Message.StartsWith("Tickets from 'Sprint board': #1234 Fix the costing (Bug, Active)"));
        Assert.Contains(_log, l => l.Message == "One-off prompt 'Next run' taken for this run and cleared:\nUse the new pricing API.");
        Assert.All(_fake.Requests, r => Assert.DoesNotContain("ado-secret", r.Url.ToString()));

        // Taken once: the next run finds nothing waiting.
        var next = await Harness().PrepareAsync(resources, Log, default);
        Assert.Single(next.Sections);
        Assert.Empty(next.Taken);
    }

    [Fact]
    public async Task A_ticket_that_cannot_be_read_refuses_the_run_and_the_prompt_waits_for_the_next_one()
    {
        var resources = await Store(Tickets("1234, 999"), Note("Use the new pricing API."));

        var missing = await Harness().PrepareAsync(resources, Log, default);
        Assert.Contains("'Sprint board': #999 could not be found", missing.Refusal);
        Assert.Empty(missing.Sections);

        _fake.Override = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var locked = await Harness().PrepareAsync(resources, Log, default);
        Assert.Contains("The tickets of 'Sprint board' could not be read: Azure DevOps rejected the token", locked.Refusal);

        Assert.Equal("Use the new pricing API.", OneOffPromptResources.Text(await StoredConfig(resources[1].Id)));

        var unselected = await Store(Tickets(""));
        Assert.Contains("has no tickets selected", (await Harness().PrepareAsync(unselected, Log, default)).Refusal);
    }

    [Fact]
    public async Task A_prompt_goes_back_when_the_model_never_started_unless_it_was_rewritten_meanwhile()
    {
        var resources = await Store(Note("Use the new pricing API."));
        var harness = Harness();

        var first = await harness.PrepareAsync(resources, Log, default);
        await harness.ReturnPromptsAsync(first, Log);
        Assert.Equal("Use the new pricing API.", OneOffPromptResources.Text(await StoredConfig(resources[0].Id)));
        Assert.Contains(_log, l => l.Message == "One-off prompt 'Next run' put back: the run stopped before the model started.");

        var second = await harness.PrepareAsync(resources, Log, default);
        await Rewrite(resources[0].Id, """{"text":"Something newer."}""");
        await harness.ReturnPromptsAsync(second, Log);
        Assert.Equal("Something newer.", OneOffPromptResources.Text(await StoredConfig(resources[0].Id)));
    }

    [Fact]
    public async Task A_dry_run_reads_nothing_books_nothing_and_keeps_the_prompt()
    {
        var resources = await Store(Tickets(), Note("Use the new pricing API."), Time());

        await Harness().RehearseAsync(resources, Log);

        Assert.Empty(_fake.Requests);
        Assert.Equal("Use the new pricing API.", OneOffPromptResources.Text(await StoredConfig(resources[1].Id)));
        Assert.Contains(_log, l => l.Message == "[dry run] Tickets not read: a real run would work on #1234, #1235 from 'Sprint board'.");
        Assert.Contains(_log, l => l.Message == "[dry run] One-off prompt 'Next run' kept for the next real run.");
        Assert.Contains(_log, l => l.Message == "[dry run] No time booked to Tempo.");
    }

    [Fact]
    public async Task After_a_run_its_time_is_booked_around_what_the_timesheet_already_holds()
    {
        var resources = await Store(Tickets(), Time());
        _fake.Timesheet.Add(("2026-09-24 10:20:00.000", 300));                     // a meeting the user logged by hand
        var (agent, runId) = await FinishedRun(RunStatus.Succeeded, 12,
            new DateTime(2026, 9, 24, 10, 2, 40, DateTimeKind.Utc), new DateTime(2026, 9, 24, 10, 34, 10, DateTimeKind.Utc));

        await Harness().AfterRunAsync(agent, resources, runId, Log);

        Assert.Equal(2, _fake.Booked.Count);
        Assert.Equal(["2026-09-24 10:05:00.000", "2026-09-24 10:25:00.000"], _fake.Booked.Select(b => b["started"]!.GetValue<string>()));
        Assert.Equal([900, 600], _fake.Booked.Select(b => b["timeSpentSeconds"]!.GetValue<int>()));
        Assert.All(_fake.Booked, b =>
        {
            Assert.Equal("PROJ-7", b["originTaskId"]!.GetValue<string>());
            Assert.Equal("jdoe", b["worker"]!.GetValue<string>());
            Assert.Equal("Development work on 1234 1235", b["comment"]!.GetValue<string>());
            Assert.Equal("1234", b["attributes"]!["_MarelDevOpsReference_"]!["value"]!.GetValue<string>());
        });
        Assert.Contains(_log, l => l.Message == "Tempo: booked Developing 15m (24 Sep 10:05–10:20) to PROJ-7 — Inventory costing, DevOps reference #1234.");
        Assert.Contains(_log, l => l.Message.Contains("overlapped worklogs already in your timesheet"));
        Assert.All(_fake.Requests.Where(r => r.Url.Host == "jira.test"), r => Assert.Equal("Bearer jira-secret", r.Authorization));
    }

    [Theory]
    [InlineData("After every run", RunStatus.Failed, 0)]                 // refused before the model started
    [InlineData("After successful runs", RunStatus.Failed, 8)]
    [InlineData("Never", RunStatus.Succeeded, 8)]
    public async Task Some_runs_book_nothing(string booking, RunStatus status, int turns)
    {
        var resources = await Store(Tickets(), Time(booking));
        var (agent, runId) = await FinishedRun(status, turns, DateTime.UtcNow.AddMinutes(-30), DateTime.UtcNow);

        await Harness().AfterRunAsync(agent, resources, runId, Log);

        Assert.Empty(_fake.Booked);
        Assert.DoesNotContain(_fake.Requests, r => r.Url.Host == "jira.test");
    }

    [Fact]
    public async Task A_failed_run_is_booked_too_and_a_tempo_refusal_is_logged_not_thrown()
    {
        var resources = await Store(Tickets(), Time());
        var (agent, runId) = await FinishedRun(RunStatus.Failed, 5, DateTime.UtcNow.AddMinutes(-20), DateTime.UtcNow);

        await Harness().AfterRunAsync(agent, resources, runId, Log);
        Assert.Single(_fake.Booked);

        _fake.Override = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/worklogs/")
            ? FakeBoards.Json(HttpStatusCode.BadRequest, """{"errors":[{"message":"Worklog overlaps another"}]}""")
            : null;
        await Harness().AfterRunAsync(agent, resources, runId, Log);
        Assert.Contains(_log, l => l.Level == "warn" && l.Message.StartsWith("Tempo: booking on 'Costing time' failed") &&
                                   l.Message.Contains("Worklog overlaps another"));
    }

    [Fact]
    public async Task A_successful_run_clears_a_selection_that_asked_for_it_unless_the_user_changed_it()
    {
        var clears = await Store(Tickets("1234", clearAfterSuccess: true));
        var keeps = await Store(Tickets("1234", clearAfterSuccess: false));
        var (agent, runId) = await FinishedRun(RunStatus.Succeeded, 3, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);

        await Harness().AfterRunAsync(agent, clears.Concat(keeps).ToList(), runId, Log);
        Assert.Equal("", JsonNode.Parse(await StoredConfig(clears[0].Id))!["ticketIds"]!.GetValue<string>());
        Assert.Equal("1234", JsonNode.Parse(await StoredConfig(keeps[0].Id))!["ticketIds"]!.GetValue<string>());
        Assert.Contains(_log, l => l.Message == "Selection on 'Sprint board' cleared after the successful run (#1234).");

        // The user picked new tickets while the run worked: theirs stays.
        var changed = await Store(Tickets("1234", clearAfterSuccess: true));
        var snapshot = changed.ToList();
        await Rewrite(changed[0].Id, changed[0].ConfigJson.Replace("\"1234\"", "\"5678\""));
        await Harness().AfterRunAsync(agent, snapshot, runId, Log);
        Assert.Equal("5678", JsonNode.Parse(await StoredConfig(changed[0].Id))!["ticketIds"]!.GetValue<string>());

        var (failedAgent, failedRun) = await FinishedRun(RunStatus.Failed, 3, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow);
        var retry = await Store(Tickets("1234", clearAfterSuccess: true));
        await Harness().AfterRunAsync(failedAgent, retry, failedRun, Log);
        Assert.Equal("1234", JsonNode.Parse(await StoredConfig(retry[0].Id))!["ticketIds"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_run_gets_get_work_item_and_it_reads_a_ticket_in_full_through_the_resource()
    {
        var tools = LooperTools.ForRun([Tickets()]);
        var tool = Assert.Single(tools, t => t.Name == "get_work_item");
        Assert.Equal(["id"], tool.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("integer", tool.InputSchema["properties"]!["id"]!["type"]!.GetValue<string>());
        Assert.Contains("\"Sprint board\": #1234, #1235", tool.Description);
        Assert.DoesNotContain("ado-secret", tool.Description);
        Assert.DoesNotContain(LooperTools.ForRun([Note("x"), Time()]), t => t.Name == "get_work_item");

        await using var db = new LooperDbContext(_options);
        var agent = new LoopAgent { Name = "Loop", Prompt = "Work.", Model = "claude-opus-5", WorkingDirectory = _dir };
        agent.Resources.Add(Tickets());
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        var handler = new GetWorkItemHandler(db, _fake.Azure());

        var item = await handler.Handle(new GetWorkItemQuery(agent.Id, 1235), default);
        Assert.Equal(("Round the totals", "Round half up."), (item.Title, item.Description));
        var unknown = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new GetWorkItemQuery(agent.Id, 42), default));
        Assert.Contains("Work item #42 was not found", unknown.Message);
    }

    [Fact]
    public async Task The_editor_lists_with_unsaved_filters_and_the_stored_token_but_only_for_its_own_organization()
    {
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new AzureDevOpsTicketsModule());
        registry.RegisterBuiltIn(new JiraTimeTrackingModule());
        var stored = (await Store(Tickets("1235")))[0];
        _fake.Board.Add(1234);
        await using var db = new LooperDbContext(_options);
        var handler = new ListBoardTicketsHandler(db, registry, _fake.Azure());

        var form = new JsonObject
        {
            ["organization"] = "contoso", ["project"] = "Fabrikam", ["pat"] = SecretMasker.Sentinel, ["tag"] = "costing", ["ticketIds"] = "1235"
        };
        var tickets = await handler.Handle(new ListBoardTicketsQuery(stored.Id, form.ToJsonString()), default);

        Assert.Equal([(1235, false), (1234, true)], tickets.Select(t => (t.Id, t.Listed)));   // the selected, unlisted one first
        Assert.All(_fake.Requests, r => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":ado-secret")), r.Authorization));
        Assert.Contains("[System.Tags] CONTAINS 'costing'", JsonNode.Parse(_fake.Requests[0].Body)!["query"]!.GetValue<string>());

        form["organization"] = "somewhere-else";
        var moved = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new ListBoardTicketsQuery(stored.Id, form.ToJsonString()), default));
        Assert.Contains("paste the token again", moved.Message);

        var blank = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new ListBoardTicketsQuery(null, "{}"), default));
        Assert.Contains("Fill in the organization", blank.Message);

        _fake.Issues.Add(("PROJ-7", "Inventory costing"));
        var jira = new SearchJiraIssuesHandler(db, registry, _fake.Jira());
        var config = """{"baseUrl":"https://jira.test","pat":"jira-secret"}""";
        Assert.Empty(await jira.Handle(new SearchJiraIssuesQuery(null, config, "x"), default));   // too short to search
        Assert.Equal([new JiraIssueDto("PROJ-7", "Inventory costing")], await jira.Handle(new SearchJiraIssuesQuery(null, config, "costing"), default));
    }
}

/// <summary>The coordinator runs the harness around a real run: refuse before tokens, put an unused prompt back, book after.</summary>
public sealed class BoardRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-boardrun-{Guid.NewGuid():N}");
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly FakeBoards _fake = new();

    public BoardRunTests()
    {
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "runs.db")}").Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
        _fake.WorkItems[1234] = FakeBoards.Item(1234, "Fix the costing", "Costs are wrong");
        _fake.WorkItems[1235] = FakeBoards.Item(1235, "Round the totals", "Round half up");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Factory(DbContextOptions<LooperDbContext> options) : IDbContextFactory<LooperDbContext>
    {
        public LooperDbContext CreateDbContext() => new(options);
    }

    /// <summary>A stand-in for the CLI that answers one successful result: a batch file on Windows, a shell script elsewhere.</summary>
    private string FakeClaude()
    {
        const string result = """{"result":"done","subtype":"success","total_cost_usd":0.01,"is_error":false,"num_turns":2,"duration_ms":5}""";
        if (OperatingSystem.IsWindows())
        {
            var batch = Path.Combine(_dir, "claude.cmd");
            File.WriteAllText(batch, $"@echo off\r\necho {result}\r\n");
            return batch;
        }
        var script = Path.Combine(_dir, "claude");
        File.WriteAllText(script, $"#!/bin/sh\necho '{result}'\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private AgentRunCoordinator Coordinator(string claudeCommand)
    {
        var looperOptions = Options.Create(new LooperOptions { MaxConcurrentRuns = 2, RunTimeoutMinutes = 5, ClaudeCommand = claudeCommand });
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        registry.RegisterBuiltIn(new AzureDevOpsTicketsModule());
        registry.RegisterBuiltIn(new JiraTimeTrackingModule());
        registry.RegisterBuiltIn(new OneOffPromptModule());
        var factory = new Factory(_options);
        return new AgentRunCoordinator(
            factory,
            new ClaudeCliExecutor(looperOptions, new ClaudeAuthProvider(factory), registry,
                new GraphContextService(looperOptions, NullLogger<GraphContextService>.Instance), NullLogger<ClaudeCliExecutor>.Instance),
            new SimulatedAgentExecutor(),
            new TestingActionRunner(looperOptions),
            new ScriptRunner(looperOptions),
            new MetricRecorder(factory, NullLogger<MetricRecorder>.Instance),
            new ReviewRunner(looperOptions, new ClaudeAuthProvider(factory), NullLogger<ReviewRunner>.Instance),
            new EventDispatcher(NullLogger<EventDispatcher>.Instance),
            new BoardHarness(factory, new StubHttpClientFactory(_fake), NullLogger<BoardHarness>.Instance) { TimeZone = TimeZoneInfo.Utc },
            looperOptions,
            NullLogger<AgentRunCoordinator>.Instance);
    }

    private async Task<(AgentRun Run, List<RunLogEntry> Log, Guid NoteId)> Run(string claudeCommand, params Resource[] resources)
    {
        Guid agentId;
        await using (var db = new LooperDbContext(_options))
        {
            var agent = new LoopAgent { Name = "Costing loop", Prompt = "Implement the tickets.", Model = "claude-opus-5", DryRun = false, MaxTurns = 5, WorkingDirectory = _dir };
            agent.Resources.AddRange(resources);
            db.Agents.Add(agent);
            await db.SaveChangesAsync();
            agentId = agent.Id;
        }

        var coordinator = Coordinator(claudeCommand);
        var runId = (await coordinator.TriggerRunAsync(agentId, RunTrigger.Manual))!.Value;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (coordinator.IsRunning(agentId))                                       // until the after-run follow-up is done too
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("run did not complete");
            await Task.Delay(100);
        }

        await using var read = new LooperDbContext(_options);
        var run = await read.Runs.AsNoTracking().SingleAsync(r => r.Id == runId);
        var log = await read.RunLogs.AsNoTracking().Where(l => l.RunId == runId).OrderBy(l => l.Id).ToListAsync();
        var noteId = resources.FirstOrDefault(OneOffPromptResources.IsOneOffPrompt)?.Id ?? Guid.Empty;
        return (run, log, noteId);
    }

    private async Task<string> NoteText(Guid id)
    {
        await using var db = new LooperDbContext(_options);
        return OneOffPromptResources.Text((await db.Resources.AsNoTracking().SingleAsync(r => r.Id == id)).ConfigJson);
    }

    [Fact]
    public async Task A_run_whose_tickets_cannot_be_read_spends_nothing_and_keeps_its_prompt()
    {
        _fake.Override = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var (run, log, noteId) = await Run(Path.Combine(_dir, "no-such-claude"),
            BoardHarnessTests.Tickets(), BoardHarnessTests.Note("Use the new pricing API."), BoardHarnessTests.Time());

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("could not be read: Azure DevOps rejected the token", run.ErrorMessage);
        Assert.EndsWith("The iteration was not started.", run.ErrorMessage);
        Assert.Equal(0m, run.CostUsd);
        Assert.Equal("Use the new pricing API.", await NoteText(noteId));
        Assert.Empty(_fake.Booked);
        Assert.DoesNotContain(log, l => l.Message.StartsWith("Launching"));
    }

    [Fact]
    public async Task A_run_that_never_reaches_the_model_puts_its_prompt_back_and_books_nothing()
    {
        var (run, log, noteId) = await Run(Path.Combine(_dir, "no-such-claude"),
            BoardHarnessTests.Tickets(), BoardHarnessTests.Note("Use the new pricing API."), BoardHarnessTests.Time());

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("Could not start", run.ErrorMessage);
        Assert.Equal("Use the new pricing API.", await NoteText(noteId));
        Assert.Contains(log, l => l.Message == "One-off prompt 'Next run' put back: the run stopped before the model started.");
        Assert.Contains(log, l => l.Message == "Tempo: nothing booked — the run stopped before the model started.");
        Assert.Empty(_fake.Booked);
    }

    [Fact]
    public async Task A_real_run_reads_its_tickets_takes_its_prompt_and_books_its_time()
    {
        var (run, log, noteId) = await Run(FakeClaude(),
            BoardHarnessTests.Tickets(), BoardHarnessTests.Note("Use the new pricing API."), BoardHarnessTests.Time());

        Assert.True(run.Status == RunStatus.Succeeded, $"run {run.Status}: {run.ErrorMessage}");
        Assert.Equal("", await NoteText(noteId));
        Assert.Contains(log, l => l.Message.StartsWith("Tickets from 'Sprint board': #1234 Fix the costing"));
        Assert.Contains(log, l => l.Message.StartsWith("One-off prompt 'Next run' taken for this run and cleared"));
        var booked = Assert.Single(_fake.Booked);
        Assert.Equal("PROJ-7", booked["originTaskId"]!.GetValue<string>());
        Assert.Equal(300, booked["timeSpentSeconds"]!.GetValue<int>());           // a quick run still books the five-minute minimum
        Assert.Contains(log, l => l.Message.StartsWith("Tempo: booked Developing 5m"));
    }
}
