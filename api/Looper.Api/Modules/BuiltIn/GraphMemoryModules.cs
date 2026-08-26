namespace Looper.Api.Modules.BuiltIn;

// ============================================================================
// Shipped graph-shaped resource types. They ride the same IResourceTypeModule
// pipeline as AI-generated modules, but are compiled into the app and
// registered as built-ins at startup.
//
// Looper loops are reset loops: every iteration is a fresh context, so
// everything an iteration needs must exist on disk (Module 8, Lesson 2 of the
// context-engineering course). PrepareRun therefore seeds each storage folder
// with the loopergraph toolkit (typed edges from a controlled ontology,
// bi-temporal invalidate-never-delete, vector-entry recall, validated
// execution graph — Module 9), a starter ontology, and a protocol README.
// The prompt sections stay short and point at the tool: the read path is
// commands, not a raw folder.
// ============================================================================

public sealed class ContinuousVectorMemoryGraphModule : IResourceTypeModule
{
    public string TypeKey => "ContinuousVectorMemoryGraph";
    public string DisplayName => "Continuous Vector Memory Graph";
    public string Icon => "🧠";
    public string Blurb => "Semantic memory the agent searches and grows on every iteration.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("path", "Storage folder", ResourceFieldKind.Path, Required: true,
            Hint: "Where the memory graph lives. The agent gets read/write access.",
            Placeholder: "/home/you/looper-memory/vectors"),
        new("collection", "Collection", ResourceFieldKind.Text,
            Hint: "Optional namespace when several agents share the store.", Placeholder: "default"),
        new("mcpUrl", "Vector store MCP URL", ResourceFieldKind.Text,
            Hint: "Optional http/sse MCP endpoint of a real vector database. Without it the agent uses the seeded file-based toolkit (lexical entry, honest about it).",
            Placeholder: "http://localhost:8123/mcp")
    ];

    private static readonly Dictionary<string, string> Ontology = new()
    {
        ["prefers"] = "Subject prefers or has chosen the object",
        ["uses"] = "Subject uses or depends on the object day-to-day",
        ["works_on"] = "Subject is actively working on the object",
        ["decided"] = "Subject made the decision named by the object",
        ["observed"] = "Subject was seen in the state named by the object",
        ["caused_by"] = "Subject happened because of the object",
        ["supersedes"] = "Subject replaces the object",
        ["mentioned_in"] = "Subject is discussed in the object (doc, ticket, episode)"
    };

    public void PrepareRun(ResourceModuleContext context)
    {
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return;
        GraphWorkspace.Ensure(path, Ontology, GraphReadmes.VectorMemory);
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return contribution;

        var collection = context.GetString("collection");
        var mcpUrl = context.GetString("mcpUrl");

        contribution.EnvironmentVariables["LOOPER_VECTOR_MEMORY_PATH"] = path;
        if (!string.IsNullOrWhiteSpace(collection))
        {
            contribution.EnvironmentVariables["LOOPER_VECTOR_MEMORY_COLLECTION"] = collection;
        }
        contribution.AdditionalDirectories.Add(path);

        contribution.PromptSections.Add(
            $"CONTINUOUS VECTOR MEMORY GRAPH at {path} (also $LOOPER_VECTOR_MEMORY_PATH). Your durable semantic memory " +
            "across iterations; its README.md defines the protocol and command crib. Start every iteration with: " +
            $"python3 \"{path}/loopergraph.py\" --dir \"{path}\" recall \"<what this iteration is about>\" " +
            "and consult what comes back before acting. Record raw events with the `episode` command, then extract " +
            "durable facts with `add <edge_type> <subject> <object> --source <episode-id>` using ONLY edge types " +
            "from ontology.json. When a fact changes, use `supersede` — never edit or delete history; ask about the " +
            "past with `recall --at YYYY-MM-DD`. Never store secrets in memory." +
            (string.IsNullOrWhiteSpace(mcpUrl)
                ? ""
                : " A real vector store is also connected via MCP — prefer its tools for semantic search and storage, and keep the folder's episode log as the audit trail."));

        if (!string.IsNullOrWhiteSpace(mcpUrl))
        {
            var serverName = string.IsNullOrWhiteSpace(collection) ? "vector-memory" : $"vector-memory-{collection}";
            contribution.McpServers[serverName] = new McpServerSpec(Transport: "http", Url: mcpUrl);
        }

        return contribution;
    }
}

public sealed class KnowledgeGraphModule : IResourceTypeModule
{
    public string TypeKey => "KnowledgeGraph";
    public string DisplayName => "Knowledge Graph";
    public string Icon => "🕸️";
    public string Blurb => "Domain entities and relations the agent consults — and optionally extends.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("path", "Storage folder", ResourceFieldKind.Path, Required: true,
            Hint: "Where the graph lives (typed nodes and edges, ontology-gated).",
            Placeholder: "/home/you/looper-memory/knowledge"),
        new("readOnly", "Read-only", ResourceFieldKind.Boolean,
            Hint: "The agent may query the graph but never modify it.")
    ];

    private static readonly Dictionary<string, string> Ontology = new()
    {
        ["depends_on"] = "Subject needs the object to function",
        ["part_of"] = "Subject is a component of the object",
        ["owned_by"] = "Subject is the responsibility of the object (team/person)",
        ["supersedes"] = "Subject replaces the object (answers: what is current?)",
        ["caused_by"] = "Subject happened because of the object",
        ["documented_in"] = "Subject is described in the object",
        ["decided_by"] = "Subject was decided by the object (answers: who do I ask?)",
        ["blocks"] = "Subject prevents progress on the object"
    };

    public void PrepareRun(ResourceModuleContext context)
    {
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return;
        GraphWorkspace.Ensure(path, Ontology, GraphReadmes.Knowledge);
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return contribution;

        var readOnly = context.GetBool("readOnly");

        contribution.EnvironmentVariables["LOOPER_KNOWLEDGE_GRAPH_PATH"] = path;
        contribution.AdditionalDirectories.Add(path);
        contribution.PromptSections.Add(
            $"KNOWLEDGE GRAPH at {path} (also $LOOPER_KNOWLEDGE_GRAPH_PATH): typed domain facts; protocol in its README.md. " +
            $"Query it before decisions that depend on domain facts: python3 \"{path}/loopergraph.py\" --dir \"{path}\" " +
            "neighbors <entity> | path <a> <b> | missing <node_type> <edge_type> | recall \"<fuzzy question>\". " +
            "Trust short paths over long ones — each hop multiplies uncertainty, and the tool prints per-path confidence." +
            (readOnly
                ? " This graph is READ-ONLY for you: query it, never modify it."
                : " When you establish a durable fact, `add` it with an edge type from ontology.json (never invent " +
                  "synonym types); when a fact stops being true, `supersede` or `invalidate` it — never delete. " +
                  "Use canonical entity names: check `neighbors` for the existing node before minting a new id."));

        return contribution;
    }
}

public sealed class MemoryGraphModule : IResourceTypeModule
{
    public string TypeKey => "MemoryGraph";
    public string DisplayName => "Memory Graph";
    public string Icon => "💭";
    public string Blurb => "Episodic memory linking what past iterations did, decided and left open.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("path", "Storage folder", ResourceFieldKind.Path, Required: true,
            Hint: "Where episodes and the facts extracted from them are stored.",
            Placeholder: "/home/you/looper-memory/episodes"),
        new("maxEpisodes", "Max episodes", ResourceFieldKind.Number,
            Hint: "Optional soft cap — beyond it, fold old episodes' lessons into facts.", Placeholder: "unbounded")
    ];

    private static readonly Dictionary<string, string> Ontology = new()
    {
        ["did"] = "An iteration performed the action named by the object",
        ["decided"] = "An iteration made the decision named by the object",
        ["learned"] = "An iteration learned the fact named by the object",
        ["left_open"] = "An iteration left the object unfinished",
        ["continues"] = "Subject episode carries on the object episode's work",
        ["caused_by"] = "Subject happened because of the object",
        ["supersedes"] = "Subject replaces the object"
    };

    public void PrepareRun(ResourceModuleContext context)
    {
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return;
        GraphWorkspace.Ensure(path, Ontology, GraphReadmes.Memory);
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return contribution;

        var maxEpisodes = context.GetNumber("maxEpisodes");

        contribution.EnvironmentVariables["LOOPER_MEMORY_GRAPH_PATH"] = path;
        contribution.AdditionalDirectories.Add(path);
        contribution.PromptSections.Add(
            $"EPISODIC MEMORY GRAPH at {path} (also $LOOPER_MEMORY_GRAPH_PATH); protocol in its README.md. " +
            $"Start each iteration with: python3 \"{path}/loopergraph.py\" --dir \"{path}\" recall \"<this iteration's task>\" " +
            "to learn what earlier iterations did, decided and left open. End each iteration by recording one episode " +
            "(`episode \"<what you did, why, outcome, open threads>\"`) and extracting its durable facts " +
            "(`add did|decided|learned|left_open this-iteration <object> --source <episode-id>`). Episodes are " +
            "immutable and append-only; when something a past iteration recorded stops being true, `invalidate` the " +
            "fact — never rewrite the episode." +
            (maxEpisodes is > 0
                ? $" Soft cap: around {maxEpisodes:0} episodes, fold the oldest episodes' still-relevant lessons into facts before adding more."
                : ""));

        return contribution;
    }
}

public sealed class ExecutionGraphModule : IResourceTypeModule
{
    public string TypeKey => "ExecutionGraph";
    public string DisplayName => "Execution Graph";
    public string Icon => "🧭";
    public string Blurb => "A dependency graph of work items the agent advances one node per loop.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("path", "Storage folder", ResourceFieldKind.Path, Required: true,
            Hint: "Where the execution graph lives (work items as nodes, dependencies as edges).",
            Placeholder: "/home/you/looper-memory/execution"),
        new("strictOrdering", "Strict ordering", ResourceFieldKind.Boolean,
            Hint: "Remind the agent to work exactly one ready node per iteration.")
    ];

    public void PrepareRun(ResourceModuleContext context)
    {
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return;
        GraphWorkspace.Ensure(path, ontologySeed: null, GraphReadmes.Execution);
        GraphWorkspace.EnsureExecutionFile(path);
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var path = context.GetString("path");
        if (string.IsNullOrWhiteSpace(path)) return contribution;

        var strict = context.GetBool("strictOrdering");

        contribution.EnvironmentVariables["LOOPER_EXECUTION_GRAPH_PATH"] = path;
        contribution.AdditionalDirectories.Add(path);
        contribution.PromptSections.Add(
            $"EXECUTION GRAPH at {path} (also $LOOPER_EXECUTION_GRAPH_PATH): the durable plan this loop advances; " +
            $"protocol in its README.md. Each iteration: python3 \"{path}/loopergraph.py\" --dir \"{path}\" exec-next, " +
            "pick ONE ready item, `exec-start` it, do the work, then `exec-done --note \"<outcome>\"` (or `exec-block " +
            "--note \"<why>\"`). Newly discovered work goes in with `exec-add <id> \"<title>\" --after <real-deps>` — " +
            "only add a dependency the work genuinely needs; false edges serialize work for nothing. The tool refuses " +
            "unknown dependencies, cycles, and starting before dependencies are done. The file is the checkpoint: " +
            "if a previous iteration crashed mid-item, exec-next shows it in_progress — verify its state before continuing." +
            (strict ? " Strict ordering: never touch more than one work item per iteration." : ""));

        return contribution;
    }
}
