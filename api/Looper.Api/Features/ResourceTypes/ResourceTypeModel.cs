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
        new(nameof(ResourceType.FileLocation), "Folder / Files", "📁", "A folder the agent can read and edit — documents, data, a codebase. The primary one becomes its working directory.", true, null),
        new(nameof(ResourceType.Rag), "Knowledge (RAG)", "📚", "A knowledge source the agent is told to consult.", true, null),
        new(nameof(ResourceType.TestingAction), "Check", "🧪", "A command that runs after every iteration and must exit 0 for the work to count — a test suite, a validator, a link checker, anything scriptable.", true, null),
        new(nameof(ResourceType.Rule), "Rule", "📏", "Standing instructions appended to the agent's system prompt.", true, null),
        new(nameof(ResourceType.RuleSet), "Rule Set", "📋", "A managed collection of rules — add, toggle and remove without the clutter.", true, null),
        new(nameof(ResourceType.WorkspacePool), "Dynamic Workspaces", "🗂️", "Agents claim a dedicated workspace per unit of work — provisioned on demand, context passed in, cleaned up on retention.", true, null),
        new(nameof(ResourceType.UserAction), "Ask the user", "🙋", "A tool, not a question: lets the agent raise a User Action Request whenever it needs something only you can do or decide. Attach one; the agent decides what to ask.", true, null),
        new(nameof(ResourceType.SubAgent), "Sub-agent", "🤖", "A helper agent the main agent can delegate to.", true, null),
        new(nameof(ResourceType.Reviewer), "Reviewer", "🧐", "An independent agent that reviews the work after every loop — pass, or fail with fix instructions.", true, null),
        new(nameof(ResourceType.AzureConnection), "Azure Connection", "☁️", "Azure identity exposed as environment variables.", true, null),
        new(nameof(ResourceType.PatToken), "API key / secret", "🔑", "A secret injected as an environment variable — an API key, a token, a password.", true, null)
    ];

    public static ResourceTypeDto ToDto(this IResourceTypeModule module, bool builtIn = false) => new(
        module.TypeKey,
        module.DisplayName,
        module.Icon,
        module.Blurb,
        BuiltIn: builtIn,
        module.Fields.Select(f => new ResourceFieldDto(f.Key, f.Label, f.Kind, f.Required, f.Hint, f.Options, f.Placeholder)).ToList());
}
