using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Looper.Api.Infrastructure.Delivery;

public sealed record PrSnapshot(
    string State,               // open | merged | closed
    string Title,
    int Number,
    int Additions,
    int Deletions,
    DateTime? MergedAtUtc,
    DateTime? ClosedAtUtc,
    string? MergeCommitSha,
    int ReviewRounds,           // CHANGES_REQUESTED reviews
    int ReviewComments,         // inline review comments
    int HumanCommits);          // commits without a Claude co-author trailer

/// <summary>
/// Reads PR state from GitHub via the gh CLI (the user's existing auth — no tokens stored).
/// Only github.com URLs are inspectable; anything else stays manually maintained.
/// </summary>
public sealed partial class GitHubPrInspector(ILogger<GitHubPrInspector> logger)
{
    private bool? _ghAvailable;

    public static (string Owner, string Repo, int Number)? ParseUrl(string? url)
    {
        if (url is null) return null;
        var match = PrUrl().Match(url);
        return match.Success
            ? (match.Groups[1].Value, match.Groups[2].Value, int.Parse(match.Groups[3].Value))
            : null;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        if (_ghAvailable is bool cached) return cached;
        var (exitCode, _, _) = await RunGh(["auth", "status"], cancellationToken);
        _ghAvailable = exitCode == 0;
        if (!_ghAvailable.Value)
        {
            logger.LogInformation("gh CLI unavailable or unauthenticated — GitHub PR sync disabled, manual updates only");
        }
        return _ghAvailable.Value;
    }

    /// <summary>Null when the URL is not a GitHub PR; throws with a readable message on API failure.</summary>
    public async Task<PrSnapshot?> FetchAsync(string url, CancellationToken cancellationToken)
    {
        if (ParseUrl(url) is not var (owner, repo, number) || owner is null) return null;
        var basePath = $"repos/{owner}/{repo}/pulls/{number}";

        var pr = await GhJson(basePath, cancellationToken);
        var reviews = await GhJson($"{basePath}/reviews?per_page=100", cancellationToken);
        var commits = await GhJson($"{basePath}/commits?per_page=100", cancellationToken);

        var merged = pr.TryGetProperty("merged_at", out var mergedProp) && mergedProp.ValueKind == JsonValueKind.String;
        var state = merged ? "merged" : pr.GetProperty("state").GetString()!; // open | closed

        var reviewRounds = 0;
        var reviewBodies = 0;
        foreach (var review in reviews.EnumerateArray())
        {
            if (review.GetProperty("state").GetString() == "CHANGES_REQUESTED") reviewRounds++;
            if (review.TryGetProperty("body", out var body) && !string.IsNullOrWhiteSpace(body.GetString())) reviewBodies++;
        }

        var humanCommits = 0;
        foreach (var commit in commits.EnumerateArray())
        {
            var message = commit.GetProperty("commit").GetProperty("message").GetString() ?? "";
            if (!message.Contains("Co-Authored-By: Claude", StringComparison.OrdinalIgnoreCase)) humanCommits++;
        }

        return new PrSnapshot(
            state,
            pr.GetProperty("title").GetString() ?? "",
            number,
            pr.GetProperty("additions").GetInt32(),
            pr.GetProperty("deletions").GetInt32(),
            GetDate(pr, "merged_at"),
            GetDate(pr, "closed_at"),
            pr.TryGetProperty("merge_commit_sha", out var sha) ? sha.GetString() : null,
            reviewRounds,
            pr.GetProperty("review_comments").GetInt32() + reviewBodies,
            humanCommits);
    }

    private static DateTime? GetDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetDateTime().ToUniversalTime()
            : null;

    private async Task<JsonElement> GhJson(string path, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await RunGh(["api", path], cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"gh api {path} failed: {Truncate(stderr, 200)}");
        }
        using var document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGh(
        string[] args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (-1, "", ex.Message);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value.Trim() : value[..max].Trim() + "…";

    [GeneratedRegex(@"github\.com/([^/\s]+)/([^/\s]+)/pull/(\d+)")]
    private static partial Regex PrUrl();
}
