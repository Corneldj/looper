using System.Collections.Concurrent;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;
using Microsoft.Extensions.Options;

namespace Looper.Api.Features.ResourceTypes;

public enum GenerationPhase
{
    Generating,
    Compiling,
    Repairing,
    Installing,
    Installed,
    Failed,
    Cancelled
}

public sealed record GenerationJobDto(
    Guid Id,
    string Description,
    GenerationPhase Phase,
    int Attempt,
    int MaxAttempts,
    IReadOnlyList<string> LastErrors,
    decimal CostUsd,
    GeneratedResourceTypeDto? Result,
    string? Error,
    DateTime StartedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>One generation in flight (or finished): what Claude is doing, which attempt, what broke last time.</summary>
public sealed class GenerationJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public required string Description { get; init; }
    public required int MaxAttempts { get; init; }
    public GenerationPhase Phase { get; set; } = GenerationPhase.Generating;
    public int Attempt { get; set; }
    public IReadOnlyList<string> LastErrors { get; set; } = [];
    public decimal CostUsd { get; set; }
    public GeneratedResourceTypeDto? Result { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    internal CancellationTokenSource Cancellation { get; } = new();
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsTerminal => Phase is GenerationPhase.Installed or GenerationPhase.Failed or GenerationPhase.Cancelled;

    public GenerationJobDto ToDto() => new(Id, Description, Phase, Attempt, MaxAttempts, LastErrors, CostUsd, Result, Error, StartedAtUtc, UpdatedAtUtc);
}

/// <summary>
/// Runs resource-type generation as a background job: Claude writes the module, Roslyn compiles
/// it, and every compiler error is handed straight back to Claude for another attempt — up to a
/// hard cap, and only until the user cancels. Nothing retries forever, and nothing is installed
/// unless it compiled and loaded.
/// </summary>
public sealed class ResourceTypeGenerationJobs(
    IModuleSourceGenerator generator,
    ResourceModuleInstaller installer,
    IServiceScopeFactory scopeFactory,
    IOptions<LooperOptions> options,
    ILogger<ResourceTypeGenerationJobs> logger)
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<Guid, GenerationJob> _jobs = new();

    public GenerationJob Start(string description)
    {
        Sweep();
        var job = new GenerationJob
        {
            Description = description.Trim(),
            MaxAttempts = Math.Clamp(options.Value.ModuleGenerationMaxAttempts, 1, 10)
        };
        _jobs[job.Id] = job;
        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    public bool TryGet(Guid id, out GenerationJob job) => _jobs.TryGetValue(id, out job!);

    /// <summary>Stops the job: the running CLI process is killed and nothing is installed.</summary>
    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var job) || job.IsTerminal) return false;
        job.Cancellation.Cancel();
        return true;
    }

    /// <summary>Awaits the job; if the caller goes away first, the job is cancelled with it.</summary>
    public async Task<GenerationJob> WaitAsync(GenerationJob job, CancellationToken cancellationToken)
    {
        await using var registration = cancellationToken.Register(() => Cancel(job.Id));
        await job.Completion.Task;
        return job;
    }

    private async Task RunAsync(GenerationJob job)
    {
        var token = job.Cancellation.Token;
        try
        {
            IReadOnlyList<string>? errors = null;
            string? source = null;

            for (var attempt = 1; attempt <= job.MaxAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                Update(job, attempt == 1 ? GenerationPhase.Generating : GenerationPhase.Repairing, attempt);

                var generated = await generator.GenerateAsync(job.Description, errors, source, token);
                job.CostUsd += generated.CostUsd;
                if (!generated.Success)
                {
                    token.ThrowIfCancellationRequested();
                    // The model could not be reached or returned nothing — not something a retry fixes.
                    Finish(job, GenerationPhase.Failed, generated.Error ?? "Claude produced no module source.");
                    return;
                }
                source = generated.Source!;

                Update(job, GenerationPhase.Compiling, attempt);
                var compiled = installer.TryCompile(source);
                if (!compiled.Success)
                {
                    errors = compiled.Errors;
                    job.LastErrors = errors;
                    logger.LogInformation("Generated module did not compile on attempt {Attempt}/{Max} ({Count} error(s))",
                        attempt, job.MaxAttempts, errors.Count);
                    continue;
                }

                Update(job, GenerationPhase.Installing, attempt);
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<LooperDbContext>();
                try
                {
                    var (module, _) = await installer.InstallAsync(source, job.Description, db, token);
                    job.Result = new GeneratedResourceTypeDto(module.ToDto(), source, job.CostUsd);
                    Finish(job, GenerationPhase.Installed, null);
                    return;
                }
                catch (ModuleInstallException ex)
                {
                    // A TypeKey collision or a load failure is something the model can fix — hand it back.
                    errors = ex.Errors;
                    job.LastErrors = errors;
                }
            }

            Finish(job, GenerationPhase.Failed,
                $"The module still did not compile after {job.MaxAttempts} attempt(s); nothing was installed. Last errors:\n" +
                string.Join("\n", job.LastErrors));
        }
        catch (OperationCanceledException)
        {
            Finish(job, GenerationPhase.Cancelled, "Cancelled — nothing was installed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resource type generation job {JobId} crashed", job.Id);
            Finish(job, GenerationPhase.Failed, $"Generation crashed: {ex.Message}");
        }
    }

    private static void Update(GenerationJob job, GenerationPhase phase, int attempt)
    {
        job.Phase = phase;
        job.Attempt = attempt;
        job.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static void Finish(GenerationJob job, GenerationPhase phase, string? error)
    {
        job.Phase = phase;
        job.Error = error;
        job.UpdatedAtUtc = DateTime.UtcNow;
        job.Completion.TrySetResult();
    }

    private void Sweep()
    {
        var cutoff = DateTime.UtcNow - Retention;
        foreach (var (id, job) in _jobs)
        {
            if (job.IsTerminal && job.UpdatedAtUtc < cutoff) _jobs.TryRemove(id, out _);
        }
    }
}
