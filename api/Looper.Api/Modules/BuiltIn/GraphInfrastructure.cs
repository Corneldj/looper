using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// The role an agent plays towards a shared graph. Decoupling memory from execution
/// (the GraphRAG separation of concerns): executing loops consume — they query and drop
/// contributions in the inbox — while exactly one curator loop owns canonical writes and
/// maintenance. A graph with no curator is private: the attached agent maintains it itself.
/// </summary>
public enum GraphRole
{
    Private,
    Consumer,
    Curator
}

/// <summary>Shared-layer settings every memory-shaped graph resource carries in its config.</summary>
public sealed record GraphSharedConfig(
    string? Curator = null,
    double? PreambleK = null,
    bool AutoLog = false,
    double? InboxThreshold = null)
{
    public const int DefaultPreambleK = 6;
    public const int DefaultInboxThreshold = 5;

    public int EffectivePreambleK => PreambleK is { } k ? Math.Max(0, (int)k) : DefaultPreambleK;
    public int EffectiveInboxThreshold => InboxThreshold is { } t and >= 1 ? (int)t : DefaultInboxThreshold;
    public bool HasCurator => !string.IsNullOrWhiteSpace(Curator);
}

/// <summary>
/// Everything Looper knows about graph resources as *infrastructure*: which resource types
/// are memory-shaped, how roles derive from the curator setting, the deterministic curation
/// event topic, and the harness-side inbox writer (run outcomes logged without agent tokens).
/// </summary>
public static class GraphInfrastructure
{
    public const string VectorTypeKey = "ContinuousVectorMemoryGraph";
    public const string KnowledgeTypeKey = "KnowledgeGraph";
    public const string MemoryTypeKey = "MemoryGraph";
    public const string ExecutionTypeKey = "ExecutionGraph";

    /// <summary>Graph types that hold memory (facts/episodes) and join the shared-memory layer.
    /// Execution graphs are plans, not memory — they stay out of preambles and curation.</summary>
    public static readonly IReadOnlyList<string> MemoryTypeKeys = [VectorTypeKey, KnowledgeTypeKey, MemoryTypeKey];

    public static readonly IReadOnlyList<string> AllTypeKeys = [VectorTypeKey, KnowledgeTypeKey, MemoryTypeKey, ExecutionTypeKey];

    public const string HealthFileName = "health.json";

    /// <summary>The extra form fields every memory-shaped graph module exposes.</summary>
    public static IEnumerable<ResourceField> SharedFields()
    {
        yield return new ResourceField("curator", "Curator agent", ResourceFieldKind.Text,
            Hint: "Exact name of the ONE loop that owns canonical writes and maintenance. Set it to run this graph " +
                  "as shared infrastructure: every other attached agent becomes a query-only consumer contributing " +
                  "via the inbox. Leave empty for a private graph the agent maintains itself.");
        yield return new ResourceField("preambleK", "Context preamble size", ResourceFieldKind.Number,
            Hint: "Facts the harness recalls and injects before each run — retrieval without spending agent turns. " +
                  "0 disables.", Placeholder: GraphSharedConfig.DefaultPreambleK.ToString());
        yield return new ResourceField("autoLog", "Log run outcomes", ResourceFieldKind.Boolean,
            Hint: "After every real run of an attached agent, the harness drops the outcome into this graph's inbox.");
        yield return new ResourceField("inboxThreshold", "Curation threshold", ResourceFieldKind.Number,
            Hint: "Pending inbox items that raise the needs-curation event.",
            Placeholder: GraphSharedConfig.DefaultInboxThreshold.ToString());
    }

    public static GraphSharedConfig SharedConfig(ResourceModuleContext context) => new(
        Curator: context.GetString("curator"),
        PreambleK: context.GetNumber("preambleK"),
        AutoLog: context.GetBool("autoLog"),
        InboxThreshold: context.GetNumber("inboxThreshold"));

    /// <summary>Role derivation is deterministic: name match against the configured curator.</summary>
    public static GraphRole RoleOf(GraphSharedConfig config, string agentName)
    {
        if (!config.HasCurator) return GraphRole.Private;
        return string.Equals(config.Curator!.Trim(), agentName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? GraphRole.Curator
            : GraphRole.Consumer;
    }

    /// <summary>graph.&lt;resource-slug&gt;.needs-curation — what a curator loop listens for.</summary>
    public static string CurationTopic(string resourceName) =>
        $"graph.{EventDispatcher.Slug(resourceName, "graph")}.needs-curation";

    public static bool IsMemoryGraph(Resource resource) =>
        resource.Type == ResourceType.Custom && resource.CustomTypeKey is { } key &&
        MemoryTypeKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    public static bool IsGraph(Resource resource) =>
        resource.Type == ResourceType.Custom && resource.CustomTypeKey is { } key &&
        AllTypeKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Memory graphs of this run whose config opts into harness-side outcome logging.</summary>
    public static IEnumerable<(Resource Resource, string Path, GraphSharedConfig Config)> AutoLogTargets(
        IEnumerable<Resource> resources)
    {
        foreach (var resource in resources.Where(IsMemoryGraph))
        {
            var context = new ResourceModuleContext(resource.ConfigJson);
            var config = SharedConfig(context);
            var path = context.GetString("path");
            if (config.AutoLog && !string.IsNullOrWhiteSpace(path))
            {
                yield return (resource, path, config);
            }
        }
    }

    /// <summary>
    /// Drops one item into a graph's inbox exactly the way `loopergraph.py remember` does:
    /// one file per contribution, so parallel writers never collide and no lock is needed.
    /// </summary>
    public static string WriteInboxItem(string graphPath, string kind, string text, string? agent, string? run)
    {
        var inbox = Path.Combine(graphPath, "inbox");
        Directory.CreateDirectory(inbox);
        var id = $"in-{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var item = new
        {
            id,
            kind,
            text,
            agent,
            run,
            created = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
        };
        var tmp = Path.Combine(inbox, $"{id}.json.tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Move(tmp, Path.Combine(inbox, $"{id}.json"), overwrite: false);
        return id;
    }

    public static int PendingInboxCount(string graphPath)
    {
        var inbox = Path.Combine(graphPath, "inbox");
        if (!Directory.Exists(inbox)) return 0;
        return Directory.EnumerateFiles(inbox, "*.json").Count();
    }
}
