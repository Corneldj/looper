using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Features.Delivery;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Delivery;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

// ---------------------------------------------------------------------------
// GitHubPrInspector.ParseUrl
// ---------------------------------------------------------------------------

public sealed class GitHubPrUrlParsingTests
{
    [Fact]
    public void Parses_a_plain_https_pr_url()
    {
        var parsed = GitHubPrInspector.ParseUrl("https://github.com/acme/rockets/pull/7");

        Assert.NotNull(parsed);
        Assert.Equal(("acme", "rockets", 7), parsed!.Value);
    }

    [Fact]
    public void Parses_a_pr_url_with_a_trailing_path()
    {
        var parsed = GitHubPrInspector.ParseUrl("https://github.com/acme/rockets/pull/42/files#diff-0");

        Assert.NotNull(parsed);
        Assert.Equal(("acme", "rockets", 42), parsed!.Value);
    }

    [Theory]
    [InlineData("https://github.com/acme/rockets")]
    [InlineData("https://github.com/acme/rockets/issues/7")]
    [InlineData("https://github.com/acme")]
    [InlineData("https://gitlab.com/acme/rockets/-/merge_requests/7")]
    [InlineData("https://example.com/acme/rockets/pull/7")]
    [InlineData("not a url at all")]
    [InlineData(null)]
    public void Non_pr_and_non_github_urls_yield_null(string? url) =>
        Assert.Null(GitHubPrInspector.ParseUrl(url));
}

// ---------------------------------------------------------------------------
// GetDeliveryMetricsHandler — the five metrics and the autonomy recommendation
// ---------------------------------------------------------------------------

public sealed class DeliveryMetricsHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;

    public DeliveryMetricsHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private async Task<LoopAgent> SeedAgent(int autonomyLevel = 3, string name = "Metrics agent")
    {
        await using var db = new LooperDbContext(_options);
        var agent = new LoopAgent { Name = name, Prompt = "Ship something real.", AutonomyLevel = autonomyLevel };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private async Task SeedRun(Guid agentId, decimal costUsd, bool escalated = false, bool dryRun = false)
    {
        await using var db = new LooperDbContext(_options);
        db.Runs.Add(new AgentRun
        {
            AgentId = agentId,
            Status = RunStatus.Succeeded,
            CompletedAtUtc = DateTime.UtcNow,
            Model = "claude-opus-5",
            CostUsd = costUsd,
            Escalated = escalated,
            DryRun = dryRun
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Merged PRs get MergedAtUtc now; passing survivingAdditions marks the PR survival-checked.</summary>
    private async Task SeedPr(
        Guid agentId,
        PrStatus status = PrStatus.Merged,
        int additions = 0,
        int deletions = 0,
        int reviewRounds = 0,
        int reviewComments = 0,
        int humanCommits = 0,
        int? survivingAdditions = null)
    {
        await using var db = new LooperDbContext(_options);
        db.PullRequests.Add(new AgentPullRequest
        {
            AgentId = agentId,
            Title = "test pr",
            Status = status,
            MergedAtUtc = status == PrStatus.Merged ? DateTime.UtcNow : null,
            ClosedAtUtc = status == PrStatus.Closed ? DateTime.UtcNow : null,
            Additions = additions,
            Deletions = deletions,
            ReviewRounds = reviewRounds,
            ReviewComments = reviewComments,
            HumanCommits = humanCommits,
            SurvivingAdditions = survivingAdditions,
            SurvivalCheckedAtUtc = survivingAdditions is null ? null : DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<DeliveryMetricsDto> Metrics(int days = 30)
    {
        await using var db = new LooperDbContext(_options);
        return await new GetDeliveryMetricsHandler(db).Handle(new GetDeliveryMetricsQuery(days), CancellationToken.None);
    }

    [Fact]
    public async Task Dry_runs_never_enter_cost_or_escalation()
    {
        var agent = await SeedAgent();
        await SeedRun(agent.Id, 1m);
        await SeedRun(agent.Id, 1m, escalated: true);
        await SeedRun(agent.Id, 5m, dryRun: true);
        await SeedRun(agent.Id, 5m, dryRun: true);
        await SeedRun(agent.Id, 5m, dryRun: true);

        var metrics = await Metrics();

        Assert.Equal(2m, metrics.TotalCostUsd);
        Assert.Equal(2, metrics.CompletedRuns);
        Assert.Equal(1, metrics.EscalatedRuns);
        Assert.Equal(0.5, metrics.EscalationRate);
    }

    [Fact]
    public async Task Cost_per_merged_pr_divides_real_spend_by_merges()
    {
        var agent = await SeedAgent();
        await SeedRun(agent.Id, 1m);
        await SeedRun(agent.Id, 1m, escalated: true);
        await SeedRun(agent.Id, 5m, dryRun: true);
        await SeedRun(agent.Id, 5m, dryRun: true);
        await SeedRun(agent.Id, 5m, dryRun: true);
        await SeedPr(agent.Id);
        await SeedPr(agent.Id);

        var metrics = await Metrics();

        Assert.Equal(2, metrics.MergedPrs);
        Assert.Equal(1m, metrics.CostPerMergedPrUsd);
    }

    [Fact]
    public async Task Cost_per_merged_pr_is_null_without_merges()
    {
        var agent = await SeedAgent();
        await SeedRun(agent.Id, 1m);
        await SeedRun(agent.Id, 1m);

        var metrics = await Metrics();

        Assert.Equal(0, metrics.MergedPrs);
        Assert.Null(metrics.CostPerMergedPrUsd);
    }

    [Fact]
    public async Task First_pass_counts_only_clean_merged_prs()
    {
        var agent = await SeedAgent();
        await SeedPr(agent.Id);                                  // merged, 0 rounds, 0 human commits → first pass
        await SeedPr(agent.Id, reviewRounds: 1);                 // merged but reworked in review
        await SeedPr(agent.Id, humanCommits: 1);                 // merged but a human quietly fixed it
        await SeedPr(agent.Id, status: PrStatus.Open);           // clean so far, but open PRs never count

        var metrics = await Metrics();

        Assert.Equal(3, metrics.MergedPrs);
        Assert.Equal(1, metrics.OpenPrs);
        Assert.NotNull(metrics.FirstPassRate);
        Assert.Equal(1 / 3.0, metrics.FirstPassRate!.Value, 12);
    }

    [Fact]
    public async Task Survival_is_addition_weighted_over_checked_prs_only()
    {
        var agent = await SeedAgent();
        await SeedPr(agent.Id, additions: 100, survivingAdditions: 80);
        await SeedPr(agent.Id, additions: 100, survivingAdditions: 40);
        await SeedPr(agent.Id, additions: 100); // merged but never survival-checked → excluded

        var metrics = await Metrics();

        Assert.Equal(2, metrics.SurvivalCheckedPrs);
        Assert.NotNull(metrics.CodeSurvivalRate);
        Assert.Equal(0.6, metrics.CodeSurvivalRate!.Value, 12);
    }

    [Fact]
    public async Task Review_churn_is_rounds_plus_comments_per_100_changed_lines()
    {
        var agent = await SeedAgent();
        await SeedPr(agent.Id, additions: 150, deletions: 50, reviewRounds: 2, reviewComments: 4);

        var metrics = await Metrics();

        Assert.NotNull(metrics.ReviewChurnPer100Lines);
        Assert.Equal(3.0, metrics.ReviewChurnPer100Lines!.Value, 12);
    }

    [Fact]
    public async Task Recommendation_moves_the_autonomy_dial_on_evidence()
    {
        // Level 3, 3 clean completed runs, 5 merged first-pass PRs, no escalations → promote.
        var promotable = await SeedAgent(autonomyLevel: 3, name: "Promotable");
        for (var i = 0; i < 3; i++) await SeedRun(promotable.Id, 1m);
        for (var i = 0; i < 5; i++) await SeedPr(promotable.Id);

        // Same evidence at the top level — nowhere left to promote to.
        var capped = await SeedAgent(autonomyLevel: 4, name: "Capped");
        for (var i = 0; i < 3; i++) await SeedRun(capped.Id, 1m);
        for (var i = 0; i < 5; i++) await SeedPr(capped.Id);

        // Escalation rate 2/3 > 0.3 at level 2 → demote.
        var strained = await SeedAgent(autonomyLevel: 2, name: "Strained");
        await SeedRun(strained.Id, 1m);
        await SeedRun(strained.Id, 1m, escalated: true);
        await SeedRun(strained.Id, 1m, escalated: true);

        // Only 2 completed runs → not enough evidence either way.
        var unproven = await SeedAgent(name: "Unproven");
        await SeedRun(unproven.Id, 1m);
        await SeedRun(unproven.Id, 1m);

        var metrics = await Metrics();

        Assert.Equal("promote", metrics.Agents.Single(a => a.AgentId == promotable.Id).Recommendation);
        Assert.Equal("hold", metrics.Agents.Single(a => a.AgentId == capped.Id).Recommendation);
        Assert.Equal("demote", metrics.Agents.Single(a => a.AgentId == strained.Id).Recommendation);
        Assert.Null(metrics.Agents.Single(a => a.AgentId == unproven.Id).Recommendation);
    }
}

// ---------------------------------------------------------------------------
// HTTP integration — registration, escalation, manual updates, metrics
// ---------------------------------------------------------------------------

public class DeliveryApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record AgentResponse(Guid Id, string Name);
    private sealed record PrResponse(
        Guid Id, Guid AgentId, string AgentName, string Title, string? Url, string Repository, int? Number,
        string Status, DateTime? MergedAtUtc, int ReviewRounds, int HumanCommits, bool? FirstPass);
    private sealed record RunRow(Guid Id, string Status, bool Escalated);
    private sealed record MetricsResponse(int WindowDays);

    private async Task<AgentResponse> CreateAgent(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/agents", new
        {
            name,
            description = "delivery test agent",
            prompt = "Open a PR and report it.",
            model = "claude-sonnet-5",
            effort = "Medium",
            intervalMinutes = 60,
            maxTurns = 5,
            maxBudgetUsd = (decimal?)null,
            workingDirectory = (string?)null,
            allowedTools = (string?)null,
            bypassPermissions = true,
            autonomyLevel = 3,
            dryRun = true,
            resourceIds = Array.Empty<Guid>()
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AgentResponse>(TestJson.Options))!;
    }

    [Fact]
    public async Task Register_parses_the_github_url_and_upserts_by_url()
    {
        var agent = await CreateAgent("PR reporter");
        const string url = "https://github.com/acme/rockets/pull/7";

        var create = await _client.PostAsJsonAsync("/api/delivery/prs", new
        {
            agentId = agent.Id,
            url,
            title = "Add telemetry"
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<PrResponse>(TestJson.Options);
        Assert.Equal("acme/rockets", created!.Repository);
        Assert.Equal(7, created.Number);
        Assert.Equal("Add telemetry", created.Title);

        // Re-reporting the same URL updates the record instead of duplicating it.
        var upsert = await _client.PostAsJsonAsync("/api/delivery/prs", new
        {
            agentId = agent.Id,
            url,
            title = "Add telemetry (retitled)"
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, upsert.StatusCode);

        var list = await _client.GetFromJsonAsync<List<PrResponse>>(
            $"/api/delivery/prs?agentId={agent.Id}", TestJson.Options);
        var only = Assert.Single(list!);
        Assert.Equal(created.Id, only.Id);
        Assert.Equal("Add telemetry (retitled)", only.Title);
    }

    [Fact]
    public async Task Register_without_run_or_agent_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/delivery/prs", new
        {
            url = "https://github.com/acme/rockets/pull/8"
        }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_with_unknown_agent_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/delivery/prs", new
        {
            agentId = Guid.NewGuid(),
            title = "Phantom PR"
        }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Escalation_flags_the_run_and_unknown_runs_return_404()
    {
        var agent = await CreateAgent("Escalating agent");

        var trigger = await _client.PostAsJsonAsync($"/api/agents/{agent.Id}/run", new { }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);
        using var runIdDoc = JsonDocument.Parse(await trigger.Content.ReadAsStringAsync());
        var runId = runIdDoc.RootElement.GetProperty("runId").GetGuid();

        var escalate = await _client.PostAsJsonAsync($"/api/runs/{runId}/escalate",
            new { reason = "need prod credentials" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, escalate.StatusCode);

        // The run may still be Running or already finished — only the flag matters here.
        var runs = await _client.GetFromJsonAsync<List<RunRow>>($"/api/agents/{agent.Id}/runs", TestJson.Options);
        var run = Assert.Single(runs!, r => r.Id == runId);
        Assert.True(run.Escalated);

        var unknown = await _client.PostAsJsonAsync($"/api/runs/{Guid.NewGuid()}/escalate",
            new { reason = "nobody home" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Merging_sets_the_timestamp_and_first_pass_tracks_review_rounds()
    {
        var agent = await CreateAgent("Merging agent");
        var create = await _client.PostAsJsonAsync("/api/delivery/prs", new
        {
            agentId = agent.Id,
            title = "Manual forge PR"
        }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var pr = await create.Content.ReadFromJsonAsync<PrResponse>(TestJson.Options);

        var merge = await _client.PutAsJsonAsync($"/api/delivery/prs/{pr!.Id}", new
        {
            title = "Manual forge PR",
            status = "Merged",
            additions = 10,
            deletions = 2,
            reviewRounds = 0,
            reviewComments = 0,
            humanCommits = 0,
            repoPath = (string?)null,
            mergeCommitSha = (string?)null
        }, TestJson.Options);
        merge.EnsureSuccessStatusCode();
        var merged = await merge.Content.ReadFromJsonAsync<PrResponse>(TestJson.Options);
        Assert.Equal("Merged", merged!.Status);
        Assert.NotNull(merged.MergedAtUtc);
        Assert.True(merged.FirstPass);

        // One change-request round later, it is no longer a first-pass merge.
        var rework = await _client.PutAsJsonAsync($"/api/delivery/prs/{pr.Id}", new
        {
            title = "Manual forge PR",
            status = "Merged",
            additions = 10,
            deletions = 2,
            reviewRounds = 1,
            reviewComments = 0,
            humanCommits = 0,
            repoPath = (string?)null,
            mergeCommitSha = (string?)null
        }, TestJson.Options);
        rework.EnsureSuccessStatusCode();
        var reworked = await rework.Content.ReadFromJsonAsync<PrResponse>(TestJson.Options);
        Assert.False(reworked!.FirstPass);
        Assert.NotNull(reworked.MergedAtUtc);
    }

    [Fact]
    public async Task Metrics_endpoint_echoes_the_window()
    {
        var response = await _client.GetAsync("/api/delivery/metrics?days=21");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var metrics = await response.Content.ReadFromJsonAsync<MetricsResponse>(TestJson.Options);
        Assert.Equal(21, metrics!.WindowDays);
    }
}

// ---------------------------------------------------------------------------
// CodeSurvivalCalculator — against a real throwaway git repository
// ---------------------------------------------------------------------------

public sealed class CodeSurvivalCalculatorTests
{
    private const string GitBinary = "/usr/bin/git";

    [Fact]
    public async Task Survival_tracks_rewrites_and_deletions_in_a_real_repository()
    {
        if (!File.Exists(GitBinary)) return; // no git on this machine — nothing to measure

        var calculator = new CodeSurvivalCalculator(NullLogger<CodeSurvivalCalculator>.Instance);
        var repo = Directory.CreateTempSubdirectory("looper-survival-").FullName;
        try
        {
            await Git(repo, "init");
            await Git(repo, "config", "user.email", "tests@looper.local");
            await Git(repo, "config", "user.name", "Looper Tests");
            await Git(repo, "config", "commit.gpgsign", "false");

            await File.WriteAllLinesAsync(Path.Combine(repo, "file.txt"),
                Enumerable.Range(1, 10).Select(i => $"line {i}"));
            await Git(repo, "add", ".");
            await Git(repo, "commit", "-m", "base");

            // Commit B: the "PR" whose additions we measure.
            await File.WriteAllLinesAsync(Path.Combine(repo, "added.txt"),
                Enumerable.Range(1, 4).Select(i => $"added {i}"));
            await Git(repo, "add", ".");
            await Git(repo, "commit", "-m", "feature");
            var shaB = (await Git(repo, "rev-parse", "HEAD")).Trim();

            // Fresh after merge: every added line still blames to B.
            var fresh = await calculator.ComputeAsync(repo, shaB, CancellationToken.None);
            Assert.Null(fresh.Error);
            Assert.Equal(4, fresh.Additions);
            Assert.Equal(4, fresh.SurvivingAdditions);

            // Rewriting 2 of the 4 lines re-attributes them to the rewrite commit.
            await File.WriteAllLinesAsync(Path.Combine(repo, "added.txt"),
                ["added 1", "rewritten 2", "rewritten 3", "added 4"]);
            await Git(repo, "add", ".");
            await Git(repo, "commit", "-m", "rewrite two lines");
            var afterRewrite = await calculator.ComputeAsync(repo, shaB, CancellationToken.None);
            Assert.Null(afterRewrite.Error);
            Assert.Equal(4, afterRewrite.Additions);
            Assert.Equal(2, afterRewrite.SurvivingAdditions);

            // Deleting the file entirely means nothing survived.
            await Git(repo, "rm", "added.txt");
            await Git(repo, "commit", "-m", "delete the feature");
            var afterDelete = await calculator.ComputeAsync(repo, shaB, CancellationToken.None);
            Assert.Null(afterDelete.Error);
            Assert.Equal(4, afterDelete.Additions);
            Assert.Equal(0, afterDelete.SurvivingAdditions);

            // A sha the repository has never seen.
            var unknown = await calculator.ComputeAsync(repo, new string('a', 40), CancellationToken.None);
            Assert.NotNull(unknown.Error);
            Assert.Contains("not found", unknown.Error);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task A_directory_without_a_repository_reports_an_error()
    {
        if (!File.Exists(GitBinary)) return;

        var calculator = new CodeSurvivalCalculator(NullLogger<CodeSurvivalCalculator>.Instance);
        var dir = Directory.CreateTempSubdirectory("looper-norepo-").FullName;
        try
        {
            var result = await calculator.ComputeAsync(dir, "abcdef1234", CancellationToken.None);
            Assert.NotNull(result.Error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<string> Git(string repoPath, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GitBinary,
            WorkingDirectory = repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) startInfo.ArgumentList.Add(a);

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {await stderr}");
        return await stdout;
    }
}
