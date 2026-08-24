using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Looper.Api.Modules;

public sealed record ModuleCompilationResult(
    bool Success,
    byte[]? Assembly,
    IReadOnlyList<string> Errors,
    string? TypeKey);

/// <summary>
/// Compiles resource-module C# source in-process with Roslyn against the running
/// framework and Looper.Api itself (which carries the module contract).
/// </summary>
public sealed class ResourceModuleCompiler
{
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(BuildReferences);

    /// <summary>Module sources get the same implicit usings a normal .NET project has.</summary>
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    public ModuleCompilationResult Compile(string source, string assemblyName)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions);
        var usingsTree = CSharpSyntaxTree.ParseText(GlobalUsings, parseOptions);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree, usingsTree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithNullableContextOptions(NullableContextOptions.Enable)
                .WithOptimizationLevel(OptimizationLevel.Release));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);

        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .Take(20)
                .ToList();
            return new ModuleCompilationResult(false, null, errors, null);
        }

        // Validate the contract without leaking the probe assembly into the app: load it
        // in a collectible context, find exactly one module implementation, read its key.
        var bytes = stream.ToArray();
        var probeContext = new System.Runtime.Loader.AssemblyLoadContext($"probe-{assemblyName}", isCollectible: true);
        try
        {
            using var probeStream = new MemoryStream(bytes);
            var assembly = probeContext.LoadFromStream(probeStream);
            var moduleTypes = assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IResourceTypeModule).IsAssignableFrom(t))
                .ToList();

            if (moduleTypes.Count != 1)
            {
                return new ModuleCompilationResult(false, null,
                    [$"The source must contain exactly one public class implementing IResourceTypeModule (found {moduleTypes.Count})."], null);
            }

            var module = (IResourceTypeModule)Activator.CreateInstance(moduleTypes[0])!;
            if (string.IsNullOrWhiteSpace(module.TypeKey) || !module.TypeKey.All(char.IsLetterOrDigit))
            {
                return new ModuleCompilationResult(false, null,
                    [$"TypeKey '{module.TypeKey}' is invalid — use letters and digits only (e.g. \"SlackWebhook\")."], null);
            }

            // Exercise the descriptive surface so obviously broken modules fail at creation, not at run time.
            _ = module.DisplayName;
            _ = module.Fields;

            return new ModuleCompilationResult(true, bytes, [], module.TypeKey);
        }
        catch (Exception ex)
        {
            return new ModuleCompilationResult(false, null, [$"Module failed to load: {ex.GetBaseException().Message}"], null);
        }
        finally
        {
            probeContext.Unload();
        }
    }

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var references = new List<MetadataReference>();

        // Everything the running app was compiled against, incl. the BCL and ASP.NET Core.
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trusted)
        {
            foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // The module contract lives in Looper.Api itself.
        references.Add(MetadataReference.CreateFromFile(typeof(IResourceTypeModule).Assembly.Location));
        return references;
    }
}
