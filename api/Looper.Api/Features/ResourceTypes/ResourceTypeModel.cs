using Looper.Api.Domain;
using Looper.Api.Modules;

namespace Looper.Api.Features.ResourceTypes;

public sealed record ResourceFieldDto(
    string Key,
    string Label,
    ResourceFieldKind Kind,
    bool Required,
    string? Hint,
    string[]? Options,
    string? Placeholder);

public sealed record ResourceTypeDto(
    string TypeKey,
    string Label,
    string Icon,
    string Blurb,
    bool BuiltIn,
    IReadOnlyList<ResourceFieldDto>? Fields);

public static class ResourceTypeCatalog
{
    /// <summary>
    /// The built-in types with their bespoke frontend forms (Fields = null). Labels/icons
    /// mirror the frontend catalog so both sides describe types identically.
    /// </summary>
    public static readonly IReadOnlyList<ResourceTypeDto> BuiltIns =
    [
        new(nameof(ResourceType.McpServer), "MCP Server", "⚡", "Tools exposed to the agent over the Model Context Protocol.", true, null),
        new(nameof(ResourceType.FileLocation), "Folder / Files", "📁", "A directory the agent can read and edit. The primary one becomes its working directory.", true, null),
        new(nameof(ResourceType.Rag), "Knowledge (RAG)", "📚", "A knowledge source the agent is told to consult.", true, null),
        new(nameof(ResourceType.TestingAction), "Testing Action", "🧪", "A command that runs after every loop and gates the result.", true, null),
        new(nameof(ResourceType.Rule), "Rule", "📏", "Standing instructions appended to the agent's system prompt.", true, null),
        new(nameof(ResourceType.SubAgent), "Sub-agent", "🤖", "A helper agent the main agent can delegate to.", true, null),
        new(nameof(ResourceType.AzureConnection), "Azure Connection", "☁️", "Azure identity exposed as environment variables.", true, null),
        new(nameof(ResourceType.PatToken), "PAT Token", "🔑", "A personal access token injected as an environment variable.", true, null)
    ];

    public static ResourceTypeDto ToDto(this IResourceTypeModule module, bool builtIn = false) => new(
        module.TypeKey,
        module.DisplayName,
        module.Icon,
        module.Blurb,
        BuiltIn: builtIn,
        module.Fields.Select(f => new ResourceFieldDto(f.Key, f.Label, f.Kind, f.Required, f.Hint, f.Options, f.Placeholder)).ToList());
}
