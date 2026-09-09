using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Looper.Api.Tests;

// ============================================================================
// Settings: Claude Code subscription (default) or an API key. The choice is applied
// to every CLI launch deterministically — set the key, or strip an inherited one —
// and "API key mode with no key" is refused, never quietly downgraded.
// ============================================================================

public sealed class ClaudeAuthTests
{
    private const string Key = "sk-ant-api03-0123456789abcdefABCD";

    [Fact]
    public void Api_key_mode_puts_the_key_in_the_child_environment_and_describes_it_by_its_tail()
    {
        var startInfo = new ProcessStartInfo();
        var description = ClaudeAuth.Apply(startInfo, new ClaudeAuthSettings(ClaudeAuthMode.ApiKey, Key, null));

        Assert.Equal(Key, startInfo.Environment[ClaudeAuth.ApiKeyVariable]);
        Assert.Equal("API key (…ABCD)", description);
        Assert.DoesNotContain(Key, description);
    }

    [Fact]
    public void Subscription_mode_strips_an_inherited_key_so_the_login_is_actually_used()
    {
        var startInfo = new ProcessStartInfo();
        startInfo.Environment[ClaudeAuth.ApiKeyVariable] = "sk-ant-inherited-from-the-server-shell";

        var description = ClaudeAuth.Apply(startInfo, ClaudeAuthSettings.Default);

        Assert.False(startInfo.Environment.ContainsKey(ClaudeAuth.ApiKeyVariable));
        Assert.Equal("Claude Code subscription", description);
    }

    [Fact]
    public void Api_key_mode_without_a_stored_key_fails_closed()
    {
        var ex = Assert.Throws<ClaudeAuthException>(() =>
            ClaudeAuth.Apply(new ProcessStartInfo(), new ClaudeAuthSettings(ClaudeAuthMode.ApiKey, "  ", null)));
        Assert.Contains("no key is stored", ex.Message);
    }

    [Theory]
    [InlineData("sk-ant-api03-0123456789abcdef", null)]
    [InlineData("", "Enter an API key.")]
    [InlineData("sk-ant-api03 0123456789abcdef", "spaces")]
    [InlineData("eyJhbGciOi.some-oauth-token-pasted-by-mistake", "sk-ant-")]
    [InlineData("sk-ant-short", "too short")]
    public void Keys_are_checked_for_the_obvious_paste_mistakes(string key, string? expectedFragment)
    {
        var error = ClaudeAuth.ValidateApiKey(key);
        if (expectedFragment is null) Assert.Null(error);
        else Assert.Contains(expectedFragment, error);
    }
}

public class SettingsApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();
    private const string Key = "sk-ant-api03-settings-test-key-0000wxyz";

    private sealed record SettingsResponse(string ClaudeAuthMode, bool HasApiKey, string? ApiKeyHint, DateTime? UpdatedAtUtc);

    [Fact]
    public async Task Settings_round_trip_masks_the_key_and_refuses_api_key_mode_without_a_key()
    {
        // Fresh install: subscription, nothing stored.
        var initial = await _client.GetFromJsonAsync<SettingsResponse>("/api/settings", TestJson.Options);
        Assert.Equal("Subscription", initial!.ClaudeAuthMode);
        Assert.False(initial.HasApiKey);

        // Choosing "API key" without pasting one is refused up front.
        var refused = await _client.PutAsJsonAsync("/api/settings", new { claudeAuthMode = "ApiKey" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Paste an Anthropic API key", await refused.Content.ReadAsStringAsync());

        // A key that is not an Anthropic key is refused too.
        var wrongShape = await _client.PutAsJsonAsync("/api/settings", new { claudeAuthMode = "ApiKey", apiKey = "not-a-key-at-all" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, wrongShape.StatusCode);
        Assert.Contains("sk-ant-", await wrongShape.Content.ReadAsStringAsync());

        // Store a real-looking key: the response says one exists and shows its tail, never the key.
        var saved = await _client.PutAsJsonAsync("/api/settings", new { claudeAuthMode = "ApiKey", apiKey = Key }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var savedBody = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, savedBody);
        var stored = System.Text.Json.JsonSerializer.Deserialize<SettingsResponse>(savedBody, TestJson.Options)!;
        Assert.Equal("ApiKey", stored.ClaudeAuthMode);
        Assert.True(stored.HasApiKey);
        Assert.Equal("…wxyz", stored.ApiKeyHint);
        Assert.NotNull(stored.UpdatedAtUtc);

        // Saving again with the sentinel (what the UI sends when the field was left alone) keeps the key.
        var kept = await (await _client.PutAsJsonAsync("/api/settings", new { claudeAuthMode = "ApiKey", apiKey = "__SECRET_UNCHANGED__" }, TestJson.Options))
            .Content.ReadFromJsonAsync<SettingsResponse>(TestJson.Options);
        Assert.True(kept!.HasApiKey);
        Assert.Equal("…wxyz", kept.ApiKeyHint);

        // Clearing the key while API-key mode is selected is refused; with subscription it is fine.
        var clearInApiMode = await _client.PutAsJsonAsync("/api/settings", new { claudeAuthMode = "ApiKey", clearApiKey = true }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, clearInApiMode.StatusCode);

        var back = await (await _client.PutAsJsonAsync("/api/settings", new { claudeAuthMode = "Subscription", clearApiKey = true }, TestJson.Options))
            .Content.ReadFromJsonAsync<SettingsResponse>(TestJson.Options);
        Assert.Equal("Subscription", back!.ClaudeAuthMode);
        Assert.False(back.HasApiKey);
        Assert.Null(back.ApiKeyHint);

        // Switching back to subscription can also keep the key parked for later.
        await _client.PutAsJsonAsync("/api/settings", new { claudeAuthMode = "ApiKey", apiKey = Key }, TestJson.Options);
        var parked = await (await _client.PutAsJsonAsync("/api/settings", new { claudeAuthMode = "Subscription" }, TestJson.Options))
            .Content.ReadFromJsonAsync<SettingsResponse>(TestJson.Options);
        Assert.Equal("Subscription", parked!.ClaudeAuthMode);
        Assert.True(parked.HasApiKey);
    }
}

/// <summary>The setting reaches the spawned CLI: the fake `claude` reports what it saw in its environment.</summary>
public sealed class ClaudeAuthHarnessTests : IDisposable
{
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly string _dir;
    private readonly string _seenFile;
    private readonly string _fakeClaude;
    private const string Key = "sk-ant-api03-harness-test-key-0000abcd";

    public ClaudeAuthHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"looper-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "harness.db")}").Options;
        using (var db = new LooperDbContext(_options)) db.Database.EnsureCreated();

        _seenFile = Path.Combine(_dir, "seen-key.txt");
        _fakeClaude = Path.Combine(_dir, "claude");
        File.WriteAllText(_fakeClaude,
            $"#!/bin/bash\nprintf '%s' \"${{ANTHROPIC_API_KEY:-<none>}}\" > '{_seenFile}'\n" +
            "echo '{\"result\":\"done\",\"total_cost_usd\":0.01,\"is_error\":false,\"num_turns\":1,\"duration_ms\":5,\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}'\n");
        File.SetUnixFileMode(_fakeClaude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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

    private AgentRunCoordinator CreateCoordinator()
    {
        var looperOptions = Options.Create(new LooperOptions { MaxConcurrentRuns = 2, RunTimeoutMinutes = 5, ClaudeCommand = _fakeClaude });
        var registry = new ResourceModuleRegistry(NullLogger<ResourceModuleRegistry>.Instance);
        var auth = new ClaudeAuthProvider(new Factory(_options));
        return new AgentRunCoordinator(
            new Factory(_options),
            new ClaudeCliExecutor(looperOptions, auth, registry,
                new GraphContextService(looperOptions, NullLogger<GraphContextService>.Instance),
                NullLogger<ClaudeCliExecutor>.Instance),
            new SimulatedAgentExecutor(),
            new TestingActionRunner(looperOptions),
            new ScriptRunner(looperOptions),
            new MetricRecorder(new Factory(_options), NullLogger<MetricRecorder>.Instance),
            new ReviewRunner(looperOptions, auth, NullLogger<ReviewRunner>.Instance),
            new EventDispatcher(NullLogger<EventDispatcher>.Instance),
            looperOptions,
            NullLogger<AgentRunCoordinator>.Instance);
    }

    private async Task<Guid> SeedAgent(params AppSetting[] settings)
    {
        await using var db = new LooperDbContext(_options);
        db.Settings.AddRange(settings);
        var agent = new LoopAgent
        {
            Name = "Real loop", Prompt = "Do the thing.", Model = "claude-opus-5",
            DryRun = false, MaxTurns = 5, WorkingDirectory = _dir
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync();
        return agent.Id;
    }

    private async Task<(AgentRun Run, List<string> Log)> WaitForCompletion(Guid runId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            await using var db = new LooperDbContext(_options);
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
            if (run is not null && run.Status != RunStatus.Running)
            {
                var log = await db.RunLogs.AsNoTracking().Where(l => l.RunId == runId).Select(l => l.Message).ToListAsync();
                return (run, log);
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("run did not complete");
    }

    [Fact]
    public async Task With_api_key_selected_the_cli_receives_the_key_and_the_run_log_says_so_without_leaking_it()
    {
        var agentId = await SeedAgent(
            new AppSetting { Key = AppSettingKeys.ClaudeAuthMode, Value = "ApiKey" },
            new AppSetting { Key = AppSettingKeys.ClaudeApiKey, Value = Key });

        var runId = await CreateCoordinator().TriggerRunAsync(agentId, RunTrigger.Manual);
        var (run, log) = await WaitForCompletion(runId!.Value);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Equal(Key, await File.ReadAllTextAsync(_seenFile));
        Assert.Contains(log, line => line.Contains("auth=API key (…abcd)"));
        Assert.DoesNotContain(log, line => line.Contains(Key));
    }

    [Fact]
    public async Task With_the_subscription_selected_the_run_log_says_so()
    {
        var agentId = await SeedAgent(new AppSetting { Key = AppSettingKeys.ClaudeAuthMode, Value = "Subscription" });

        var runId = await CreateCoordinator().TriggerRunAsync(agentId, RunTrigger.Manual);
        var (run, log) = await WaitForCompletion(runId!.Value);

        Assert.Equal(RunStatus.Succeeded, run.Status);
        Assert.Contains(log, line => line.Contains("auth=Claude Code subscription"));
    }

    [Fact]
    public async Task Api_key_mode_with_no_stored_key_fails_the_run_before_the_cli_starts()
    {
        var agentId = await SeedAgent(new AppSetting { Key = AppSettingKeys.ClaudeAuthMode, Value = "ApiKey" });

        var runId = await CreateCoordinator().TriggerRunAsync(agentId, RunTrigger.Manual);
        var (run, _) = await WaitForCompletion(runId!.Value);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Contains("no key is stored", run.ErrorMessage);
        Assert.False(File.Exists(_seenFile));   // the CLI never ran
    }
}
