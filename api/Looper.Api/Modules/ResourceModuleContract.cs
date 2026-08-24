using System.Text.Json;

namespace Looper.Api.Modules;

// ============================================================================
// The contract every dynamic resource-type module implements. Generated modules
// are compiled against Looper.Api.dll, so KEEP THIS SURFACE STABLE — additive
// changes only, or existing module DLLs stop loading.
// ============================================================================

/// <summary>
/// A pluggable resource type. Implementations describe the form the UI renders
/// (<see cref="Fields"/>) and translate a saved config into concrete capabilities
/// for an agent run (<see cref="Contribute"/>).
/// </summary>
public interface IResourceTypeModule
{
    /// <summary>Stable unique identifier, PascalCase, e.g. "SlackWebhook".</summary>
    string TypeKey { get; }

    string DisplayName { get; }

    /// <summary>One emoji shown in the resource catalog.</summary>
    string Icon { get; }

    /// <summary>One sentence describing what the resource gives an agent.</summary>
    string Blurb { get; }

    /// <summary>The form fields the UI renders; their values are stored as a JSON object keyed by field key.</summary>
    IReadOnlyList<ResourceField> Fields { get; }

    /// <summary>Translates a stored config into capabilities applied to a run.</summary>
    ResourceContribution Contribute(ResourceModuleContext context);
}

public enum ResourceFieldKind
{
    Text,
    Multiline,
    Number,
    Boolean,
    Password,
    Select
}

public sealed record ResourceField(
    string Key,
    string Label,
    ResourceFieldKind Kind,
    bool Required = false,
    string? Hint = null,
    string[]? Options = null,
    string? Placeholder = null);

/// <summary>Read access to the resource's stored configuration.</summary>
public sealed class ResourceModuleContext
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly JsonElement _config;

    public ResourceModuleContext(string configJson)
    {
        ConfigJson = configJson;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
            _config = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var document = JsonDocument.Parse("{}");
            _config = document.RootElement.Clone();
        }
    }

    public string ConfigJson { get; }

    public T? GetConfig<T>() where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(ConfigJson, Web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string? GetString(string key) =>
        TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public bool GetBool(string key) =>
        TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    public double? GetNumber(string key) =>
        TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private bool TryGetProperty(string key, out JsonElement value)
    {
        if (_config.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in _config.EnumerateObject())
            {
                if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

/// <summary>What a resource adds to an agent run. Empty collections are simply ignored.</summary>
public sealed class ResourceContribution
{
    /// <summary>Environment variables injected into the run process (values may be secrets — never logged).</summary>
    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.Ordinal);

    /// <summary>Standing rules appended to the agent's system prompt.</summary>
    public List<string> SystemPromptRules { get; } = [];

    /// <summary>Context sections appended to the loop prompt (e.g. knowledge-source descriptions).</summary>
    public List<string> PromptSections { get; } = [];

    /// <summary>Extra directories the agent may access.</summary>
    public List<string> AdditionalDirectories { get; } = [];

    /// <summary>MCP servers made available to the run, keyed by server name.</summary>
    public Dictionary<string, McpServerSpec> McpServers { get; } = new(StringComparer.Ordinal);
}

public sealed record McpServerSpec(
    string Transport = "stdio",
    string? Command = null,
    string[]? Args = null,
    Dictionary<string, string>? Env = null,
    string? Url = null);
