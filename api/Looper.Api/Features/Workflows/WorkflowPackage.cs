using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Looper.Api.Domain;
using Looper.Api.Modules;

namespace Looper.Api.Features.Workflows;

// ============================================================================
// A workflow package is one JSON document another Looper instance can import:
// the workflow, its resources (secrets stripped), its agents and their wiring,
// and every dynamic resource type they depend on — source AND compiled DLL, so
// the receiving instance can rebuild the type or load it as-is.
// Refs ("r1", "a2") replace database ids; nothing in a package is machine-specific
// except paths, which the import reports rather than guesses at.
// ============================================================================

public sealed record WorkflowPackage(
    string Format,
    int Version,
    DateTime ExportedAtUtc,
    string? ExportedFrom,
    PackagedWorkflow Workflow,
    List<PackagedResourceType> ResourceTypes,
    List<PackagedResource> Resources,
    List<PackagedAgent> Agents,
    List<RedactedSecret> RedactedSecrets)
{
    public const string FormatName = "looper-workflow";
    public const int CurrentVersion = 1;

    /// <summary>Enums as names, indented, so a package reads as a document and survives hand edits.</summary>
    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string FileNameFor(string workflowName)
    {
        var slug = new string(workflowName.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return $"{(slug.Length == 0 ? "workflow" : slug)}.looper-workflow.json";
    }
}

public sealed record PackagedWorkflow(string Name, string Description);

/// <summary>A dynamic resource type the package depends on. Built-in types are never packaged — every instance has them.</summary>
public sealed record PackagedResourceType(
    string TypeKey,
    string DisplayName,
    string Icon,
    string Blurb,
    string SourceCode,
    /// <summary>The compiled module, base64. Used when the source does not compile on the importing instance.</summary>
    string? DllBase64,
    string? GenerationPrompt);

public sealed record PackagedResource(
    string Ref,
    string Name,
    ResourceType Type,
    string? CustomTypeKey,
    string Description,
    string ConfigJson);

public sealed record PackagedAgent(
    string Ref,
    string Name,
    string Description,
    string Prompt,
    string Model,
    EffortLevel Effort,
    int IntervalMinutes,
    TriggerMode TriggerMode,
    string? TriggerTopics,
    int MaxTurns,
    decimal? MaxBudgetUsd,
    string? WorkingDirectory,
    string? AllowedTools,
    bool BypassPermissions,
    bool DryRun,
    int AutonomyLevel,
    List<string> ResourceRefs);

/// <summary>A secret that was stripped on export; the importing user has to fill it in.</summary>
public sealed record RedactedSecret(string ResourceRef, string ResourceName, string Field);

/// <summary>
/// A Check may run a Script resource by id. Ids do not survive an export, refs do: on the way out
/// <c>scriptResourceId</c> becomes <c>scriptResourceRef</c>, on the way in it becomes the new id.
/// </summary>
public static class CheckScriptReference
{
    public const string IdKey = "scriptResourceId";
    public const string RefKey = "scriptResourceRef";

    public static string ToRef(Resource resource, string configJson, IReadOnlyDictionary<Guid, string> refs)
    {
        if (resource.Type != ResourceType.TestingAction) return configJson;
        if (JsonNode.Parse(configJson) is not JsonObject config || config[IdKey] is null) return configJson;
        if (!Guid.TryParse(config[IdKey]!.ToString(), out var scriptId) || !refs.TryGetValue(scriptId, out var scriptRef))
        {
            throw new FluentValidation.ValidationException(
                $"Check '{resource.Name}' runs a script that is not in this workflow, so it cannot be packaged. Point it at a script of this workflow first.");
        }
        config.Remove(IdKey);
        config[RefKey] = scriptRef;
        return config.ToJsonString();
    }

    public static string? RefOf(PackagedResource resource)
    {
        if (resource.Type != ResourceType.TestingAction || string.IsNullOrWhiteSpace(resource.ConfigJson)) return null;
        try
        {
            return JsonNode.Parse(resource.ConfigJson) is JsonObject config && config[RefKey] is JsonValue value ? value.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ToId(string configJson, Guid scriptId)
    {
        if (JsonNode.Parse(configJson) is not JsonObject config) return configJson;
        config.Remove(RefKey);
        config[IdKey] = scriptId.ToString();
        return config.ToJsonString();
    }
}

/// <summary>What is machine-specific about a package: paths. Reported on import, never rewritten.</summary>
public static class WorkflowPortability
{
    private static readonly string[] PathKeys = ["path", "rootPath", "workingDirectory", "directory", "folder", "templatePath"];

    /// <summary>Path-valued config fields of a resource that do not exist on this machine.</summary>
    public static IEnumerable<string> MissingPaths(PackagedResource resource, ResourceModuleRegistry registry)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(resource.ConfigJson); }
        catch (JsonException) { yield break; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) yield break;

            var pathKeys = new HashSet<string>(PathKeys, StringComparer.OrdinalIgnoreCase);
            if (resource.Type == ResourceType.Custom && resource.CustomTypeKey is not null && registry.TryGet(resource.CustomTypeKey, out var module))
            {
                foreach (var field in module.Fields.Where(f => f.Kind == ResourceFieldKind.Path)) pathKeys.Add(field.Key);
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!pathKeys.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String) continue;
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value)) continue;
                if (value.Contains("://", StringComparison.Ordinal)) continue; // a URL, not a path
                if (!Directory.Exists(value) && !File.Exists(value)) yield return value;
            }
        }
    }
}
