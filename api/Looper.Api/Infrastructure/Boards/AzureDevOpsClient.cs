using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Looper.Api.Infrastructure.Boards;

/// <summary>Azure DevOps refused or failed a request. The message is safe to show the user and the model: never the token.</summary>
public sealed class AzureDevOpsException(string message) : Exception(message);

/// <summary>Where a project lives and the token that reads it. The PAT goes into one request header and nowhere else.</summary>
public sealed record AzureDevOpsConnection(string Organization, string Project, string Pat)
{
    /// <summary>A bare organization name means dev.azure.com; a pasted organization URL is used as it is.</summary>
    public string BaseUrl => Organization.Trim().StartsWith("http", StringComparison.OrdinalIgnoreCase)
        ? Organization.Trim().TrimEnd('/')
        : $"https://dev.azure.com/{Uri.EscapeDataString(Organization.Trim())}";
}

public enum TicketAssignee
{
    Me,
    MeOrUnassigned,
    Anyone
}

/// <summary>Which open tickets a board lists — the filters Ticket Filler's Board page applies.</summary>
public sealed record TicketFilter(
    TicketAssignee Assignee,
    string? Team,
    string? Tag,
    IReadOnlyList<string> WorkItemTypes,
    IReadOnlyList<string> ExcludedStates);

/// <summary>A work item, simplified. Description, acceptance criteria and repro steps are plain text (HTML stripped).</summary>
public sealed record WorkItem(
    int Id,
    string Url,
    string Title,
    string Type,
    string State,
    string AssignedTo,
    IReadOnlyList<string> Tags,
    string AreaPath,
    string Iteration,
    int? Priority,
    string Description,
    string AcceptanceCriteria,
    string ReproSteps,
    DateTime? ChangedAtUtc);

/// <summary>
/// The only code that talks to Azure DevOps: lists a board's open work items and reads work
/// items by id. Read-only — nothing here changes a ticket. The PAT arrives per call from the
/// resource that holds it.
/// </summary>
public sealed partial class AzureDevOpsClient(HttpClient http)
{
    public const string HttpClientName = "azure-devops";
    public const string ApiVersion = "7.1";

    /// <summary>The most tickets one board query lists; the newest changes come first.</summary>
    public const int MaxListed = 200;

    /// <summary>Azure DevOps reads at most this many work items per request.</summary>
    private const int BatchSize = 200;

    /// <summary>What a listing needs; the full item (description and all) is only read for a run.</summary>
    private const string ListingFields =
        "System.Id,System.Title,System.WorkItemType,System.State,System.AssignedTo,System.Tags," +
        "System.AreaPath,System.IterationPath,System.ChangedDate,Microsoft.VSTS.Common.Priority";

    /// <summary>The board: open work items matching the filter, most recently changed first, without their long text.</summary>
    public async Task<IReadOnlyList<WorkItem>> QueryAsync(AzureDevOpsConnection connection, TicketFilter filter, CancellationToken cancellationToken)
    {
        var conditions = new List<string> { "[System.TeamProject] = @project" };
        if (filter.ExcludedStates.Count > 0)
        {
            conditions.Add("(" + string.Join(" AND ", filter.ExcludedStates.Select(s => $"[System.State] <> '{Q(s)}'")) + ")");
        }
        if (filter.WorkItemTypes.Count > 0)
        {
            conditions.Add("(" + string.Join(" OR ", filter.WorkItemTypes.Select(t => $"[System.WorkItemType] = '{Q(t)}'")) + ")");
        }
        switch (filter.Assignee)
        {
            case TicketAssignee.Me:
                conditions.Add("[System.AssignedTo] = @me");
                break;
            case TicketAssignee.MeOrUnassigned:
                conditions.Add("([System.AssignedTo] = @me OR [System.AssignedTo] = '')");
                break;
        }
        if (!string.IsNullOrWhiteSpace(filter.Team) &&
            await TeamClauseAsync(connection, filter.Team.Trim(), cancellationToken) is { } teamClause)
        {
            conditions.Add(teamClause);
        }
        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            conditions.Add($"[System.Tags] CONTAINS '{Q(filter.Tag.Trim())}'");
        }

        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE {string.Join(" AND ", conditions)} ORDER BY [System.ChangedDate] DESC";
        var result = await SendAsync(HttpMethod.Post,
            $"{ProjectUrl(connection)}/_apis/wit/wiql?api-version={ApiVersion}&$top={MaxListed}",
            connection, new JsonObject { ["query"] = wiql }, cancellationToken);

        var ids = (result["workItems"] as JsonArray ?? [])
            .Select(w => Int(w?["id"]))
            .OfType<int>()
            .Take(MaxListed)
            .ToList();
        if (ids.Count == 0) return [];

        // Read back in board order; an item deleted between the two calls simply drops out.
        return (await ReadAsync(connection, ids, ListingFields, cancellationToken)).OfType<WorkItem>().ToList();
    }

    /// <summary>Work items by id, in the order asked for. An id that does not exist (or the token cannot see) is null.</summary>
    public Task<IReadOnlyList<WorkItem?>> GetAsync(AzureDevOpsConnection connection, IReadOnlyList<int> ids, CancellationToken cancellationToken) =>
        ReadAsync(connection, ids, null, cancellationToken);

    /// <summary>As <see cref="GetAsync"/>, but only the fields a listing shows.</summary>
    public Task<IReadOnlyList<WorkItem?>> GetSummariesAsync(AzureDevOpsConnection connection, IReadOnlyList<int> ids, CancellationToken cancellationToken) =>
        ReadAsync(connection, ids, ListingFields, cancellationToken);

    private async Task<IReadOnlyList<WorkItem?>> ReadAsync(AzureDevOpsConnection connection, IReadOnlyList<int> ids,
        string? fields, CancellationToken cancellationToken)
    {
        var found = new Dictionary<int, WorkItem>();
        for (var i = 0; i < ids.Count; i += BatchSize)
        {
            var batch = string.Join(',', ids.Skip(i).Take(BatchSize));
            // errorPolicy=Omit: a deleted or invisible id comes back as null instead of failing the whole batch.
            var url = $"{connection.BaseUrl}/_apis/wit/workitems?ids={batch}&errorPolicy=Omit&api-version={ApiVersion}" +
                      (fields is null ? "" : $"&fields={fields}");
            var result = await SendAsync(HttpMethod.Get, url, connection, null, cancellationToken);
            foreach (var node in result["value"] as JsonArray ?? [])
            {
                if (node is null) continue;
                var item = Simplify(connection, node);
                if (item.Id > 0) found[item.Id] = item;
            }
        }
        return ids.Select(id => found.GetValueOrDefault(id)).ToList();
    }

    /// <summary>
    /// A team's tickets are the ones under its area paths; a team whose area is the whole
    /// project falls back to its iteration tree — the same rule Ticket Filler applies.
    /// </summary>
    private async Task<string?> TeamClauseAsync(AzureDevOpsConnection connection, string team, CancellationToken cancellationToken)
    {
        var teamUrl = $"{ProjectUrl(connection)}/{Uri.EscapeDataString(team)}/_apis/work/teamsettings";
        var areas = await SendAsync(HttpMethod.Get, $"{teamUrl}/teamfieldvalues?api-version={ApiVersion}", connection, null, cancellationToken);
        var meaningful = (areas["values"] as JsonArray ?? [])
            .Where(v => v is not null)
            .Select(v => (Value: Str(v!["value"]), IncludeChildren: v["includeChildren"] is JsonValue flag && flag.TryGetValue<bool>(out var b) && b))
            .Where(a => a.Value.Length > 0 && !string.Equals(a.Value, connection.Project, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (meaningful.Count > 0)
        {
            return "(" + string.Join(" OR ", meaningful.Select(a => a.IncludeChildren
                ? $"[System.AreaPath] UNDER '{Q(a.Value)}'"
                : $"[System.AreaPath] = '{Q(a.Value)}'")) + ")";
        }

        var iterations = await SendAsync(HttpMethod.Get, $"{teamUrl}/iterations?api-version={ApiVersion}", connection, null, cancellationToken);
        var roots = (iterations["value"] as JsonArray ?? [])
            .Select(i => Str(i?["path"]))
            .Where(p => p.Length > 0)
            .Select(p => string.Join('\\', p.Split('\\').Take(2)))
            .Where(r => r.Contains('\\'))
            .Distinct()
            .ToList();
        return roots.Count > 0
            ? "(" + string.Join(" OR ", roots.Select(r => $"[System.IterationPath] UNDER '{Q(r)}'")) + ")"
            : null;
    }

    private static WorkItem Simplify(AzureDevOpsConnection connection, JsonNode item)
    {
        var fields = item["fields"];
        var id = Int(item["id"]) ?? 0;
        return new WorkItem(
            id,
            $"{ProjectUrl(connection)}/_workitems/edit/{id}",
            Str(fields?["System.Title"]),
            Str(fields?["System.WorkItemType"]),
            Str(fields?["System.State"]),
            AssignedTo(fields?["System.AssignedTo"]),
            Str(fields?["System.Tags"]).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Str(fields?["System.AreaPath"]),
            Str(fields?["System.IterationPath"]),
            Int(fields?["Microsoft.VSTS.Common.Priority"]),
            HtmlToText(Str(fields?["System.Description"])),
            HtmlToText(Str(fields?["Microsoft.VSTS.Common.AcceptanceCriteria"])),
            HtmlToText(Str(fields?["Microsoft.VSTS.TCM.ReproSteps"])),
            DateTime.TryParse(Str(fields?["System.ChangedDate"]), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var changed)
                ? changed
                : null);
    }

    private async Task<JsonObject> SendAsync(HttpMethod method, string url, AzureDevOpsConnection connection, JsonNode? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + connection.Pat)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new AzureDevOpsException($"Azure DevOps could not be reached at {connection.BaseUrl}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AzureDevOpsException("Azure DevOps did not answer within the time limit.");
        }

        using (response)
        {
            // An expired or revoked PAT often answers 203 with an HTML sign-in page rather than a 401.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NonAuthoritativeInformation)
            {
                throw new AzureDevOpsException(
                    $"Azure DevOps rejected the token (HTTP {(int)response.StatusCode}): it is likely expired, revoked, or missing the Work Items (Read) scope.");
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new AzureDevOpsException(
                    $"Azure DevOps refused the request ({(int)response.StatusCode} {response.ReasonPhrase}): {Describe(text)}");
            }
            if (response.Content.Headers.ContentType?.MediaType is { } mediaType &&
                !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                throw new AzureDevOpsException("Azure DevOps answered with a sign-in page instead of data: the token is likely expired.");
            }
            if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
            try
            {
                return JsonNode.Parse(text) as JsonObject
                       ?? throw new AzureDevOpsException("Azure DevOps answered with an unexpected shape of data.");
            }
            catch (JsonException)
            {
                throw new AzureDevOpsException("Azure DevOps answered with something that is not JSON.");
            }
        }
    }

    /// <summary>Azure DevOps errors look like {"message":"TF401232: …","typeName":…}; pull the message out.</summary>
    internal static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no details.";
        try
        {
            if (JsonNode.Parse(body)?["message"] is JsonValue message && message.TryGetValue<string>(out var text)) return text;
        }
        catch (JsonException)
        {
            // Not JSON: the raw body is the best we have.
        }
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    /// <summary>Very light HTML to text for ticket fields: line breaks and list items survive, tags and entities do not.</summary>
    internal static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = BreakRegex().Replace(html, "\n");
        text = BlockCloseRegex().Replace(text, "\n");
        text = ListItemRegex().Replace(text, "- ");
        text = TagRegex().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = TrailingSpaceRegex().Replace(text, "\n");
        return BlankLinesRegex().Replace(text, "\n\n").Trim();
    }

    private static string ProjectUrl(AzureDevOpsConnection connection) =>
        $"{connection.BaseUrl}/{Uri.EscapeDataString(connection.Project.Trim())}";

    /// <summary>WIQL string literals double their quotes.</summary>
    private static string Q(string value) => value.Replace("'", "''");

    private static string Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";

    private static int? Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary>An identity object in current API versions; older ones send "Name &lt;email&gt;" as a string.</summary>
    private static string AssignedTo(JsonNode? node) =>
        node is JsonObject identity ? Str(identity["displayName"]) : Str(node);

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"</(p|div|li|tr|h[1-6])>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockCloseRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t]+\n")]
    private static partial Regex TrailingSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
}
