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
public sealed record BuildWorkflowCommand(string Description) : ICommand<ArchitectResultDto>;

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
    ILogger<BuildWorkflowHandler> logger) : ICommandHandler<BuildWorkflowCommand, ArchitectResultDto>
{
    public async Task<ArchitectResultDto> Handle(BuildWorkflowCommand command, CancellationToken cancellationToken)
    {
        var resourcesBefore = await db.Resources.Select(r => r.Id).ToHashSetAsync(cancellationToken);
        var agentsBefore = await db.Agents.Select(a => a.Id).ToHashSetAsync(cancellationToken);

        var inventory = await BuildInventoryJson(cancellationToken);
        var prompt = BuildArchitectPrompt(command.Description, inventory, options.Value.PublicUrl.TrimEnd('/'));

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

    private async Task<string> BuildInventoryJson(CancellationToken cancellationToken)
    {
        var resources = await db.Resources.AsNoTracking()
            .Select(r => new { id = r.Id, name = r.Name, type = r.Type.ToString(), customTypeKey = r.CustomTypeKey, description = r.Description })
            .ToListAsync(cancellationToken);
        var agents = await db.Agents.AsNoTracking()
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
        startInfo.ArgumentList.Add("50");
        startInfo.ArgumentList.Add("--allowedTools");
        startInfo.ArgumentList.Add("Bash(curl:*)");

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

    internal static string BuildArchitectPrompt(string description, string inventoryJson, string apiUrl) =>
        $$"""
        You are the Looper architect. Looper runs autonomous agent loops; users compose workflows
        from RESOURCES (capabilities) wired into AGENTS (scheduled loops). Your job: set up the
        workflow the user asks for below, exactly as a careful user would — reusing what already
        exists, creating only what's missing, and wiring it together.

        THE USER'S REQUEST:
        {{description}}

        THE CURRENT WORKSPACE (reuse suitable items from this pool instead of duplicating them):
        {{inventoryJson}}

        YOUR TOOLSET — Looper's API at {{apiUrl}} via curl (your ONLY tool). All POSTs take
        -H 'Content-Type: application/json'. This toolset is ADDITIVE: you may create and wire,
        never remove — no delete calls, ever, even if asked.

        1. List resource types (built-in + dynamic, with field specs for dynamic ones):
           curl -s {{apiUrl}}/api/resource-types
        2. Create a resource:
           curl -s -X POST {{apiUrl}}/api/resources -d '{"name":"…","type":"<TypeKey>","customTypeKey":null,"description":"…","configJson":"<JSON string>"}'
           configJson shapes per built-in type:
           - McpServer: {"transport":"stdio|http|sse","command":"…","args":["…"],"env":{},"url":"…"}
           - FileLocation: {"path":"/abs/path","primary":true}   (primary = the agent's working directory)
           - Rag: {"instructions":"…","path":"…?","url":"…?"}
           - TestingAction: {"command":"npm test","workingDirectory":"…?","timeoutSeconds":300}
           - Rule: {"text":"…"}
           - RuleSet: {"rules":[{"text":"…","enabled":true}]}
           - SubAgent: {"description":"…","prompt":"…","tools":"Read,Grep?","model":"…?"}
           - Reviewer: {"rubric":"what acceptable means","model":"…?","maxFixRounds":2,"escalateOnFail":true}
           - WorkspacePool: {"rootPath":"/abs/path","provisioning":"blank|git-clone|copy-template","source":"…?","retentionDays":14,"maxWorkspaces":null}
           - AzureConnection / PatToken: credential configs — create ONLY with placeholder values and say so in your report; never invent real secrets.
           - Dynamic types: type "Custom" + customTypeKey "<TypeKey>"; configJson keys = the type's field keys.
        3. Create an agent (a scheduled loop):
           curl -s -X POST {{apiUrl}}/api/agents -d '{"name":"…","description":"…","prompt":"<the loop prompt>","model":"claude-opus-5|claude-sonnet-5|claude-haiku-4-5","effort":"Low|Medium|High","intervalMinutes":60,"maxTurns":25,"maxBudgetUsd":null,"workingDirectory":null,"allowedTools":null,"bypassPermissions":true,"dryRun":true,"autonomyLevel":2,"resourceIds":["<resource ids to attach>"]}'
        4. Update an agent (e.g. to attach more resources later): PUT {{apiUrl}}/api/agents/<id> with the same body shape.
        5. If the workflow genuinely needs a capability no existing type covers, you may commission
           a new resource type (this invokes another AI and takes minutes — use sparingly):
           curl -s -X POST {{apiUrl}}/api/resource-types/generate -d '{"description":"…"}'

        GOVERNANCE — non-negotiable:
        - Every agent you create: "dryRun": true and leave it DISABLED (never call the enabled endpoint).
          The human reviews and flips the switches. Say this in your report.
        - Autonomy level 1 or 2 for new agents; promotion is earned with evidence, not granted at birth.
        - Reuse pool items where they fit; do not create near-duplicates of existing resources.
        - Write real, specific loop prompts and rubrics — a workflow of placeholder prose is worthless.
        - Give agents a Reviewer or TestingAction gate whenever their work product can be checked.

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
