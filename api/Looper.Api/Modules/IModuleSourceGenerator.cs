namespace Looper.Api.Modules;

public sealed record ModuleGenerationResult(bool Success, string? Source, string? Error, decimal CostUsd);

/// <summary>
/// Writes (or repairs) a resource-type module for a description. The generation job feeds
/// compiler errors and the previous source back in on every retry; an interface so the job
/// loop can be tested with a scripted generator instead of a live model.
/// </summary>
public interface IModuleSourceGenerator
{
    Task<ModuleGenerationResult> GenerateAsync(string description, IReadOnlyList<string>? previousErrors,
        string? previousSource, CancellationToken cancellationToken);
}
