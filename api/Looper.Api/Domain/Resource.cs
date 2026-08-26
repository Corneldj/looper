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

    /// <summary>
    /// An independent model-based review gate: after the worker succeeds (and testing actions pass),
    /// a fresh-context reviewer judges the work against a rubric, and failures loop fix instructions
    /// back to the worker. Deliberately NOT a sub-agent — the worker must not control its own gate.
    /// </summary>
    Reviewer,

    /// <summary>A managed collection of rules with per-rule toggles; enabled rules join the system prompt.</summary>
    RuleSet,

    /// <summary>
    /// A pool of dynamic workspaces: agents claim a dedicated directory per unit of work
    /// (blank, git clone, or template copy), seeded with a context brief and cleaned up
    /// automatically after the retention window.
    /// </summary>
    WorkspacePool,

    /// <summary>
    /// Lets the agent raise a User Action Request: something only the human can do or decide.
    /// Raising one is not a failure — the run succeeds and the schedule parks until resolved.
    /// </summary>
    UserAction,

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
