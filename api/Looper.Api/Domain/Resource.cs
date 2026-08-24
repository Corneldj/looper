namespace Looper.Api.Domain;

public enum ResourceType
{
    McpServer,
    FileLocation,
    Rag,
    TestingAction,
    Rule,
    SubAgent,
    AzureConnection,
    PatToken,

    /// <summary>A dynamically generated resource type; the concrete kind lives in <see cref="Resource.CustomTypeKey"/>.</summary>
    Custom
}

/// <summary>
/// A reusable capability an agent can be granted: an MCP server, a folder, a rule,
/// a post-run testing action, a sub-agent definition, a credential, etc.
/// Type-specific settings live in <see cref="ConfigJson"/>; the expected shape per type
/// is owned by the frontend forms and interpreted by the executor.
/// </summary>
public class Resource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public ResourceType Type { get; set; }

    /// <summary>For <see cref="ResourceType.Custom"/> resources: the TypeKey of the module that owns them.</summary>
    public string? CustomTypeKey { get; set; }

    public string Description { get; set; } = "";
    public string ConfigJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<LoopAgent> Agents { get; set; } = [];
}
