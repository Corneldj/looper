using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Looper.Api.Infrastructure.Boards;

/// <summary>Jira or Tempo refused or failed a request. The message is safe to show: never the token.</summary>
public sealed class JiraException(string message) : Exception(message);

/// <summary>A Jira Data Center instance and the personal access token that acts as the user there.</summary>
public sealed record JiraConnection(string BaseUrl, string Pat);

public sealed record JiraIssue(string Key, string Summary);

/// <summary>One Tempo worklog to book. The start is local wall-clock time, which is what Tempo stores.</summary>
public sealed record TempoWorklog(
    string IssueKey,
    string Activity,
    string? DevOpsReference,
    string Comment,
    DateTime StartLocal,
    int Seconds);

/// <summary>
/// The only code that talks to Jira and Tempo Timesheets: the issue typeahead, the user's
/// existing worklogs (so nothing is double-booked), and booking a worklog. Bearer PAT per call.
/// </summary>
public sealed class JiraTempoClient(HttpClient http)
{
    public const string HttpClientName = "jira";

    /// <summary>Tempo's activity values (the _Activity_ work attribute), as Ticket Filler books them.</summary>
    public static readonly string[] Activities = ["Developing", "Analysis", "Test", "CodeReview", "Admin", "Meeting", "Support"];

    public const string DefaultActivity = "Developing";

    /// <summary>Marel's Tempo work attributes: the activity, and the Azure DevOps work item the time belongs to.</summary>
    private const string ActivityAttribute = "_Activity_";
    private const string DevOpsReferenceAttribute = "_MarelDevOpsReference_";

    public async Task<IReadOnlyList<JiraIssue>> SearchIssuesAsync(JiraConnection connection, string query, int limit,
        CancellationToken cancellationToken)
    {
        var result = await SendAsync(HttpMethod.Get, connection,
            $"/rest/api/2/issue/picker?query={Uri.EscapeDataString(query)}&currentJQL=", null, cancellationToken);

        var issues = new List<JiraIssue>();
        foreach (var section in (result as JsonObject)?["sections"] as JsonArray ?? [])
        {
            foreach (var issue in (section as JsonObject)?["issues"] as JsonArray ?? [])
            {
                if (issue is not JsonObject found) continue;
                var key = Str(found["key"]);
                if (key.Length == 0 || issues.Any(i => i.Key == key)) continue;

                // summaryText is plain; summary carries <b> highlight tags around the matched words.
                var summary = Str(found["summaryText"]);
                if (summary.Length == 0) summary = AzureDevOpsClient.HtmlToText(Str(found["summary"]));
                issues.Add(new JiraIssue(key, summary));
                if (issues.Count >= limit) return issues;
            }
        }
        return issues;
    }

    /// <summary>Who the token books time as: the Jira user key Tempo calls the worker.</summary>
    public async Task<string> GetWorkerKeyAsync(JiraConnection connection, CancellationToken cancellationToken)
    {
        var me = await SendAsync(HttpMethod.Get, connection, "/rest/api/2/myself", null, cancellationToken) as JsonObject;
        var key = Str(me?["key"]);
        if (key.Length == 0) key = Str(me?["name"]);
        return key.Length > 0 ? key : throw new JiraException("Jira did not say which user the token belongs to.");
    }

    /// <summary>The user's worklogs on any issue in the date range, as local [start, end) spans.</summary>
    public async Task<IReadOnlyList<(DateTime StartLocal, DateTime EndLocal)>> SearchWorklogsAsync(
        JiraConnection connection, string worker, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["worker"] = new JsonArray(worker)
        };
        var result = await SendAsync(HttpMethod.Post, connection, "/rest/tempo-timesheets/4/worklogs/search", body, cancellationToken);

        var spans = new List<(DateTime, DateTime)>();
        foreach (var node in result as JsonArray ?? [])
        {
            if (node is not JsonObject worklog) continue;
            var seconds = worklog["timeSpentSeconds"] is JsonValue value && value.TryGetValue<int>(out var s) ? s : 0;
            // "2026-07-09 08:00:00.000" — the user's local time.
            if (seconds <= 0 || !DateTime.TryParse(Str(worklog["started"]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
            {
                continue;
            }
            spans.Add((start, start.AddSeconds(seconds)));
        }
        return spans;
    }

    public async Task CreateWorklogAsync(JiraConnection connection, string worker, TempoWorklog worklog, CancellationToken cancellationToken)
    {
        var attributes = new JsonObject
        {
            [ActivityAttribute] = new JsonObject { ["workAttributeId"] = 1, ["value"] = worklog.Activity }
        };
        if (!string.IsNullOrWhiteSpace(worklog.DevOpsReference))
        {
            attributes[DevOpsReferenceAttribute] = new JsonObject { ["workAttributeId"] = 2, ["value"] = worklog.DevOpsReference };
        }

        var body = new JsonObject
        {
            ["attributes"] = attributes,
            ["billableSeconds"] = worklog.Seconds,
            ["comment"] = worklog.Comment,
            ["originTaskId"] = worklog.IssueKey,
            ["started"] = TempoTime.FormatStarted(worklog.StartLocal),
            ["timeSpentSeconds"] = worklog.Seconds,
            ["worker"] = worker
        };
        await SendAsync(HttpMethod.Post, connection, "/rest/tempo-timesheets/4/worklogs/", body, cancellationToken);
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, JiraConnection connection, string path, JsonNode? body,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(connection.BaseUrl.Trim().TrimEnd('/') + path, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https"))
        {
            throw new JiraException($"'{connection.BaseUrl}' is not a Jira address — give the full URL, e.g. https://jira.example.com.");
        }

        using (var request = new HttpRequestMessage(method, url))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.Pat);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new JiraException($"Jira could not be reached at {connection.BaseUrl}: {ex.Message}");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new JiraException("Jira did not answer within the time limit.");
            }

            using (response)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new JiraException(
                        $"Jira rejected the token (HTTP {(int)response.StatusCode}): it is likely expired or lacks permission.");
                }

                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new JiraException($"Jira refused the request ({(int)response.StatusCode} {response.ReasonPhrase}): {Describe(text)}");
                }
                if (string.IsNullOrWhiteSpace(text)) return null;
                try
                {
                    return JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    throw new JiraException("Jira answered with something that is not JSON — check the address points at Jira itself.");
                }
            }
        }
    }

    /// <summary>Jira says {"errorMessages":[…],"errors":{…}}; Tempo says {"errors":[{"message":…}]}. Pull the words out.</summary>
    internal static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no details.";
        try
        {
            if (JsonNode.Parse(body) is JsonObject root)
            {
                var messages = new List<string>();
                foreach (var message in root["errorMessages"] as JsonArray ?? []) messages.Add(Str(message));
                switch (root["errors"])
                {
                    case JsonObject byField:
                        messages.AddRange(byField.Select(p => $"{p.Key}: {Str(p.Value)}"));
                        break;
                    case JsonArray list:
                        messages.AddRange(list.Select(e => Str((e as JsonObject)?["message"])));
                        break;
                }
                messages.Add(Str(root["message"]));
                var text = string.Join(" ", messages.Where(m => m.Length > 0));
                if (text.Length > 0) return text;
            }
        }
        catch (JsonException)
        {
            // Not JSON: the raw body is the best we have.
        }
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    private static string Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
