using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Infrastructure.Workspaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Looper.Api.Tests;

public sealed class WorkspaceProvisionerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"looper-ws-{Guid.NewGuid():N}");
    private readonly WorkspaceProvisioner _provisioner = new(NullLogger<WorkspaceProvisioner>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private WorkspacePoolConfig Config(string provisioning = "blank", string? source = null) =>
        new() { RootPath = _root, Provisioning = provisioning, Source = source };

    [Theory]
    [InlineData("Fix the Login Bug!", "fix-the-login-bug")]
    [InlineData("  ../../etc/passwd  ", "etc-passwd")]        // traversal collapses into a safe slug
    [InlineData("émoji & spaces", "moji-spaces")]
    public void Unit_names_are_slugged_safely(string unit, string expected)
    {
        Assert.Equal(expected, WorkspaceProvisioner.Slugify(unit));
    }

    [Fact]
    public async Task Blank_provisioning_creates_the_directory_and_seeds_the_brief()
    {
        var path = await _provisioner.ProvisionAsync(Config(), "auth-refactor", "Split the auth module.", "Test agent", default);

        Assert.StartsWith(Path.GetFullPath(_root), path);
        var brief = await File.ReadAllTextAsync(Path.Combine(path, "WORKBRIEF.md"));
        Assert.Contains("auth-refactor", brief);
        Assert.Contains("Split the auth module.", brief);   // the passed context landed in the workspace
        Assert.Contains("Handoff notes", brief);            // the cross-iteration protocol is on disk
    }

    [Fact]
    public async Task Copy_template_duplicates_the_source_folder()
    {
        var template = Path.Combine(_root, "_template");
        Directory.CreateDirectory(Path.Combine(template, "src"));
        await File.WriteAllTextAsync(Path.Combine(template, "src", "main.txt"), "seed");

        var path = await _provisioner.ProvisionAsync(
            Config("copy-template", template), "unit-a", "", "tester", default);

        Assert.Equal("seed", await File.ReadAllTextAsync(Path.Combine(path, "src", "main.txt")));
    }

    [Fact]
    public async Task Colliding_slugs_get_distinct_directories()
    {
        var first = await _provisioner.ProvisionAsync(Config(), "same-unit", "", "t", default);
        var second = await _provisioner.ProvisionAsync(Config(), "same-unit", "", "t", default);

        Assert.NotEqual(first, second);
        Assert.True(Directory.Exists(first) && Directory.Exists(second));
    }

    [Fact]
    public void Deletion_outside_the_pool_root_is_refused()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), $"looper-elsewhere-{Guid.NewGuid():N}");
        Directory.CreateDirectory(elsewhere);
        try
        {
            Assert.Throws<WorkspaceProvisioningException>(() => _provisioner.RemoveDirectory(Config(), elsewhere));
            Assert.True(Directory.Exists(elsewhere));
        }
        finally
        {
            Directory.Delete(elsewhere);
        }
    }

    [Fact]
    public async Task Git_clone_provisioning_clones_a_local_repo()
    {
        if (!File.Exists("/usr/bin/git")) return;

        var repo = Path.Combine(_root, "_origin");
        Directory.CreateDirectory(repo);
        Run(repo, "init");
        Run(repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "--allow-empty", "-m", "init");
        await File.WriteAllTextAsync(Path.Combine(repo, "hello.txt"), "hi");
        Run(repo, "add", ".");
        Run(repo, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-m", "content");

        var path = await _provisioner.ProvisionAsync(Config("git-clone", repo), "cloned-unit", "", "t", default);

        Assert.True(File.Exists(Path.Combine(path, "hello.txt")));
        Assert.True(File.Exists(Path.Combine(path, "WORKBRIEF.md"))); // brief is seeded on top of the clone
    }

    private static void Run(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo { FileName = "git", WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit(30_000);
        Assert.Equal(0, p.ExitCode);
    }
}

public sealed class WorkspaceJanitorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"looper-janitor-{Guid.NewGuid():N}");

    public WorkspaceJanitorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Factory(DbContextOptions<LooperDbContext> options) : IDbContextFactory<LooperDbContext>
    {
        public LooperDbContext CreateDbContext() => new(options);
    }

    [Fact]
    public async Task Done_workspaces_are_cleaned_only_after_their_pool_retention()
    {
        var pool = new Resource
        {
            Name = "Pool", Type = ResourceType.WorkspacePool,
            ConfigJson = $$"""{"rootPath":"{{_root.Replace("\\", "\\\\")}}","retentionDays":7}"""
        };
        var freshDir = Path.Combine(_root, "fresh-unit");
        var staleDir = Path.Combine(_root, "stale-unit");
        var activeDir = Path.Combine(_root, "active-unit");
        Directory.CreateDirectory(freshDir);
        Directory.CreateDirectory(staleDir);
        Directory.CreateDirectory(activeDir);

        await using (var db = new LooperDbContext(_options))
        {
            db.Resources.Add(pool);
            db.Workspaces.AddRange(
                new ManagedWorkspace { Resource = pool, Unit = "fresh-unit", Path = freshDir, Status = WorkspaceStatus.Done, DoneAtUtc = DateTime.UtcNow.AddDays(-2) },
                new ManagedWorkspace { Resource = pool, Unit = "stale-unit", Path = staleDir, Status = WorkspaceStatus.Done, DoneAtUtc = DateTime.UtcNow.AddDays(-10) },
                new ManagedWorkspace { Resource = pool, Unit = "active-unit", Path = activeDir, Status = WorkspaceStatus.Active });
            await db.SaveChangesAsync();
        }

        var janitor = new WorkspaceJanitorService(
            new Factory(_options),
            new WorkspaceProvisioner(NullLogger<WorkspaceProvisioner>.Instance),
            NullLogger<WorkspaceJanitorService>.Instance);

        var cleaned = await janitor.CleanupDueAsync(DateTime.UtcNow, default);

        Assert.Equal(1, cleaned);
        Assert.False(Directory.Exists(staleDir));  // past retention — removed
        Assert.True(Directory.Exists(freshDir));   // done, but inside the window
        Assert.True(Directory.Exists(activeDir));  // never touched

        await using var verify = new LooperDbContext(_options);
        Assert.Equal(WorkspaceStatus.Cleaned,
            (await verify.Workspaces.SingleAsync(w => w.Unit == "stale-unit")).Status);
    }
}

public sealed class WorkspacesApiTests(LooperApiFactory factory) : IClassFixture<LooperApiFactory>, IDisposable
{
    private readonly HttpClient _client = factory.CreateClient();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"looper-wsapi-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed record ClaimResponse(Guid Id, string Unit, string Path, bool Created, string Brief);
    private sealed record WorkspaceResponse(Guid Id, string Unit, string Status, string Path);

    private async Task<Guid> CreatePool(int? maxWorkspaces = null)
    {
        var config = System.Text.Json.JsonSerializer.Serialize(new
        {
            rootPath = _root, provisioning = "blank", retentionDays = 7, maxWorkspaces,
        });
        var response = await _client.PostAsJsonAsync("/api/resources", new
        {
            name = $"Pool {Guid.NewGuid():N}"[..12], type = "WorkspacePool", description = "", configJson = config,
        }, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WorkspaceResponse>(TestJson.Options))!.Id;
    }

    [Fact]
    public async Task Claim_is_idempotent_per_unit_and_reactivates_done_workspaces()
    {
        var poolId = await CreatePool();

        var first = await _client.PostAsJsonAsync("/api/workspaces",
            new { resourceId = poolId, unit = "Auth Refactor", context = "Split the module." }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var claim = await first.Content.ReadFromJsonAsync<ClaimResponse>(TestJson.Options);
        Assert.True(claim!.Created);
        Assert.Equal("auth-refactor", claim.Unit);
        Assert.True(Directory.Exists(claim.Path));
        Assert.Contains("Split the module.", await File.ReadAllTextAsync(Path.Combine(claim.Path, "WORKBRIEF.md")));

        // Same unit again: same workspace, not a new one.
        var second = await _client.PostAsJsonAsync("/api/workspaces",
            new { resourceId = poolId, unit = "auth refactor" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var reclaim = await second.Content.ReadFromJsonAsync<ClaimResponse>(TestJson.Options);
        Assert.False(reclaim!.Created);
        Assert.Equal(claim.Path, reclaim.Path);

        // Done, then re-claimed: the unit reopens rather than duplicating.
        (await _client.PostAsJsonAsync($"/api/workspaces/{claim.Id}/done", new { summary = "shipped" }, TestJson.Options))
            .EnsureSuccessStatusCode();
        var third = await _client.PostAsJsonAsync("/api/workspaces",
            new { resourceId = poolId, unit = "auth-refactor" }, TestJson.Options);
        var reopened = await third.Content.ReadFromJsonAsync<ClaimResponse>(TestJson.Options);
        Assert.False(reopened!.Created);

        var list = await _client.GetFromJsonAsync<List<WorkspaceResponse>>(
            $"/api/workspaces?resourceId={poolId}", TestJson.Options);
        var row = Assert.Single(list!);
        Assert.Equal("Active", row.Status);
    }

    [Fact]
    public async Task Clean_removes_the_directory_and_keeps_the_record_as_history()
    {
        var poolId = await CreatePool();
        var claim = await (await _client.PostAsJsonAsync("/api/workspaces",
            new { resourceId = poolId, unit = "throwaway" }, TestJson.Options))
            .Content.ReadFromJsonAsync<ClaimResponse>(TestJson.Options);

        (await _client.DeleteAsync($"/api/workspaces/{claim!.Id}")).EnsureSuccessStatusCode();

        Assert.False(Directory.Exists(claim.Path));
        var all = await _client.GetFromJsonAsync<List<WorkspaceResponse>>(
            $"/api/workspaces?resourceId={poolId}&includeCleaned=true", TestJson.Options);
        Assert.Equal("Cleaned", Assert.Single(all!).Status);
        var visible = await _client.GetFromJsonAsync<List<WorkspaceResponse>>(
            $"/api/workspaces?resourceId={poolId}", TestJson.Options);
        Assert.Empty(visible!);
    }

    [Fact]
    public async Task The_pool_cap_refuses_further_units()
    {
        var poolId = await CreatePool(maxWorkspaces: 1);
        (await _client.PostAsJsonAsync("/api/workspaces",
            new { resourceId = poolId, unit = "first" }, TestJson.Options)).EnsureSuccessStatusCode();

        var overflow = await _client.PostAsJsonAsync("/api/workspaces",
            new { resourceId = poolId, unit = "second" }, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, overflow.StatusCode);
        Assert.Contains("cap", await overflow.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Claiming_against_a_non_pool_resource_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/workspaces",
            new { resourceId = Guid.NewGuid(), unit = "x" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
