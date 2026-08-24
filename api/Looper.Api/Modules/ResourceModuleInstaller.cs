using Looper.Api.Domain;
using Looper.Api.Infrastructure;

namespace Looper.Api.Modules;

public sealed class ModuleInstallException(IReadOnlyList<string> errors)
    : Exception(string.Join("\n", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>Takes module source through compile → uniqueness check → DLL on disk → registry → database.</summary>
public sealed class ResourceModuleInstaller(
    ResourceModuleCompiler compiler,
    ResourceModuleRegistry registry,
    ILogger<ResourceModuleInstaller> logger)
{
    private static readonly HashSet<string> BuiltInKeys = new(Enum.GetNames<ResourceType>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Compiles only — used by the generate flow to feed errors back to Claude for a retry.</summary>
    public ModuleCompilationResult TryCompile(string source) =>
        compiler.Compile(source, $"Looper.Module.{Guid.NewGuid():N}");

    public async Task<(IResourceTypeModule Module, ResourceModuleRecord Record)> InstallAsync(
        string source, string? generationPrompt, LooperDbContext db, CancellationToken cancellationToken)
    {
        var compiled = TryCompile(source);
        if (!compiled.Success)
        {
            throw new ModuleInstallException(compiled.Errors);
        }

        var typeKey = compiled.TypeKey!;
        if (BuiltInKeys.Contains(typeKey))
        {
            throw new ModuleInstallException([$"'{typeKey}' collides with a built-in resource type — pick another TypeKey."]);
        }
        if (registry.TryGet(typeKey, out _))
        {
            throw new ModuleInstallException([$"A resource type named '{typeKey}' already exists. Delete it first or pick another TypeKey."]);
        }

        Directory.CreateDirectory(registry.ModulesDirectory);
        var dllFileName = $"{typeKey}.dll";
        var dllPath = Path.Combine(registry.ModulesDirectory, dllFileName);
        await File.WriteAllBytesAsync(dllPath, compiled.Assembly!, cancellationToken);

        IResourceTypeModule module;
        try
        {
            module = registry.LoadFromFile(dllPath);
        }
        catch (Exception ex)
        {
            File.Delete(dllPath);
            throw new ModuleInstallException([$"Compiled module failed to load: {ex.GetBaseException().Message}"]);
        }

        var record = new ResourceModuleRecord
        {
            TypeKey = module.TypeKey,
            DisplayName = module.DisplayName,
            Icon = module.Icon,
            Blurb = module.Blurb,
            SourceCode = source,
            DllFileName = dllFileName,
            GenerationPrompt = generationPrompt
        };
        db.ResourceModules.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Installed dynamic resource type {TypeKey} ({DisplayName})", module.TypeKey, module.DisplayName);
        return (module, record);
    }
}
