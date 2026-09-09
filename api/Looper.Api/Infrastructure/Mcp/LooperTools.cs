using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Domain;
using Looper.Api.Features.Delivery;
using Looper.Api.Features.Events;
using Looper.Api.Features.Metrics;
using Looper.Api.Features.UserActions;
using Looper.Api.Features.Workspaces;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules.BuiltIn;

namespace Looper.Api.Infrastructure.Mcp;

/// <summary>Everything a tool needs to act on behalf of one run: who is calling and what it was given.</summary>
public sealed record ToolCallContext(
    Guid RunId,
    Guid AgentId,
    IReadOnlyList<Resource> Resources,
    IDispatcher Dispatcher,
    CancellationToken Cancellation);

/// <summary>A tool the model can call. The schema is what the model sees; Invoke is what happens.</summary>
public sealed record LooperTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject, ToolCallContext, Task<object?>> Invoke);

/// <summary>An argument the model got wrong — reported back to it as a tool error, not as a crash.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>
/// The tools Looper offers a running agent. A resource IS its tool: attaching "Ask the user" gives
/// the run an ask_user tool, attaching a Metric gives it record_metric with that metric's name,
/// attaching a Workspace pool gives it claim/finish/list. Nothing here is prose the model may or
/// may not follow — it is a typed call the harness executes and records. The tool set for a run
/// is a pure function of the attached resources, so the same workflow always offers the same tools.
/// </summary>
public static class LooperTools
{
    /// <summary>The MCP server name; Claude Code exposes each tool as mcp__looper__&lt;name&gt;.</summary>
    public const string ServerName = "looper";

    public static string ServerUrl(string publicUrl, Guid runId) => $"{publicUrl.TrimEnd('/')}/mcp/runs/{runId}";

    public static string QualifiedName(string tool) => $"mcp__{ServerName}__{tool}";

    public static IReadOnlyList<LooperTool> ForRun(IReadOnlyList<Resource> resources)
    {
        var tools = new List<LooperTool> { ReportDeliverable(resources), Escalate(), RaiseEvent() };

        var askUser = resources.Where(r => r.Type == ResourceType.UserAction).ToList();
        if (askUser.Count > 0) tools.Add(AskUser(askUser));

        var metrics = resources.Where(MetricResources.IsMetric).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (metrics.Count > 0) tools.Add(RecordMetric(metrics));

        var pools = ClaudeCliExecutor.WorkspacePools(resources).ToList();
        if (pools.Count > 0)
        {
            tools.Add(ClaimWorkspace(pools));
            tools.Add(FinishWorkspace());
            tools.Add(ListWorkspaces(pools));
        }

        return tools;
    }

    // ---------- always on ----------

    private static LooperTool ReportDeliverable(IReadOnlyList<Resource> resources)
    {
        var hasSpec = resources.Any(r => r.Type == ResourceType.Custom &&
                                         string.Equals(r.CustomTypeKey, "Specification", StringComparison.OrdinalIgnoreCase));
        var description =
            "Register something you actually finished that has a link — a pull request, a published page, a document, a report. " +
            "Call it the moment the deliverable exists, once per deliverable. Never invent a link." +
            (hasSpec
                ? " A specification is attached to this run: `satisfies` is REQUIRED and must cite its acceptance criteria " +
                  "(e.g. \"AC-1,AC-3\"); registrations that omit citations or cite unknown criteria are rejected."
                : " If a specification applies, cite the acceptance criteria it satisfies in `satisfies` (e.g. \"AC-1,AC-3\").");
        var schema = Schema(["url", "title"],
            ("url", Str("Link to the deliverable (the PR URL, the published URL, the document link).")),
            ("title", Str("What it is, in one line.")),
            ("satisfies", Str("Comma-separated acceptance criteria ids this deliverable satisfies, e.g. \"AC-1,AC-3\".")),
            ("repoPath", Str("Local path of the repository the deliverable came from, when there is one (usually your working directory).")));
        return new LooperTool("report_deliverable", description, schema, async (args, ctx) =>
            await ctx.Dispatcher.Send(new RegisterPullRequestCommand(
                ctx.RunId, ctx.AgentId, Required(args, "url"), Required(args, "title"),
                Optional(args, "repoPath"), null, Optional(args, "satisfies")), ctx.Cancellation));
    }

    private static LooperTool Escalate() => new(
        "escalate",
        "You are blocked on something only a human can decide or authorize, and there is no way to finish this iteration " +
        "without it. Records the escalation on this run so the dashboard shows it. After calling it, stop cleanly: summarise " +
        "what you completed and what you need.",
        Schema(["reason"], ("reason", Str("One sentence on what you need and why."))),
        async (args, ctx) =>
        {
            await ctx.Dispatcher.Send(new EscalateRunCommand(ctx.RunId, Required(args, "reason")), ctx.Cancellation);
            return new { escalated = true, runId = ctx.RunId };
        });

    private static LooperTool RaiseEvent() => new(
        "raise_event",
        "Raise a named event on Looper's event bus so other loops that listen for it wake up. Topics are dotted lowercase keys " +
        "(e.g. docs.updated, newsletter.sent). Raise events only for real, completed facts — never speculatively. " +
        "Your run's own completion event (agent.<name>.succeeded / .failed) is raised by Looper automatically.",
        Schema(["topic"], ("topic", Str("The topic, e.g. \"docs.updated\". Letters, digits, dashes and dots; no wildcards.")),
               ("payload", Str("What happened, for the loops that wake up."))),
        async (args, ctx) =>
            await ctx.Dispatcher.Send(new RaiseEventCommand(Required(args, "topic"), Optional(args, "payload"), ctx.RunId, ctx.AgentId), ctx.Cancellation));

    // ---------- per resource ----------

    private static LooperTool AskUser(IReadOnlyList<Resource> resources)
    {
        var guidance = resources
            .Select(r => ResourceConfig.Parse<UserActionConfig>(r).Instructions)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i!.Trim())
            .ToList();
        var description =
            "Ask the user for something only they can do or decide — unclear requirements, a choice between real alternatives, " +
            "a credential or approval you lack. Use it instead of guessing or failing. Raising a request is NOT a failure: your " +
            "loop pauses until the user completes it, then resumes from a clean context. The answer is never handed to you as a " +
            "message — it is recorded into your resources (a standing rule in your system prompt, or your memory graph's inbox), " +
            "so check your rules before raising the same request again; if the action was done without a record, verify the " +
            "state yourself; if it was not done correctly, raise a new request stating exactly what is still missing. After " +
            "calling it, finish the iteration cleanly." +
            (guidance.Count > 0 ? " Guidance from the user on when to ask: " + string.Join(" ", guidance) : "");
        return new LooperTool("ask_user", description,
            Schema(["title", "details"], ("title", Str("The ask, in one line.")),
                   ("details", Str("Exactly what you need and why — with the options, if it is a decision."))),
            async (args, ctx) =>
                await ctx.Dispatcher.Send(new RaiseUserActionCommand(ctx.RunId, ctx.AgentId, Required(args, "title"), Optional(args, "details")), ctx.Cancellation));
    }

    private static LooperTool RecordMetric(IReadOnlyList<Resource> metrics)
    {
        var names = metrics.Select(m => m.Name).ToArray();
        var lines = metrics.Select(m =>
        {
            var config = MetricResources.Parse(new Modules.ResourceModuleContext(m.ConfigJson));
            var semantics = config.Aggregation switch
            {
                MetricAggregation.Sum => "values add up — report only the new amount since your last report",
                MetricAggregation.Average => "values are averaged — report each measurement",
                _ => "latest value wins — report the figure as it stands now"
            };
            return $"\"{m.Name}\"{(string.IsNullOrWhiteSpace(config.Unit) ? "" : $" ({config.Unit})")}: {semantics}.";
        });
        var description =
            "Record a measurement for one of the outcomes the user tracks on the dashboard. Report only measured facts — never " +
            "estimates, projections or hopes; if you could not measure it this iteration, do not call this. Metrics on this run: " +
            string.Join(" ", lines);
        var metric = Str("Which metric.");
        metric["enum"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray());
        return new LooperTool("record_metric", description,
            Schema(["metric", "value"], ("metric", metric), ("value", Num("The measured number.")), ("note", Str("What you measured and how."))),
            async (args, ctx) =>
                await ctx.Dispatcher.Send(new RecordMetricValueCommand(Required(args, "metric"), RequiredNumber(args, "value"),
                    Optional(args, "note"), ctx.RunId, ctx.AgentId, "Agent"), ctx.Cancellation));
    }

    private static LooperTool ClaimWorkspace(IReadOnlyList<(Resource Resource, WorkspacePoolConfig Config)> pools)
    {
        var poolNames = pools.Select(p => p.Resource.Name).ToArray();
        var description =
            "Claim a dedicated workspace directory for a distinct unit of work (a feature, a fix, a project) instead of working in " +
            "a shared directory. Returns the workspace path: cd there and read WORKBRIEF.md first. Claiming the same unit again " +
            "returns the same workspace, so iterations resume where the last one left off. Before finishing an iteration, append " +
            "a handoff note to WORKBRIEF.md. Pools on this run: " +
            string.Join("; ", pools.Select(p => $"\"{p.Resource.Name}\" (root {p.Config.RootPath})")) + ".";
        var pool = Str(pools.Count == 1 ? "The pool (only one is attached; may be omitted)." : "Which pool to claim in.");
        pool["enum"] = new JsonArray(poolNames.Select(n => (JsonNode)n).ToArray());
        string[] required = pools.Count == 1 ? ["unit"] : ["pool", "unit"];
        return new LooperTool("claim_workspace", description,
            Schema(required, ("pool", pool), ("unit", Str("Short kebab-case name of the unit of work, e.g. \"checkout-retry\".")),
                   ("context", Str("What this unit is about — written into the workspace brief."))),
            async (args, ctx) =>
            {
                var chosen = ResolvePool(pools, Optional(args, "pool"));
                return await ctx.Dispatcher.Send(new ClaimWorkspaceCommand(chosen.Id, Required(args, "unit"), Optional(args, "context"),
                    ctx.RunId, ctx.AgentId), ctx.Cancellation);
            });
    }

    private static LooperTool FinishWorkspace() => new(
        "finish_workspace",
        "Mark a claimed workspace's unit of work as fully complete. Looper cleans the directory up after the pool's retention window.",
        Schema(["workspaceId"], ("workspaceId", Str("The workspace id returned by claim_workspace.")), ("summary", Str("One line on what was delivered."))),
        async (args, ctx) =>
        {
            await ctx.Dispatcher.Send(new CompleteWorkspaceCommand(RequiredGuid(args, "workspaceId"), Optional(args, "summary")), ctx.Cancellation);
            return new { done = true };
        });

    private static LooperTool ListWorkspaces(IReadOnlyList<(Resource Resource, WorkspacePoolConfig Config)> pools)
    {
        var pool = Str("Limit to one pool; omit for every pool on this run.");
        pool["enum"] = new JsonArray(pools.Select(p => (JsonNode)p.Resource.Name).ToArray());
        return new LooperTool("list_workspaces",
            "List the workspaces already claimed in this run's pools: their units, paths, status and briefs — so you resume work instead of starting over.",
            Schema(("pool", pool)),
            async (args, ctx) =>
            {
                var chosen = Optional(args, "pool") is { } name ? ResolvePool(pools, name).Id : (Guid?)null;
                var all = await ctx.Dispatcher.Query(new GetWorkspacesQuery(chosen, false), ctx.Cancellation);
                var ids = pools.Select(p => p.Resource.Id).ToHashSet();
                return all.Where(w => ids.Contains(w.ResourceId)).ToList();
            });
    }

    private static Resource ResolvePool(IReadOnlyList<(Resource Resource, WorkspacePoolConfig Config)> pools, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return pools.Count == 1
                ? pools[0].Resource
                : throw new ToolArgumentException($"Say which pool: {string.Join(", ", pools.Select(p => $"\"{p.Resource.Name}\""))}.");
        }
        return pools.FirstOrDefault(p => string.Equals(p.Resource.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).Resource
               ?? throw new ToolArgumentException($"No pool named \"{name}\" on this run. Pools: {string.Join(", ", pools.Select(p => $"\"{p.Resource.Name}\""))}.");
    }

    // ---------- schema and argument helpers ----------

    private static JsonObject Schema(params (string Key, JsonObject Property)[] properties) => Schema([], properties);

    private static JsonObject Schema(string[] required, params (string Key, JsonObject Property)[] properties)
    {
        var props = new JsonObject();
        foreach (var (key, property) in properties) props[key] = property;
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
        return schema;
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Num(string description) => new() { ["type"] = "number", ["description"] = description };

    private static string? Optional(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => node.ToJsonString(),
            _ => throw new ToolArgumentException($"'{key}' must be a string.")
        };
    }

    private static string Required(JsonObject args, string key) =>
        Optional(args, key) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ToolArgumentException($"'{key}' is required.");

    private static double RequiredNumber(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) throw new ToolArgumentException($"'{key}' is required.");
        if (node.GetValueKind() == JsonValueKind.Number) return node.GetValue<double>();
        if (node.GetValueKind() == JsonValueKind.String && double.TryParse(node.GetValue<string>(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)) return parsed;
        throw new ToolArgumentException($"'{key}' must be a number.");
    }

    private static Guid RequiredGuid(JsonObject args, string key) =>
        Guid.TryParse(Required(args, key), out var id) ? id : throw new ToolArgumentException($"'{key}' must be an id returned by Looper.");
}
