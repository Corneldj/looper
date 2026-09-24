using System.Text;
using FluentValidation;
using Looper.Api.Domain;
using Looper.Api.Infrastructure.Boards;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// Tickets to work on, picked from an Azure DevOps board — Ticket Filler's Board page as a
/// resource. The editor lists the board's open work items and saves the ids you tick. Before
/// every real run Looper reads those items fresh and hands the agent their full text
/// (description, acceptance criteria, repro steps); the run also gets a get_work_item tool for
/// whatever the prompt had to cut short. The token stays on the API machine, and nothing here
/// writes to Azure DevOps.
/// </summary>
public sealed class AzureDevOpsTicketsModule : IResourceTypeModule
{
    public const string TypeKey_ = "AzureDevOpsTickets";

    public static readonly string[] AssigneeOptions = ["Assigned to me", "Assigned to me or unassigned", "Anyone"];

    public static readonly string[] DefaultExcludedStates = ["Closed", "Done", "Removed", "Resolved"];

    public string TypeKey => TypeKey_;
    public string DisplayName => "Azure DevOps tickets";
    public string Icon => "🎫";
    public string Blurb => "Work items picked from your Azure DevOps board: every run gets their full text — description, acceptance criteria, repro steps — fetched fresh.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("organization", "Organization", ResourceFieldKind.Text, Required: true,
            Hint: "Your Azure DevOps organization: the name after dev.azure.com/, or the organization's full URL.",
            Placeholder: "contoso"),
        new("project", "Project", ResourceFieldKind.Text, Required: true, Placeholder: "Fabrikam"),
        new("pat", "Personal access token", ResourceFieldKind.Password, Required: true,
            Hint: "Needs Work Items (Read). Looper uses it to list and read the tickets; the agent never sees it."),
        new("assignee", "Show tickets", ResourceFieldKind.Select, Options: AssigneeOptions,
            Hint: "Which open tickets the picker lists. Empty = assigned to me."),
        new("team", "Team", ResourceFieldKind.Text, Hint: "Only this team's tickets (its area paths). Optional."),
        new("tag", "Tag", ResourceFieldKind.Text, Hint: "Only tickets carrying this tag. Optional."),
        new("workItemTypes", "Work item types", ResourceFieldKind.Text,
            Hint: "Comma-separated, e.g. Bug, User Story. Empty = every type."),
        new("excludedStates", "Hidden states", ResourceFieldKind.Text,
            Hint: "States the picker leaves out, comma-separated. Empty = Closed, Done, Removed, Resolved.",
            Placeholder: "Closed, Done, Removed, Resolved"),
        new("ticketIds", "Selected tickets", ResourceFieldKind.Text,
            Hint: "The work item ids every run works on, comma-separated — tick them in the list, or pick them on the resource's workbench card.",
            Placeholder: "12345, 12346"),
        new("clearAfterSuccess", "Clear the selection after a successful run", ResourceFieldKind.Boolean,
            Hint: "Every run then needs a fresh pick. A run that fails keeps the selection for a retry.")
    ];

    /// <summary>
    /// A typo in the ids is caught on save. An empty selection is fine while you set the resource
    /// up, but a run cannot do without tickets: it is refused before it starts.
    /// </summary>
    public void PrepareRun(ResourceModuleContext context)
    {
        var config = AzureDevOpsTicketsResources.Parse(context);
        if (config.InvalidIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"{string.Join(", ", config.InvalidIds.Select(i => $"'{i}'"))} {(config.InvalidIds.Count == 1 ? "is not a work item id" : "are not work item ids")} — ids are numbers, e.g. 12345.");
        }
        if (context.AgentId is null) return;

        if (!config.HasConnection)
        {
            throw new InvalidOperationException("Fill in the organization, project and personal access token.");
        }
        if (config.TicketIds.Count == 0)
        {
            throw new InvalidOperationException("No tickets are selected: open the resource and tick the tickets to work on.");
        }
    }

    /// <summary>Nothing static to add: Looper reads the tickets itself as a run starts (see BoardHarness).</summary>
    public ResourceContribution Contribute(ResourceModuleContext context) => new();
}

/// <summary>Parsed view of an Azure DevOps tickets resource. The token is read here and handed to the client — nowhere else.</summary>
public sealed record AzureDevOpsTicketsConfig(
    string Organization,
    string Project,
    string? Pat,
    TicketAssignee Assignee,
    string? Team,
    string? Tag,
    IReadOnlyList<string> WorkItemTypes,
    IReadOnlyList<string> ExcludedStates,
    IReadOnlyList<int> TicketIds,
    IReadOnlyList<string> InvalidIds,
    bool ClearAfterSuccess)
{
    public bool HasConnection =>
        Organization.Length > 0 && Project.Length > 0 && !string.IsNullOrWhiteSpace(Pat);

    public AzureDevOpsConnection Connection => new(Organization, Project, Pat ?? "");

    public TicketFilter Filter => new(Assignee, Team, Tag, WorkItemTypes, ExcludedStates);
}

public static class AzureDevOpsTicketsResources
{
    /// <summary>
    /// The key the resource's canvas card owns. The edit modal edits it too, but sends it only when
    /// changed there — an update that leaves it out keeps what the card (or a run) stored (see CardFields).
    /// </summary>
    public static readonly string[] CardKeys = ["ticketIds"];

    /// <summary>More than this many tickets is not one run's work — and would not fit the prompt budget in any useful depth.</summary>
    public const int MaxSelected = 50;

    /// <summary>
    /// What all the tickets of one run may put in the prompt, combined. The prompt travels as a
    /// command-line argument, so it is kept well inside the operating system's limit; the rest
    /// of any long text is one get_work_item call away.
    /// </summary>
    public const int TextBudget = 10_000;

    /// <summary>No field is cut shorter than this, however many tickets share the budget.</summary>
    private const int MinimumFieldLength = 400;

    public static bool IsTickets(Resource resource) =>
        resource.Type == ResourceType.Custom &&
        string.Equals(resource.CustomTypeKey, AzureDevOpsTicketsModule.TypeKey_, StringComparison.OrdinalIgnoreCase);

    public static AzureDevOpsTicketsConfig Parse(Resource resource) => Parse(new ResourceModuleContext(resource.ConfigJson));

    public static AzureDevOpsTicketsConfig Parse(ResourceModuleContext context)
    {
        var (ids, invalid) = ParseIds(context.GetString("ticketIds"));
        var excluded = List(context.GetString("excludedStates"));
        return new AzureDevOpsTicketsConfig(
            context.GetString("organization")?.Trim() ?? "",
            context.GetString("project")?.Trim() ?? "",
            context.GetString("pat")?.Trim(),
            context.GetString("assignee")?.Trim() switch
            {
                "Assigned to me or unassigned" => TicketAssignee.MeOrUnassigned,
                "Anyone" => TicketAssignee.Anyone,
                _ => TicketAssignee.Me
            },
            Blank(context.GetString("team")),
            Blank(context.GetString("tag")),
            List(context.GetString("workItemTypes")),
            excluded.Count > 0 ? excluded : AzureDevOpsTicketsModule.DefaultExcludedStates,
            ids,
            invalid,
            context.GetBool("clearAfterSuccess"));
    }

    /// <summary>"12345, #12346 12347" → [12345, 12346, 12347]: separators are commas, semicolons or spaces; a leading # is fine.</summary>
    public static (IReadOnlyList<int> Ids, IReadOnlyList<string> Invalid) ParseIds(string? text)
    {
        var ids = new List<int>();
        var invalid = new List<string>();
        foreach (var token in (text ?? "").Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.TrimStart('#'), out var id) && id > 0)
            {
                if (!ids.Contains(id)) ids.Add(id);
            }
            else
            {
                invalid.Add(token);
            }
        }
        return (ids, invalid);
    }

    public static string FormatIds(IEnumerable<int> ids) => string.Join(", ", ids);

    /// <summary>The ticket resources on a run, by name — the tool's choice list.</summary>
    public static IReadOnlyList<Resource> Attached(IEnumerable<Resource> resources) =>
        resources.Where(IsTickets).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every ticket selected on the run, in resource order, each once — the first one is the time-tracking reference.</summary>
    public static IReadOnlyList<int> SelectedIds(IEnumerable<Resource> resources) =>
        Attached(resources).SelectMany(r => Parse(r).TicketIds).Distinct().ToList();

    /// <summary>
    /// The resource a tool call means: the only one attached, or the one it names. Refusals are
    /// validation errors, so the model reads them as tool errors and can correct the call.
    /// </summary>
    public static Resource Resolve(IEnumerable<Resource> resources, string? name)
    {
        var attached = Attached(resources);
        if (attached.Count == 0) throw new ValidationException("No Azure DevOps tickets resource is attached to this agent.");

        var names = string.Join(", ", attached.Select(r => $"\"{r.Name}\""));
        if (string.IsNullOrWhiteSpace(name))
        {
            return attached.Count == 1
                ? attached[0]
                : throw new ValidationException($"Say which Azure DevOps tickets resource: {names}.");
        }
        return attached.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ValidationException($"No Azure DevOps tickets resource named \"{name}\" on this agent. Attached: {names}.");
    }

    /// <summary>
    /// The prompt section for one resource's tickets: every item's full text, shared out of
    /// <see cref="TextBudget"/>. The agent's own prompt says what to do with them; this section
    /// says what they are, and how to reference them so Azure DevOps links the work.
    /// </summary>
    public static string PromptSection(string resourceName, IReadOnlyList<WorkItem> items)
    {
        var references = string.Join(" ", items.Select(i => $"#{i.Id}"));
        var perTicket = TextBudget / Math.Max(1, items.Count);
        var cut = false;

        var body = new StringBuilder();
        foreach (var item in items)
        {
            body.AppendLine().AppendLine($"## Work item #{item.Id}: {item.Title}");
            var facts = new List<string> { $"Type: {item.Type}", $"State: {item.State}" };
            if (item.Priority is { } priority) facts.Add($"Priority: {priority}");
            body.AppendLine("- " + string.Join(" · ", facts));
            if (item.Tags.Count > 0) body.AppendLine($"- Tags: {string.Join(", ", item.Tags)}");
            if (item.AssignedTo.Length > 0) body.AppendLine($"- Assigned to: {item.AssignedTo}");
            body.AppendLine($"- Link: {item.Url}");

            var texts = new List<(string Heading, string Text)> { ("Description", item.Description) };
            if (item.AcceptanceCriteria.Length > 0) texts.Add(("Acceptance criteria", item.AcceptanceCriteria));
            if (item.ReproSteps.Length > 0) texts.Add(("Repro steps", item.ReproSteps));
            var perField = Math.Max(MinimumFieldLength, perTicket / texts.Count);
            foreach (var (heading, text) in texts)
            {
                body.AppendLine().AppendLine($"### {heading}");
                if (text.Length == 0)
                {
                    body.AppendLine("(none given)");
                }
                else if (text.Length > perField)
                {
                    body.AppendLine(text[..perField].TrimEnd()).AppendLine($"[… cut short — get_work_item({item.Id}) has the rest]");
                    cut = true;
                }
                else
                {
                    body.AppendLine(text);
                }
            }
        }

        var lead = items.Count == 1
            ? $"WORK ITEM — this run is about Azure DevOps work item #{items[0].Id} (selected in \"{resourceName}\"). " +
              $"Reference it as #{items[0].Id} in commit messages and pull request titles so Azure DevOps links the work."
            : $"WORK ITEMS — this run is about the following {items.Count} Azure DevOps work items (selected in \"{resourceName}\"). " +
              "Treat them as one set: read them all before you start and plan across them. " +
              $"Reference them as {references} in commit messages and pull request titles so Azure DevOps links the work.";
        if (cut) lead += " Long text is cut short where marked; the Looper tool get_work_item reads any work item in full.";
        return lead + "\n" + body.ToString().TrimEnd();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> List(string? text) =>
        (text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
}
