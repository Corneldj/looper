using System.Diagnostics;
using System.Text.Json;
using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Features.ResourceTypes;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Looper.Api.Features.Architect;

public sealed record CreatedItemDto(Guid Id, string Name, string Detail);

public sealed record ArchitectResultDto(
    bool Success,
    string Report,
    decimal CostUsd,
    IReadOnlyList<CreatedItemDto> CreatedResources,
    IReadOnlyList<CreatedItemDto> CreatedAgents,
    string? Error);

/// <summary>
/// The in-app architect: an AI run that sets up an entire workflow — resources, agents,
/// their wiring — the same way a user would, through Looper's own API. Its toolset is
/// curl-only (enforced via the CLI tool allowlist) and additive-only (no delete endpoints
/// are in the toolset); every agent it creates starts dry-run and disabled, so nothing
/// spends tokens or runs until a human flips it. What it built is reported by diffing
/// the workspace before and after — not by trusting its own account.
/// </summary>
public sealed record BuildWorkflowCommand(string Description, Guid? WorkflowId = null) : ICommand<ArchitectResultDto>;

public sealed class BuildWorkflowValidator : AbstractValidator<BuildWorkflowCommand>
{
    public BuildWorkflowValidator()
    {
        RuleFor(c => c.Description).NotEmpty().MinimumLength(10)
            .WithMessage("Describe the workflow you want (at least a sentence).");
    }
}

public sealed class BuildWorkflowHandler(
    LooperDbContext db,
    ResourceModuleRegistry registry,
    IOptions<LooperOptions> options,
    ClaudeAuthProvider claudeAuth,
    ILogger<BuildWorkflowHandler> logger) : ICommandHandler<BuildWorkflowCommand, ArchitectResultDto>
{
    public async Task<ArchitectResultDto> Handle(BuildWorkflowCommand command, CancellationToken cancellationToken)
    {
        var workflowId = await Workflows.WorkflowMapper.ResolveAsync(db, command.WorkflowId, cancellationToken);
        var resourcesBefore = await db.Resources.Select(r => r.Id).ToHashSetAsync(cancellationToken);
        var agentsBefore = await db.Agents.Select(a => a.Id).ToHashSetAsync(cancellationToken);

        var inventory = await BuildInventoryJson(workflowId, cancellationToken);
        var prompt = BuildArchitectPrompt(command.Description, inventory, options.Value.PublicUrl.TrimEnd('/'), workflowId);

        var (success, report, cost, error) = await RunBuilderAsync(prompt, cancellationToken);

        // The authoritative account of what changed: the diff, not the narrative.
        var createdResources = await db.Resources.AsNoTracking()
            .Where(r => !resourcesBefore.Contains(r.Id))
            .Select(r => new { r.Id, r.Name, r.Type, r.CustomTypeKey })
            .ToListAsync(cancellationToken);
        var createdAgents = await db.Agents.AsNoTracking()
            .Where(a => !agentsBefore.Contains(a.Id))
            .Select(a => new CreatedItemDto(a.Id, a.Name, a.Model))
            .ToListAsync(cancellationToken);

        return new ArchitectResultDto(
            success,
            report,
            cost,
            createdResources.Select(r => new CreatedItemDto(r.Id, r.Name, TypeLabel(r.Type, r.CustomTypeKey))).ToList(),
            createdAgents,
            error);
    }

    private string TypeLabel(ResourceType type, string? customTypeKey)
    {
        if (type == ResourceType.Custom && customTypeKey is not null && registry.TryGet(customTypeKey, out var module))
        {
            return module.DisplayName;
        }
        return ResourceTypeCatalog.BuiltIns.FirstOrDefault(t => t.TypeKey == type.ToString())?.Label ?? type.ToString();
    }

    private async Task<string> BuildInventoryJson(Guid workflowId, CancellationToken cancellationToken)
    {
        var resources = await db.Resources.AsNoTracking()
            .Where(r => r.WorkflowId == workflowId)
            .Select(r => new { id = r.Id, name = r.Name, type = r.Type.ToString(), customTypeKey = r.CustomTypeKey, description = r.Description })
            .ToListAsync(cancellationToken);
        var agents = await db.Agents.AsNoTracking()
            .Where(a => a.WorkflowId == workflowId)
            .Select(a => new
            {
                id = a.Id, name = a.Name, model = a.Model, intervalMinutes = a.IntervalMinutes,
                enabled = a.Enabled, dryRun = a.DryRun, autonomyLevel = a.AutonomyLevel,
                resourceIds = a.Resources.Select(r => r.Id).ToList(),
            })
            .ToListAsync(cancellationToken);
        var dynamicTypes = registry.All.Select(m => new
        {
            typeKey = m.TypeKey,
            label = m.DisplayName,
            fields = m.Fields.Select(f => new { key = f.Key, kind = f.Kind.ToString(), required = f.Required }),
        });

        return JsonSerializer.Serialize(
            new { resources, agents, dynamicResourceTypes = dynamicTypes },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<(bool Success, string Report, decimal Cost, string? Error)> RunBuilderAsync(
        string prompt, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.ClaudeCommand,
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        startInfo.Environment["LOOPER_API_URL"] = options.Value.PublicUrl.TrimEnd('/');
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("claude-opus-5");
        startInfo.ArgumentList.Add("--max-turns");
        startInfo.ArgumentList.Add("60");
        startInfo.ArgumentList.Add("--allowedTools");
        startInfo.ArgumentList.Add("Bash(curl:*)");

        try
        {
            await claudeAuth.ApplyAsync(startInfo, cancellationToken);
        }
        catch (ClaudeAuthException ex)
        {
            return (false, "", 0, ex.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return (false, "", 0,
                $"Could not start '{options.Value.ClaudeCommand}'. Install Claude Code or set Looper:ClaudeCommand. ({ex.Message})");
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        _ = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* gone */ }
            return (false, "", 0, "The architect run timed out after 10 minutes. Anything it created before the timeout is listed below.");
        }

        using var document = ClaudeCliExecutor.ExtractResultObject(await stdoutTask);
        if (document is null)
        {
            return (false, "", 0, $"The Claude CLI returned no result (exit code {process.ExitCode}).");
        }

        var root = document.RootElement;
        var cost = root.TryGetProperty("total_cost_usd", out var costProp) ? costProp.GetDecimal() : 0m;
        var report = root.TryGetProperty("result", out var resultProp) ? resultProp.GetString() ?? "" : "";
        var isError = root.TryGetProperty("is_error", out var errorProp) && errorProp.ValueKind == JsonValueKind.True;

        logger.LogInformation("Architect run finished (error: {IsError}, ${Cost})", isError, cost);
        return (!isError, report, cost, isError ? (string.IsNullOrWhiteSpace(report) ? "The architect run failed." : report) : null);
    }

    internal static string BuildArchitectPrompt(string description, string inventoryJson, string apiUrl, Guid? workflowId = null) =>
        $$"""
        You are the Looper architect. Looper runs autonomous agent loops for any kind of work —
        marketing, support, research, operations, finance, engineering — and users compose workflows
        from RESOURCES (capabilities) wired into AGENTS (scheduled loops). Your job: set up the
        workflow the user asks for below, exactly as a careful user would — reusing what already
        exists, creating only what's missing, and wiring it together. Do not assume the work is
        software unless the request says so; pick gates, metrics and folders that fit the profession.

        THE USER'S REQUEST:
        {{description}}

        YOU ARE BUILDING INSIDE WORKFLOW {{workflowId ?? Domain.Workflow.DefaultId}}: every resource and agent you
        create MUST include "workflowId":"{{workflowId ?? Domain.Workflow.DefaultId}}" in its JSON body, and you may
        only attach resources that belong to this workflow (all items in the inventory below do).

        THE CURRENT WORKSPACE (reuse suitable items from this pool instead of duplicating them):
        {{inventoryJson}}

        YOUR TOOLSET — Looper's API at {{apiUrl}} via curl (your ONLY tool). All POSTs take
        -H 'Content-Type: application/json'. This toolset is ADDITIVE: you may create and wire,
        never remove — no delete calls, ever, even if asked.

        1. List resource types (built-in + dynamic, with field specs for dynamic ones):
           curl -s {{apiUrl}}/api/resource-types
        2. Create a resource:
           curl -s -X POST {{apiUrl}}/api/resources -d '{"workflowId":"{{workflowId ?? Domain.Workflow.DefaultId}}","name":"…","type":"<TypeKey>","customTypeKey":null,"description":"…","configJson":"<JSON string>"}'
           configJson shapes per built-in type:
           - McpServer: {"transport":"stdio|http|sse","command":"…","args":["…"],"env":{},"url":"…"}
           - FileLocation: {"path":"/abs/path","primary":true}   (primary = the agent's working directory)
           - Rag: {"instructions":"…","path":"…?","url":"…?"}
           - TestingAction ("Check"): {"command":"python3 verify_report.py","workingDirectory":"…?","timeoutSeconds":300} — any command that exits non-zero when the work is wrong
           - Rule: {"text":"…"}
           - RuleSet: {"rules":[{"text":"…","enabled":true}]}
           - SubAgent: {"description":"…","prompt":"…","tools":"Read,Grep?","model":"…?"}
           - Reviewer: {"rubric":"what acceptable means","model":"…?","maxFixRounds":2,"escalateOnFail":true}
           - UserAction: {"instructions":"…?","blockScheduling":true} — the TOOL that lets an agent ask the human for something only they
             can do or decide (one per agent that may hit such moments; the agent chooses what to ask at run time). Answers are recorded
             into the agent's resources on resolution, never replayed as hidden context.
           - WorkspacePool: {"rootPath":"/abs/path","provisioning":"blank|git-clone|copy-template","source":"…?","retentionDays":14,"maxWorkspaces":null}
           - Specification (type "Custom", customTypeKey "Specification"): {"specId":"SPEC-1","content":"REQ-1 …\nAC-1 …\nAC-2 …","path":"…?","advisory":false}
             — write real REQ-n/AC-n identifiers; Looper then REQUIRES every deliverable the attached agent registers (a pull request, a published page, a filed report — anything with a link) to cite the AC ids it satisfies, verified against the spec. Attach one to any agent whose deliverable must satisfy written requirements.
           - Script (type "Custom", customTypeKey "Script"): {"language":"python|bash","code":"<the full script>","trigger":"before|after","args":"","timeoutSeconds":120,"workingDirectory":"…?"}
             — a runnable script for the deterministic parts of a loop, always run by Looper (never by the model): "before" = before every
             iteration, stdout handed to the agent as context (fetch inputs, snapshot state); "after" = after every iteration as a gate
             (non-zero exit fails the run — verification without tokens).
           - EventRaiser (type "Custom", customTypeKey "EventRaiser"): {"topic":"newsletter.sent","when":"succeeded|failed|always","payload":"{agent} finished: {result}"}
             — Looper raises the topic deterministically when the attached agent's run ends that way. This is HOW loops chain: the
             producing agent gets a raiser, the consuming agent gets a listener for the same topic.
           - EventListener (type "Custom", customTypeKey "EventListener"): {"topic":"newsletter.sent"} (exact, or a prefix ending in .*)
             — wakes the attached agent whenever a matching event is raised, in any trigger mode. Topics are dotted lowercase keys.
             Every agent also raises agent.<name-slug>.succeeded / .failed automatically. GET {{apiUrl}}/api/events/topics lists known topics.
             Write real, working code (stdlib only); scripts see LOOPER_API_URL/LOOPER_RUN_ID/LOOPER_AGENT_ID and the agent's credential env vars.
           - Metric (type "Custom", customTypeKey "Metric"): {"unit":"sign-ups","aggregation":"latest|sum|average","direction":"higher|lower","target":1000,"instructions":"how and when to measure"}
             — a user-defined OUTCOME the dashboard tracks (pull requests are just one possible outcome). Create one for whatever the
             workflow is meant to move (sign-ups, conversion, resolution time, revenue, defects) and attach it to every agent whose work
             affects it; attached agents get a reporting protocol, and Script resources can report by printing `@metric <name>=<number>`.
           - PatToken ("API key / secret"): {"envVar":"MAILCHIMP_API_KEY","value":"<placeholder>"} / AzureConnection — credential configs: create ONLY with placeholder values and say so in your report; never invent real secrets.
           - Dynamic types: type "Custom" + customTypeKey "<TypeKey>"; configJson keys = the type's field keys.
        3. Create an agent (a loop started on a schedule OR by events — one or the other):
           curl -s -X POST {{apiUrl}}/api/agents -d '{"workflowId":"{{workflowId ?? Domain.Workflow.DefaultId}}","name":"…","description":"…","prompt":"<the loop prompt>","model":"claude-opus-5|claude-sonnet-5|claude-haiku-4-5","effort":"Low|Medium|High","intervalMinutes":60,"triggerMode":"Scheduled","triggerTopics":null,"maxTurns":25,"maxBudgetUsd":null,"workingDirectory":null,"allowedTools":null,"bypassPermissions":true,"dryRun":true,"autonomyLevel":2,"resourceIds":["<resource ids to attach>"]}'
           For an event-driven loop prefer wiring an EventListener resource (visible on the canvas); "triggerMode":"Event" with
           "triggerTopics":"topic.one\ntopic.prefix.*" also works (every finished run raises agent.<name-slug>.succeeded/.failed;
           graph maintenance raises graph.<name-slug>.needs-curation).
        4. Update an agent (e.g. to attach more resources later): PUT {{apiUrl}}/api/agents/<id> with the same body shape —
           or wire one resource without resending the body: POST {{apiUrl}}/api/agents/<agent id>/resources/<resource id> (idempotent).
        5. If the workflow genuinely needs a capability no existing type covers, you may commission
           a new resource type (this invokes another AI and takes minutes — use sparingly):
           curl -s -X POST {{apiUrl}}/api/resource-types/generate -d '{"description":"…"}'
        6. TEST every Script you write before attaching it — a script that does not run is a gate that passes nothing:
           curl -s -X POST {{apiUrl}}/api/scripts/run -d '{"language":"python","code":"<the script>","args":""}'
           → {"exitCode","passed","output"}. Fix and re-run until it exits 0 with the output you expect.
        7. Workflows are separate workbenches: GET {{apiUrl}}/api/workflows lists them. Build inside the workflow named above
           unless the user explicitly asks for a separate one — then POST {{apiUrl}}/api/workflows -d '{"name":"…","description":"…"}'
           and use the returned id as workflowId for everything you create there.
        8. Verify your wiring before reporting: GET {{apiUrl}}/api/architecture/map?workflowId=<id> shows every agent with its
           resourceIds, raises (event topics) and listens (patterns); GET {{apiUrl}}/api/metrics?workflowId=<id> lists the metrics.

        THE SHARED MEMORY PATTERN (use it whenever several loops must stay aligned on standards,
        decisions, or past work): create ONE memory-shaped graph resource (ContinuousVectorMemoryGraph,
        KnowledgeGraph, or MemoryGraph as a Custom type) with config keys {"path":"/abs/path",
        "curator":"<curator agent name>","autoLog":true} — then a dedicated CURATOR agent with
        "triggerMode":"Event" and "triggerTopics":"graph.<resource-name-slug>.needs-curation", whose
        prompt is to work the graph's inbox and health report (the harness injects the full curation
        protocol automatically). Attach the graph to every consumer loop too: they get query access,
        an automatic memory preamble, and an inbox to contribute to — but only the curator writes
        canonical facts. Do NOT tell consumer loops to maintain the graph; the separation is the point.

        DETERMINISM — Looper fails closed and so must your design:
        - A run counts as succeeded only when the model finished AND every gate passed; agent.<slug>.succeeded fires only then,
          and a run that ends without a result, hits its turn or budget cap, or has a resource that fails to apply is a failure.
        - Give every agent at least one deterministic check of its work: an after-run Script or TestingAction (exit code) and/or a
          Reviewer with a real rubric. Gates and reviewers with nothing configured fail every run on purpose.
        - Fetch inputs with a before-run Script rather than asking the model to go and look; verify outputs with an after-run Script
          rather than trusting the model's summary; report outcomes with Metrics (scripts print `@metric name=value`).
        - Chain loops with EventRaiser → EventListener pairs, never with prose asking one agent to trigger another.
        - Agents start every iteration from a clean context: durable knowledge belongs in Rule Sets, Specifications, and memory
          graphs — never in "remember that…" prompt text. Answers to user action requests are recorded as rules automatically.

        GOVERNANCE — non-negotiable:
        - Every agent you create: "dryRun": true and leave it DISABLED (never call the enabled endpoint).
          The human reviews and flips the switches. Say this in your report.
        - Autonomy level 1 or 2 for new agents; promotion is earned with evidence, not granted at birth.
        - Reuse pool items where they fit; do not create near-duplicates of existing resources.
        - Write real, specific loop prompts and rubrics — a workflow of placeholder prose is worthless.
        - Give agents a Reviewer or TestingAction gate whenever their work product can be checked.
        - Define at least one Metric for the outcome the user actually wants, and attach it — a workflow nobody can measure cannot be improved.

        Work step by step: check the types you need, create resources first (capture the ids from
        each response), then the agent(s) referencing those ids. Verify each call's response before
        moving on; if a call fails, read the error and correct it.

        End your response with a concise report for the user: what you created and why, what you
        reused from the pool, what they must review or fill in (credentials, paths), and the exact
        switches to flip when they're ready to go live.
        """;
}

public sealed class BuildWorkflowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/architect/build", (BuildWorkflowCommand command, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(command, ct));
}
