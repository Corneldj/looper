namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// Protocol READMEs seeded into graph storage folders. Every Looper iteration is a
/// fresh context, so the full protocol lives here on disk — the run prompt carries
/// only the short version and points at this file.
/// </summary>
public static class GraphReadmes
{
    private const string SharedCrib = """
        ## Command crib (run from this folder, or pass --dir)

        ```
        python3 loopergraph.py status                              # what's here
        python3 loopergraph.py add <etype> <src> <dst> [--source ep-id] [--confidence 0.9]
        python3 loopergraph.py supersede <etype> <src> <new-dst>   # close old fact + add new
        python3 loopergraph.py invalidate <etype> <src> [--dst X]  # fact stopped being true
        python3 loopergraph.py neighbors <id> [--at 2026-02-01]    # facts touching a node
        python3 loopergraph.py path <a> <b>                        # how two nodes connect + confidence
        python3 loopergraph.py missing <node-type> <etype>         # absence query
        python3 loopergraph.py history <id>                        # everything ever true about a node
        python3 loopergraph.py recall "<fuzzy question>" [--at …]  # vector entry -> graph expansion
        ```

        ## Shared infrastructure (when this graph has a curator)

        A graph can serve many loops at once. Then the roles split — that separation is what
        keeps memory consistent:

        - **Consumers** (every executing loop): query freely; contribute learnings with
          `remember "<text>" --kind lesson|note|episode|proposal --agent <name> --run <id>` —
          each contribution is its own inbox file, so parallel writers never collide. Consumers
          NEVER write canonical facts.
        - **The curator** (one dedicated loop): the only canonical writer. Works the inbox
          (`inbox`, then per item extract facts and `inbox-merge <id>` or `inbox-reject <id>
          --reason "…"`), resolves the competing facts `health` reports, and runs
          `decay --days 30` so unused facts sink in ranking. Looper measures health in the
          background and raises `graph.<slug>.needs-curation` when the inbox hits its
          threshold — point the curator loop's event trigger at that topic.

        Mutations take an advisory file lock; `usage.jsonl` records what `recall` actually
        returns, which is what `health` and `decay` weigh facts by.

        ## Rules that keep this graph trustworthy

        1. **Typed edges only, from ontology.json.** A small controlled vocabulary is the whole
           point — synonym edge types (`relates_to`, `is_associated_with`) make queries return
           incomplete results. Extend the ontology only for a genuinely new relationship.
        2. **Invalidate, never delete.** History answers "what was true in March" and "when did
           this change" — deleting amputates it. `supersede` and `invalidate` close validity
           windows; nothing is ever removed.
        3. **Canonical ids.** Check `neighbors <id>` before minting a new node id — "auth-lib"
           and "auth-library" as two nodes silently halves every answer. Ids are kebab-case,
           minted once, never reused.
        4. **Short paths over long ones.** Confidence multiplies per hop; the tool prints it.
           A 2-hop answer at 0.9 beats a 5-hop chain at 0.5 — verify low-confidence paths
           before relying on them.
        """;

    public const string Knowledge = $"""
        # Knowledge graph

        Domain entities (nodes) and typed relations (edges) for the agents that share this
        folder. It answers connected questions a text search cannot: multi-hop ("what breaks
        if X fails?"), temporal ("who owned this in January?"), and absence ("which services
        have no owner?").

        Maintained by Looper agents via `loopergraph.py`. The ontology in `ontology.json` is
        the authority on edge types; edit it deliberately and keep it small.

        {SharedCrib}
        """;

    public const string Memory = $"""
        # Episodic memory graph

        What past loop iterations did, decided, learned, and left open. Two layers:

        - **Episodes** (`episodes.jsonl`) — raw, immutable, append-only accounts. One per
          iteration: what you did, why, the outcome, open threads. Never rewritten.
        - **Facts** (`edges.jsonl`) — typed, durable claims extracted from episodes, each
          carrying provenance (`--source <episode-id>`) and a validity window.

        Loop protocol: `recall` at the start of each iteration; one `episode` plus its
        extracted facts at the end. When reality changes, `invalidate` the fact — the episode
        that recorded the old state stays as history.

        {SharedCrib}
        """;

    public const string VectorMemory = $"""
        # Continuous vector memory graph

        Semantic memory with a fuzzy front door: `recall` embeds your query, finds entry
        nodes by similarity, expands along typed edges (reaching facts that share no words
        with the query), filters by validity at the asked-about time, and ranks — each result
        shows the path it came in through.

        The built-in embedder is a deterministic bag-of-tokens hash: offline, dependency-free,
        and honestly **lexical** — it matches word overlap, not meaning. Summaries are
        recomputed at query time, so entries never go stale. For true semantic matching,
        attach a real vector store via the resource's MCP URL and prefer its tools; this
        folder then remains the audit trail.

        Same store, three question shapes: fuzzy (`recall "billing thing from a while back"`),
        exact (`neighbors invoice-7712`), temporal (`recall "kim's plan" --at 2026-02-01`).

        {SharedCrib}
        """;

    public const string Execution = """
        # Execution graph

        The durable plan this loop advances: work items as nodes, dependencies as edges,
        statuses pending / in_progress / done / blocked. `execution.json` is the checkpoint —
        every mutation rewrites it atomically, so a crashed iteration loses nothing and the
        next one resumes exactly where the plan stands.

        ## Loop protocol

        1. `python3 loopergraph.py exec-next` — see what's ready. An `in_progress` item from
           a crashed iteration shows first: verify how far it actually got before continuing.
        2. Pick **one** ready item. `exec-start <id>`, do the work, then
           `exec-done <id> --note "<outcome>"` or `exec-block <id> --note "<why>"`.
        3. Discovered new work? `exec-add <id> "<title>" --after <deps>` — declare only
           dependencies the work *genuinely* needs. A false edge serializes work for nothing;
           a long chain multiplies failure odds per step, so prefer wide over deep.
        4. `exec-validate` after restructuring — it catches unknown dependencies and cycles.

        The tool enforces the invariants (no starting before dependencies are done, no cycles,
        no unknown deps). Notes on done/blocked items are the plan's memory — write them for
        the next iteration, which starts with none of your context.
        """;
}
