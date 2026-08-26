using System.Collections.Concurrent;
using System.Reflection;

namespace Looper.Api.Modules;

/// <summary>
/// Holds the live dynamic resource-type modules. DLLs live in the modules directory
/// and are loaded into the default context at startup (and when created at runtime).
/// Removal takes a module out of the registry; its assembly stays in memory until restart.
/// </summary>
public sealed class ResourceModuleRegistry(ILogger<ResourceModuleRegistry> logger)
{
    private readonly ConcurrentDictionary<string, IResourceTypeModule> _modules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _builtInKeys = new(StringComparer.OrdinalIgnoreCase);

    public string ModulesDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "modules");

    public IReadOnlyCollection<IResourceTypeModule> All =>
        _modules.Values.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public bool TryGet(string typeKey, out IResourceTypeModule module) =>
        _modules.TryGetValue(typeKey, out module!);

    /// <summary>True for modules compiled into the app (shipped types) — they cannot be removed.</summary>
    public bool IsBuiltIn(string typeKey) => _builtInKeys.Contains(typeKey);

    /// <summary>Registers a module compiled into the app itself. Called once at startup, before reconcile.</summary>
    public void RegisterBuiltIn(IResourceTypeModule module)
    {
        _modules[module.TypeKey] = module;
        _builtInKeys.Add(module.TypeKey);
    }

    /// <summary>
    /// Startup reconciliation: loads the module for every database record — recompiling from
    /// the stored source when its DLL is missing — and deletes stray DLLs with no record.
    /// </summary>
    public async Task ReconcileAsync(
        IReadOnlyList<Domain.ResourceModuleRecord> records, ResourceModuleCompiler compiler)
    {
        Directory.CreateDirectory(ModulesDirectory);

        foreach (var record in records)
        {
            var dllPath = Path.Combine(ModulesDirectory, record.DllFileName);
            try
            {
                if (!File.Exists(dllPath))
                {
                    logger.LogInformation("Module DLL for {TypeKey} is missing; recompiling from stored source", record.TypeKey);
                    var compiled = compiler.Compile(record.SourceCode, $"Looper.Module.{Guid.NewGuid():N}");
                    if (!compiled.Success)
                    {
                        logger.LogError("Stored source for {TypeKey} no longer compiles: {Errors}",
                            record.TypeKey, string.Join("; ", compiled.Errors));
                        continue;
                    }
                    await File.WriteAllBytesAsync(dllPath, compiled.Assembly!);
                }

                LoadFromFile(dllPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to load resource module {TypeKey}; skipping", record.TypeKey);
            }
        }

        var known = records.Select(r => r.DllFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stray in Directory.EnumerateFiles(ModulesDirectory, "*.dll")
                     .Where(dll => !known.Contains(Path.GetFileName(dll))))
        {
            try
            {
                File.Delete(stray);
            }
            catch (IOException)
            {
                // Possibly still mapped from a previous delete this session; retried next startup.
            }
        }

        if (!_modules.IsEmpty)
        {
            logger.LogInformation("Loaded {Count} dynamic resource type(s): {Keys}",
                _modules.Count, string.Join(", ", _modules.Keys));
        }
    }

    /// <summary>Loads one module DLL and registers it. Throws when nothing usable is inside.</summary>
    public IResourceTypeModule LoadFromFile(string dllPath)
    {
        var assembly = Assembly.LoadFrom(dllPath);
        var moduleType = assembly.GetTypes()
            .FirstOrDefault(t => t is { IsAbstract: false, IsInterface: false } && typeof(IResourceTypeModule).IsAssignableFrom(t))
            ?? throw new InvalidOperationException($"{Path.GetFileName(dllPath)} contains no IResourceTypeModule implementation.");

        var module = (IResourceTypeModule)Activator.CreateInstance(moduleType)!;
        if (_builtInKeys.Contains(module.TypeKey))
        {
            throw new InvalidOperationException($"'{module.TypeKey}' is a built-in resource type and cannot be replaced by a DLL.");
        }
        _modules[module.TypeKey] = module;
        return module;
    }

    public void Remove(string typeKey) => _modules.TryRemove(typeKey, out _);

    /// <summary>Registers an already-instantiated module. Test hook.</summary>
    internal void Register(IResourceTypeModule module) => _modules[module.TypeKey] = module;
}
