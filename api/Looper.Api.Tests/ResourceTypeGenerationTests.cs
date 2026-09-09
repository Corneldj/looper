using System.Net;
using System.Net.Http.Json;
using Looper.Api.Features.ResourceTypes;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Looper.Api.Tests;

// ============================================================================
// Generation as a job: compiler errors go back to Claude for another attempt,
// up to a hard cap; the user can cancel at any time; nothing half-done is installed.
// ============================================================================

/// <summary>A generator that follows a script: each call returns the next source (or blocks until cancelled).</summary>
public sealed class ScriptedGenerator(params Func<IReadOnlyList<string>?, string?>[] steps) : IModuleSourceGenerator
{
    private int _calls;
    public List<IReadOnlyList<string>?> ErrorsSeen { get; } = [];
    public int Calls => _calls;

    public async Task<ModuleGenerationResult> GenerateAsync(string description, IReadOnlyList<string>? previousErrors,
        string? previousSource, CancellationToken cancellationToken)
    {
        ErrorsSeen.Add(previousErrors);
        var index = Interlocked.Increment(ref _calls) - 1;
        if (index >= steps.Length)
        {
            // Past the script: behave like a model that hangs until told to stop.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        var source = steps[index](previousErrors);
        return source is null
            ? new ModuleGenerationResult(false, null, "the model was unreachable", 0)
            : new ModuleGenerationResult(true, source, null, 0.05m);
    }
}

public sealed class GenerationJobTests : IDisposable
{
    private readonly string _dir;
    private readonly ServiceProvider _services;

    public GenerationJobTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"looper-gen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<LooperDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(_dir, "gen.db")}"));
        services.AddSingleton<ResourceModuleCompiler>();
        services.AddSingleton<ResourceModuleRegistry>();
        services.AddSingleton<ResourceModuleInstaller>();
        _services = services.BuildServiceProvider();
        using var scope = _services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LooperDbContext>().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ResourceTypeGenerationJobs Jobs(IModuleSourceGenerator generator, int maxAttempts = 4) => new(
        generator,
        _services.GetRequiredService<ResourceModuleInstaller>(),
        _services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new LooperOptions { ModuleGenerationMaxAttempts = maxAttempts }),
        NullLogger<ResourceTypeGenerationJobs>.Instance);

    private static string Module(string typeKey) =>
        ResourceModuleCompilerTests.SampleModuleSource.Replace("\"SlackWebhook\"", $"\"{typeKey}\"").Replace("SlackWebhookModule", typeKey + "Module");

    [Fact]
    public async Task Compiler_errors_are_fed_back_until_the_module_compiles_then_it_is_installed()
    {
        var generator = new ScriptedGenerator(
            _ => "public class Broken {",                      // attempt 1: syntax error
            errors => errors is { Count: > 0 } ? Module("RepairedOnce") : "still wrong");  // attempt 2: fixed, given the errors
        var jobs = Jobs(generator);

        var job = await jobs.WaitAsync(jobs.Start("a webhook that posts somewhere"), CancellationToken.None);

        Assert.Equal(GenerationPhase.Installed, job.Phase);
        Assert.Equal(2, job.Attempt);
        Assert.Equal("RepairedOnce", job.Result!.Type.TypeKey);
        Assert.Equal(0.10m, job.CostUsd);                   // both attempts are paid for and reported
        Assert.Null(generator.ErrorsSeen[0]);
        Assert.NotEmpty(generator.ErrorsSeen[1]!);           // the second call saw the first attempt's errors
        Assert.True(_services.GetRequiredService<ResourceModuleRegistry>().TryGet("RepairedOnce", out _));
    }

    [Fact]
    public async Task The_attempt_cap_stops_a_module_that_never_compiles_and_installs_nothing()
    {
        var generator = new ScriptedGenerator(_ => "nope {", _ => "nope {", _ => "nope {");
        var jobs = Jobs(generator, maxAttempts: 3);

        var job = await jobs.WaitAsync(jobs.Start("something that never compiles"), CancellationToken.None);

        Assert.Equal(GenerationPhase.Failed, job.Phase);
        Assert.Equal(3, generator.Calls);
        Assert.Contains("after 3 attempt(s)", job.Error);
        Assert.NotEmpty(job.LastErrors);
        Assert.Null(job.Result);
    }

    [Fact]
    public async Task Cancelling_kills_the_attempt_in_flight_and_installs_nothing()
    {
        var generator = new ScriptedGenerator(_ => "nope {");   // attempt 2 hangs until cancelled
        var jobs = Jobs(generator);
        var job = jobs.Start("a module the user gives up on");

        // Wait until the second attempt is in flight, then cancel.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (job.Phase != GenerationPhase.Repairing && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(GenerationPhase.Repairing, job.Phase);
        Assert.True(jobs.Cancel(job.Id));

        await jobs.WaitAsync(job, CancellationToken.None);
        Assert.Equal(GenerationPhase.Cancelled, job.Phase);
        Assert.Contains("nothing was installed", job.Error);
        Assert.False(jobs.Cancel(job.Id));                   // already over
    }

    [Fact]
    public async Task A_type_key_collision_goes_back_to_the_model_like_a_compiler_error()
    {
        var setup = Jobs(new ScriptedGenerator(_ => Module("Taken")));
        Assert.Equal(GenerationPhase.Installed, (await setup.WaitAsync(setup.Start("first"), CancellationToken.None)).Phase);
        var generator = new ScriptedGenerator(_ => Module("Taken"), errors => Module("Fresh"));
        var jobs = Jobs(generator);

        var job = await jobs.WaitAsync(jobs.Start("second"), CancellationToken.None);

        Assert.Equal(GenerationPhase.Installed, job.Phase);
        Assert.Equal("Fresh", job.Result!.Type.TypeKey);
        Assert.Contains(generator.ErrorsSeen[1]!, e => e.Contains("already exists"));
    }

    [Fact]
    public async Task A_generator_that_cannot_be_reached_fails_at_once_without_retrying()
    {
        var generator = new ScriptedGenerator(_ => null, _ => Module("Never"));
        var jobs = Jobs(generator);
        var job = await jobs.WaitAsync(jobs.Start("unreachable"), CancellationToken.None);

        Assert.Equal(GenerationPhase.Failed, job.Phase);
        Assert.Equal(1, generator.Calls);
        Assert.Contains("unreachable", job.Error);
    }
}

/// <summary>Boots the API with a scripted generator so the job endpoints can be driven end to end.</summary>
public sealed class ScriptedGeneratorApiFactory : LooperApiFactory
{
    public ScriptedGenerator Generator { get; } = new(
        _ => "public class Broken {",
        errors => ResourceModuleCompilerTests.SampleModuleSource.Replace("\"SlackWebhook\"", "\"ApiRepaired\"").Replace("SlackWebhookModule", "ApiRepairedModule"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services => services.AddSingleton<IModuleSourceGenerator>(Generator));
    }
}

public class GenerationJobApiTests(ScriptedGeneratorApiFactory factory) : IClassFixture<ScriptedGeneratorApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    private sealed record JobResponse(Guid Id, string Phase, int Attempt, int MaxAttempts, List<string> LastErrors, decimal CostUsd, ResultResponse? Result, string? Error);
    private sealed record ResultResponse(TypeResponse Type, string SourceCode, decimal CostUsd);
    private sealed record TypeResponse(string TypeKey, string Label);

    [Fact]
    public async Task A_job_is_started_polled_to_installation_and_the_type_appears_in_the_catalog()
    {
        var start = await _client.PostAsJsonAsync("/api/resource-types/generate/jobs", new { description = "a webhook that posts somewhere useful" }, TestJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var job = await start.Content.ReadFromJsonAsync<JobResponse>(TestJson.Options);
        Assert.Equal(4, job!.MaxAttempts);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (job!.Phase is not ("Installed" or "Failed" or "Cancelled") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            job = await _client.GetFromJsonAsync<JobResponse>($"/api/resource-types/generate/jobs/{job.Id}", TestJson.Options);
        }

        Assert.Equal("Installed", job!.Phase);
        Assert.Equal(2, job.Attempt);
        Assert.Equal("ApiRepaired", job.Result!.Type.TypeKey);
        var types = await _client.GetFromJsonAsync<List<Dictionary<string, System.Text.Json.JsonElement>>>("/api/resource-types", TestJson.Options);
        Assert.Contains(types!, t => t["typeKey"].GetString() == "ApiRepaired");

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/resource-types/generate/jobs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/resource-types/generate/jobs", new { description = "short" }, TestJson.Options)).StatusCode);

        await _client.DeleteAsync("/api/resource-types/ApiRepaired");
    }
}
