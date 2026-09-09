using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Features.Agents;
using Looper.Api.Features.Resources;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Workflows;

public sealed record ImportWorkflowRequest(WorkflowPackage Package, string? Name = null);

public sealed record WorkflowImportResultDto(
    WorkflowDto Workflow,
    List<string> ResourceTypesInstalled,
    List<string> ResourceTypesReused,
    int ResourcesCreated,
    int AgentsCreated,
    /// <summary>Things the importing user must look at: secrets to fill in, paths that do not exist here, agents left paused.</summary>
    List<string> Warnings);

public sealed record ImportWorkflowCommand(WorkflowPackage Package, string? Name) : ICommand<WorkflowImportResultDto>;

public sealed class ImportWorkflowValidator : AbstractValidator<ImportWorkflowCommand>
{
    public ImportWorkflowValidator()
    {
        RuleFor(c => c.Package).NotNull().WithMessage("Send a workflow package.");
        When(c => c.Package is not null, () =>
        {
            RuleFor(c => c.Package.Format).Equal(WorkflowPackage.FormatName)
                .WithMessage($"This is not a Looper workflow package (expected format '{WorkflowPackage.FormatName}').");
            RuleFor(c => c.Package.Version).InclusiveBetween(1, WorkflowPackage.CurrentVersion)
                .WithMessage(c => $"Package version {c.Package.Version} is newer than this Looper understands (up to {WorkflowPackage.CurrentVersion}). Update Looper.");
            RuleFor(c => c.Package.Workflow).NotNull().WithMessage("The package has no workflow section.");
            RuleFor(c => c).Must(c => !string.IsNullOrWhiteSpace(c.Name) || !string.IsNullOrWhiteSpace(c.Package.Workflow?.Name))
                .WithMessage("The workflow needs a name.");
            RuleFor(c => c.Name).MaximumLength(200);
            RuleFor(c => c.Package.Resources).NotNull();
            RuleFor(c => c.Package.Agents).NotNull();
            RuleFor(c => c.Package.ResourceTypes).NotNull();
            RuleFor(c => c.Package).Must(HaveUniqueRefs).WithMessage("Every resource and agent in the package needs a unique, non-empty ref.");
            RuleFor(c => c.Package).Must(ResolveAgentResources).WithMessage("An agent references a resource ref that is not in the package.");
            RuleFor(c => c.Package).Must(ResolveCheckScripts).WithMessage("A check runs a script ref that is not in the package.");
            RuleForEach(c => c.Package.Resources).Must(r => !string.IsNullOrWhiteSpace(r.Name) && r.Name.Length <= 200)
                .WithMessage("Every packaged resource needs a name (up to 200 characters).");
            RuleForEach(c => c.Package.Resources).Must(r => r.Type != ResourceType.Custom || !string.IsNullOrWhiteSpace(r.CustomTypeKey))
                .WithMessage("A packaged custom resource must name its resource type.");
            RuleForEach(c => c.Package.ResourceTypes).Must(t => !string.IsNullOrWhiteSpace(t.TypeKey) && !string.IsNullOrWhiteSpace(t.SourceCode))
                .WithMessage("Every packaged resource type needs a typeKey and its source code.");
        });
    }

    private static bool HaveUniqueRefs(WorkflowPackage package)
    {
        if (package.Resources is null || package.Agents is null) return true;
        var refs = package.Resources.Select(r => r.Ref).Concat(package.Agents.Select(a => a.Ref)).ToList();
        return refs.All(r => !string.IsNullOrWhiteSpace(r)) && refs.Distinct(StringComparer.Ordinal).Count() == refs.Count;
    }

    private static bool ResolveCheckScripts(WorkflowPackage package)
    {
        if (package.Resources is null) return true;
        var known = package.Resources.Select(r => r.Ref).ToHashSet(StringComparer.Ordinal);
        return package.Resources.All(r => CheckScriptReference.RefOf(r) is not { } scriptRef || known.Contains(scriptRef));
    }

    private static bool ResolveAgentResources(WorkflowPackage package)
    {
        if (package.Resources is null || package.Agents is null) return true;
        var known = package.Resources.Select(r => r.Ref).ToHashSet(StringComparer.Ordinal);
        return package.Agents.All(a => (a.ResourceRefs ?? []).All(known.Contains));
    }
}

/// <summary>
/// Imports a package as a NEW workflow, all or nothing: resource types first (each install is
/// atomic), then the workflow, resources and agents inside one database transaction that rolls
/// back on any failure — and types installed for this import are removed again. Resources and
/// agents go through the same commands the UI uses, so every validation and module PrepareRun
/// applies; agents therefore start paused, exactly like agents created by hand.
/// </summary>
public sealed class ImportWorkflowHandler(
    LooperDbContext db,
    ResourceModuleRegistry registry,
    ResourceModuleInstaller installer,
    IDispatcher dispatcher,
    ILogger<ImportWorkflowHandler> logger) : ICommandHandler<ImportWorkflowCommand, WorkflowImportResultDto>
{
    public async Task<WorkflowImportResultDto> Handle(ImportWorkflowCommand command, CancellationToken cancellationToken)
    {
        var installed = new List<string>();
        var reused = new List<string>();
        var warnings = new List<string>();
        // Packages from before the Event Listener type was retired: fold listeners into the agents' triggers.
        var package = LegacyEventListeners.Retire(command.Package, warnings);

        await ResolveResourceTypesAsync(package, installed, reused, warnings, cancellationToken);

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var workflow = new Workflow
            {
                Name = (string.IsNullOrWhiteSpace(command.Name) ? package.Workflow.Name : command.Name).Trim(),
                Description = (package.Workflow.Description ?? "").Trim()
            };
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync(cancellationToken);

            var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var resource in package.Resources)
            {
                var created = await dispatcher.Send(new CreateResourceCommand(
                    resource.Name, resource.Type, resource.CustomTypeKey, resource.Description ?? "", resource.ConfigJson ?? "{}", workflow.Id),
                    cancellationToken);
                ids[resource.Ref] = created.Id;
                foreach (var missing in WorkflowPortability.MissingPaths(resource, registry))
                {
                    warnings.Add($"Resource '{resource.Name}' points at '{missing}', which does not exist on this machine.");
                }
            }

            // Checks that run a packaged script: now that every resource has an id, point them at the new one.
            foreach (var check in package.Resources)
            {
                if (CheckScriptReference.RefOf(check) is not { } scriptRef) continue;
                var row = await db.Resources.FirstAsync(r => r.Id == ids[check.Ref], cancellationToken);
                row.ConfigJson = CheckScriptReference.ToId(row.ConfigJson, ids[scriptRef]);
            }
            await db.SaveChangesAsync(cancellationToken);

            foreach (var agent in package.Agents)
            {
                await dispatcher.Send(new CreateAgentCommand(new SaveAgentRequest(
                    agent.Name, agent.Description ?? "", agent.Prompt, agent.Model, agent.Effort, agent.IntervalMinutes,
                    agent.TriggerMode, agent.TriggerTopics, agent.MaxTurns, agent.MaxBudgetUsd, agent.WorkingDirectory,
                    agent.AllowedTools, agent.BypassPermissions, agent.DryRun, agent.AutonomyLevel,
                    (agent.ResourceRefs ?? []).Select(r => ids[r]).ToList(), workflow.Id)), cancellationToken);
                if (!string.IsNullOrWhiteSpace(agent.WorkingDirectory) && !Directory.Exists(agent.WorkingDirectory))
                {
                    warnings.Add($"Agent '{agent.Name}' has working directory '{agent.WorkingDirectory}', which does not exist on this machine.");
                }
            }

            await transaction.CommitAsync(cancellationToken);

            foreach (var secret in package.RedactedSecrets ?? [])
            {
                warnings.Add($"Resource '{secret.ResourceName}' needs its '{secret.Field}' filled in — secrets are not exported.");
            }
            if (package.Agents.Count > 0)
            {
                warnings.Add($"{package.Agents.Count} agent{(package.Agents.Count == 1 ? " is" : "s are")} paused; review and switch on the ones you want running.");
            }

            logger.LogInformation("Imported workflow {Name}: {Resources} resources, {Agents} agents, types installed: {Installed}",
                workflow.Name, package.Resources.Count, package.Agents.Count, string.Join(", ", installed));
            return new WorkflowImportResultDto(
                workflow.ToDto(package.Agents.Count, package.Resources.Count),
                installed, reused, package.Resources.Count, package.Agents.Count, warnings);
        }
        catch
        {
            // The transaction disposed without a commit rolled the rows back; undo the type installs too.
            foreach (var typeKey in installed)
            {
                try { await installer.UninstallAsync(typeKey, db, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not remove resource type {TypeKey} after a failed import", typeKey); }
            }
            throw;
        }
    }

    /// <summary>
    /// A packaged type is reused when this instance has the same one (built-in, or dynamic with the
    /// same source), installed when it is missing, and refused when a different type already owns
    /// the key — silently mapping configs onto different fields is how imports go wrong quietly.
    /// </summary>
    private async Task ResolveResourceTypesAsync(WorkflowPackage package, List<string> installed, List<string> reused,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var packagedKeys = package.ResourceTypes.Select(t => t.TypeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in package.Resources.Where(r => r.Type == ResourceType.Custom))
        {
            if (!packagedKeys.Contains(resource.CustomTypeKey!) && !registry.TryGet(resource.CustomTypeKey!, out _))
            {
                throw new ValidationException(
                    $"Resource '{resource.Name}' needs the resource type '{resource.CustomTypeKey}', which is neither in the package nor installed here.");
            }
        }

        foreach (var packaged in package.ResourceTypes)
        {
            if (registry.IsBuiltIn(packaged.TypeKey))
            {
                reused.Add(packaged.TypeKey);
                warnings.Add($"Resource type '{packaged.TypeKey}' is built into this Looper; the packaged module was not installed.");
                continue;
            }

            if (registry.TryGet(packaged.TypeKey, out _))
            {
                var existing = await db.ResourceModules.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.TypeKey == packaged.TypeKey, cancellationToken);
                if (existing is not null && SameSource(existing.SourceCode, packaged.SourceCode))
                {
                    reused.Add(packaged.TypeKey);
                    continue;
                }
                throw new ValidationException(
                    $"A resource type '{packaged.TypeKey}' already exists here with different code. Delete it (if nothing uses it) or rename the packaged type, then import again.");
            }

            try
            {
                await installer.InstallAsync(packaged.SourceCode, packaged.GenerationPrompt, db, cancellationToken);
                installed.Add(packaged.TypeKey);
            }
            catch (ModuleInstallException compileFailure)
            {
                if (string.IsNullOrEmpty(packaged.DllBase64))
                {
                    await RollBackInstalledAsync(installed);
                    throw new ValidationException(
                        $"Resource type '{packaged.TypeKey}' did not compile here and the package carries no DLL:\n{string.Join("\n", compileFailure.Errors)}");
                }
                try
                {
                    await installer.InstallPrebuiltAsync(packaged.TypeKey, Convert.FromBase64String(packaged.DllBase64),
                        packaged.SourceCode, packaged.GenerationPrompt, db, cancellationToken);
                    installed.Add(packaged.TypeKey);
                    warnings.Add($"Resource type '{packaged.TypeKey}' was installed from the packaged DLL because its source did not compile here: {compileFailure.Errors.FirstOrDefault()}");
                }
                catch (Exception dllFailure) when (dllFailure is ModuleInstallException or FormatException)
                {
                    await RollBackInstalledAsync(installed);
                    throw new ValidationException(
                        $"Resource type '{packaged.TypeKey}' could not be installed — source: {compileFailure.Errors.FirstOrDefault()}; DLL: {dllFailure.Message}");
                }
            }
        }
    }

    private async Task RollBackInstalledAsync(List<string> installed)
    {
        foreach (var typeKey in installed)
        {
            try { await installer.UninstallAsync(typeKey, db, CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not remove resource type {TypeKey} after a failed import", typeKey); }
        }
        installed.Clear();
    }

    private static bool SameSource(string a, string b) =>
        string.Equals(a.Replace("\r\n", "\n").Trim(), b.Replace("\r\n", "\n").Trim(), StringComparison.Ordinal);
}

/// <summary>What a <c>.workflow</c> file holds, shown before anything is created.</summary>
public sealed record WorkflowPackageSummaryDto(
    string Name,
    string Description,
    string? ExportedFrom,
    DateTime ExportedAtUtc,
    int Resources,
    int Agents,
    List<PackagedTypeSummaryDto> ResourceTypes,
    List<RedactedSecret> RedactedSecrets);

public sealed record PackagedTypeSummaryDto(string TypeKey, string DisplayName, string Icon, bool HasDll);

public static class WorkflowPackageSummary
{
    public static WorkflowPackageSummaryDto Of(WorkflowPackage package) => new(
        package.Workflow.Name,
        package.Workflow.Description ?? "",
        package.ExportedFrom,
        package.ExportedAtUtc,
        package.Resources?.Count ?? 0,
        package.Agents?.Count ?? 0,
        (package.ResourceTypes ?? []).Select(t => new PackagedTypeSummaryDto(t.TypeKey, t.DisplayName, t.Icon, !string.IsNullOrEmpty(t.DllBase64))).ToList(),
        package.RedactedSecrets ?? []);
}

public sealed class ImportWorkflowEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        // The bare package as JSON — for curl and the architect.
        app.MapPost("/api/workflows/import", async (ImportWorkflowRequest body, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var result = await dispatcher.Send(new ImportWorkflowCommand(body.Package, body.Name), ct);
            return Results.Created($"/api/workflows/{result.Workflow.Id}", result);
        });

        // A .workflow file, as the UI sends it: multipart "file" plus an optional "name".
        app.MapPost("/api/workflows/import/file", async (IFormFile file, [FromForm] string? name, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var package = await UnpackAsync(file, ct);
            var result = await dispatcher.Send(new ImportWorkflowCommand(package, name), ct);
            return Results.Created($"/api/workflows/{result.Workflow.Id}", result);
        }).DisableAntiforgery();

        // Look inside a .workflow file without creating anything — the import preview.
        app.MapPost("/api/workflows/import/inspect", async (IFormFile file, CancellationToken ct) =>
            Results.Ok(WorkflowPackageSummary.Of(await UnpackAsync(file, ct)))).DisableAntiforgery();
    }

    private static async Task<WorkflowPackage> UnpackAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0) throw new ValidationException("The uploaded file is empty.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        try
        {
            return WorkflowContainer.Unpack(buffer.ToArray());
        }
        catch (WorkflowContainerException ex)
        {
            throw new ValidationException(ex.Message);
        }
    }
}
