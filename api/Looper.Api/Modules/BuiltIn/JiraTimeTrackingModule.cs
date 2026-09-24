using System.Text.RegularExpressions;
using Looper.Api.Domain;
using Looper.Api.Infrastructure.Boards;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// The Jira issue a run's time goes against — the "time goes against" picker on Ticket Filler's
/// Board page. The resource's workbench card searches Jira for the issue (the edit form holds only
/// the connection and booking choices); after each real run Looper books the run's
/// wall-clock time to Tempo on the 5-minute grid, around whatever the timesheet already holds,
/// tagged with the first selected Azure DevOps ticket. A harness action: the model never books
/// time and never sees the token.
/// </summary>
public sealed partial class JiraTimeTrackingModule : IResourceTypeModule
{
    public const string TypeKey_ = "JiraTimeTracking";

    public static readonly string[] BookingOptions = ["After every run", "After successful runs", "Never"];

    public string TypeKey => TypeKey_;
    public string DisplayName => "Jira time tracking";
    public string Icon => "⏱️";
    public string Blurb => "The Jira issue your time goes against: after each run Looper books the run's time to Tempo, tagged with the first selected ticket.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("baseUrl", "Jira address", ResourceFieldKind.Text, Required: true,
            Hint: "Your Jira Data Center URL, where Tempo Timesheets is installed.",
            Placeholder: "https://jira.example.com"),
        new("pat", "Personal access token", ResourceFieldKind.Password, Required: true,
            Hint: "A Jira personal access token. Looper uses it to find issues and book your worklogs; the agent never sees it."),
        new("issueKey", "Jira issue", ResourceFieldKind.Text,
            Hint: "The issue the time goes against — pick it on the resource's workbench card, where Jira can be searched.",
            Placeholder: "PROJ-123"),
        new("issueSummary", "Issue title", ResourceFieldKind.Text,
            Hint: "Filled in when you pick the issue."),
        new("booking", "Book time in Tempo", ResourceFieldKind.Select, Options: BookingOptions,
            Hint: "One worklog per real run that reached the model: its wall-clock time on the 5-minute grid (at least 5 minutes), " +
                  "placed around worklogs already in your timesheet and tagged with the first selected ticket. Dry runs, and runs " +
                  "that were cancelled, timed out or stopped before the model started, are not booked."),
        new("activity", "Activity", ResourceFieldKind.Select, Options: JiraTempoClient.Activities,
            Hint: "The Tempo activity the worklogs carry. Empty = Developing.")
    ];

    /// <summary>
    /// A malformed address or key is caught on save. A run that books time cannot do without the
    /// address, token and issue; one set never to book has nothing to check.
    /// </summary>
    public void PrepareRun(ResourceModuleContext context)
    {
        var config = JiraTimeTrackingResources.Parse(context);
        if (config.BaseUrl.Length > 0 && !IsHttpUrl(config.BaseUrl))
        {
            throw new InvalidOperationException($"'{config.BaseUrl}' is not a Jira address — give the full URL, e.g. https://jira.example.com.");
        }
        if (config.IssueKey.Length > 0 && !IsIssueKey(config.IssueKey))
        {
            throw new InvalidOperationException($"'{config.IssueKey}' is not a Jira issue key — keys look like PROJ-123.");
        }
        if (context.AgentId is null || config.Booking == TimeBooking.Never) return;

        if (!config.HasConnection)
        {
            throw new InvalidOperationException("Fill in the Jira address and personal access token.");
        }
        if (config.IssueKey.Length == 0)
        {
            throw new InvalidOperationException("No Jira issue is selected: open the resource and pick the issue the time goes against.");
        }
    }

    /// <summary>Nothing for the model: booking time is the harness's job, after the run (see BoardHarness).</summary>
    public ResourceContribution Contribute(ResourceModuleContext context) => new();

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>PROJ-123: a project key, a dash, a number. Upper case — keys are stored normalized.</summary>
    public static bool IsIssueKey(string key) => IssueKeyRegex().IsMatch(key);

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]*-[0-9]+$")]
    private static partial Regex IssueKeyRegex();
}

public enum TimeBooking
{
    EveryRun,
    SuccessfulRuns,
    Never
}

/// <summary>Parsed view of a Jira time-tracking resource. The token is read here and handed to the client — nowhere else.</summary>
public sealed record JiraTimeTrackingConfig(
    string BaseUrl,
    string? Pat,
    string IssueKey,
    string IssueSummary,
    TimeBooking Booking,
    string Activity)
{
    public bool HasConnection => BaseUrl.Length > 0 && !string.IsNullOrWhiteSpace(Pat);

    public JiraConnection Connection => new(BaseUrl, Pat ?? "");

    /// <summary>"PROJ-123 — The summary", or just the key.</summary>
    public string IssueLabel => IssueSummary.Length > 0 ? $"{IssueKey} — {IssueSummary}" : IssueKey;
}

public static class JiraTimeTrackingResources
{
    /// <summary>The keys the resource's canvas card owns: the edit form leaves them alone (see CardFields).</summary>
    public static readonly string[] CardKeys = ["issueKey", "issueSummary"];

    public static bool IsTimeTracking(Resource resource) =>
        resource.Type == ResourceType.Custom &&
        string.Equals(resource.CustomTypeKey, JiraTimeTrackingModule.TypeKey_, StringComparison.OrdinalIgnoreCase);

    public static JiraTimeTrackingConfig Parse(Resource resource) => Parse(new ResourceModuleContext(resource.ConfigJson));

    public static JiraTimeTrackingConfig Parse(ResourceModuleContext context)
    {
        var activity = context.GetString("activity")?.Trim();
        return new JiraTimeTrackingConfig(
            context.GetString("baseUrl")?.Trim().TrimEnd('/') ?? "",
            context.GetString("pat")?.Trim(),
            context.GetString("issueKey")?.Trim().ToUpperInvariant() ?? "",
            context.GetString("issueSummary")?.Trim() ?? "",
            context.GetString("booking")?.Trim() switch
            {
                "After successful runs" => TimeBooking.SuccessfulRuns,
                "Never" => TimeBooking.Never,
                _ => TimeBooking.EveryRun
            },
            string.IsNullOrWhiteSpace(activity) ? JiraTempoClient.DefaultActivity : activity);
    }

    public static IReadOnlyList<Resource> Attached(IEnumerable<Resource> resources) =>
        resources.Where(IsTimeTracking).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
