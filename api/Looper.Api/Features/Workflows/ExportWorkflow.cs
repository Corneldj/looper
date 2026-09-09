using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Features.Resources;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Workflows;

public sealed record ExportWorkflowQuery(Guid Id) : IQuery<WorkflowPackage>;

/// <summary>
/// Builds the package for one workflow. Definitions only — runs, logs, metric values, deliveries,
/// user actions and memory contents are this instance's history, not the workflow.
/// Secrets are stripped and listed; the receiving side asks for them.
/// </summary>
public sealed class ExportWorkflowHandler(LooperDbContext db, ResourceModuleRegistry registry)
    : IQueryHandler<ExportWorkflowQuery, WorkflowPackage>
{
    public async Task<WorkflowPackage> Handle(ExportWorkflowQuery query, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.AsNoTracking()
            .Include(w => w.Resources)
            .Include(w => w.Agents).ThenInclude(a => a.Resources)
            .FirstOrDefaultAsync(w => w.Id == query.Id, cancellationToken)
            ?? throw new NotFoundException("Workflow", query.Id);

        // Stable, readable refs: creation order, ties broken by name.
        var resources = workflow.Resources.OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var agents = workflow.Agents.OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var refs = resources.Select((r, i) => (r.Id, Ref: $"r{i + 1}")).ToDictionary(x => x.Id, x => x.Ref);

        var packagedResources = new List<PackagedResource>();
        var redacted = new List<RedactedSecret>();
        foreach (var resource in resources)
        {
            var (json, keys) = SecretMasker.Redact(resource, registry);
            json = CheckScriptReference.ToRef(resource, json, refs);
            packagedResources.Add(new PackagedResource(refs[resource.Id], resource.Name, resource.Type, resource.CustomTypeKey, resource.Description, json));
            redacted.AddRange(keys.Select(k => new RedactedSecret(refs[resource.Id], resource.Name, k)));
        }

        var packagedAgents = agents.Select((a, i) => new PackagedAgent(
            $"a{i + 1}", a.Name, a.Description, a.Prompt, a.Model, a.Effort, a.IntervalMinutes, a.TriggerMode, a.TriggerTopics,
            a.MaxTurns, a.MaxBudgetUsd, a.WorkingDirectory, a.AllowedTools, a.BypassPermissions, a.DryRun, a.AutonomyLevel,
            a.Resources.Where(r => refs.ContainsKey(r.Id)).Select(r => refs[r.Id]).OrderBy(x => x, StringComparer.Ordinal).ToList()))
            .ToList();

        return new WorkflowPackage(
            WorkflowPackage.FormatName,
            WorkflowPackage.CurrentVersion,
            DateTime.UtcNow,
            $"Looper {typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "dev"}",
            new PackagedWorkflow(workflow.Name, workflow.Description),
            await PackageResourceTypesAsync(resources, cancellationToken),
            packagedResources,
            packagedAgents,
            redacted);
    }

    /// <summary>Every dynamic type the resources use, with its source and (when on disk) its DLL. A missing type is an error, not a gap.</summary>
    private async Task<List<PackagedResourceType>> PackageResourceTypesAsync(IEnumerable<Resource> resources, CancellationToken cancellationToken)
    {
        var keys = resources
            .Where(r => r.Type == ResourceType.Custom && !string.IsNullOrEmpty(r.CustomTypeKey))
            .Select(r => r.CustomTypeKey!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(k => !registry.IsBuiltIn(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keys.Count == 0) return [];

        var records = await db.ResourceModules.AsNoTracking()
            .Where(m => keys.Contains(m.TypeKey))
            .ToDictionaryAsync(m => m.TypeKey, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var packaged = new List<PackagedResourceType>();
        foreach (var key in keys)
        {
            if (!records.TryGetValue(key, out var record))
            {
                throw new ValidationException(
                    $"The workflow uses the resource type '{key}', which is not installed on this instance, so it cannot be packaged.");
            }
            var dllPath = Path.Combine(registry.ModulesDirectory, record.DllFileName);
            var dll = File.Exists(dllPath) ? Convert.ToBase64String(await File.ReadAllBytesAsync(dllPath, cancellationToken)) : null;
            packaged.Add(new PackagedResourceType(record.TypeKey, record.DisplayName, record.Icon, record.Blurb, record.SourceCode, dll, record.GenerationPrompt));
        }
        return packaged;
    }
}

/// <summary>Default: a <c>.workflow</c> file. <c>?format=json</c> returns the bare package for inspection or curl.</summary>
public sealed class ExportWorkflowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/workflows/{id:guid}/export", async (Guid id, string? format, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var package = await dispatcher.Query(new ExportWorkflowQuery(id), ct);
            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(package, WorkflowPackage.JsonOptions);
                return Results.File(json, "application/json", WorkflowPackage.FileNameFor(package.Workflow.Name));
            }
            return Results.File(WorkflowContainer.Pack(package), WorkflowContainer.MediaType, WorkflowContainer.FileNameFor(package.Workflow.Name));
        });
}
