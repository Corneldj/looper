using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Scripts;

public sealed record ScriptRunResultDto(
    string Command, int ExitCode, bool Passed, long DurationMs, string Output);

/// <summary>
/// Runs a script on demand — a saved Script resource by id, or unsaved code straight from the
/// editor — so users can test what they (or Claude) wrote before an agent depends on it.
/// Manual runs carry LOOPER_API_URL but no agent identity and no credentials: those belong to
/// an agent, and a script sees them only when it runs as part of that agent's loop.
/// </summary>
public sealed record RunScriptCommand(
    Guid? ResourceId,
    string? Language,
    string? Code,
    string? Args,
    string? WorkingDirectory,
    int? TimeoutSeconds) : ICommand<ScriptRunResultDto>;

public sealed class RunScriptValidator : AbstractValidator<RunScriptCommand>
{
    public RunScriptValidator()
    {
        RuleFor(c => c).Must(c => c.ResourceId is not null || !string.IsNullOrWhiteSpace(c.Code))
            .WithMessage("Give either a saved script's resourceId or the code to run.");
        RuleFor(c => c.TimeoutSeconds).GreaterThan(0).When(c => c.TimeoutSeconds is not null);
    }
}

public sealed class RunScriptHandler(LooperDbContext db, ScriptRunner runner)
    : ICommandHandler<RunScriptCommand, ScriptRunResultDto>
{
    public async Task<ScriptRunResultDto> Handle(RunScriptCommand command, CancellationToken cancellationToken)
    {
        Guid resourceId;
        string name;
        ScriptConfig config;

        if (command.ResourceId is { } id)
        {
            var resource = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new NotFoundException("Resource", id);
            if (resource.Type != ResourceType.Custom ||
                !string.Equals(resource.CustomTypeKey, ScriptModule.TypeKey_, StringComparison.OrdinalIgnoreCase))
            {
                throw new ValidationException($"Resource '{resource.Name}' is not a Script.");
            }

            var stored = ScriptResources.Parse(new ResourceModuleContext(resource.ConfigJson));
            if (stored.Code.Length == 0) throw new ValidationException($"Script '{resource.Name}' has no code yet.");

            // The saved code runs; args/cwd/timeout may be overridden per call.
            resourceId = resource.Id;
            name = resource.Name;
            config = stored with
            {
                Args = command.Args?.Trim() ?? stored.Args,
                WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory) ? stored.WorkingDirectory : command.WorkingDirectory.Trim(),
                TimeoutSeconds = command.TimeoutSeconds ?? stored.TimeoutSeconds
            };
        }
        else
        {
            // Unsaved code: a throwaway materialization under a scratch id, removed afterwards.
            resourceId = Guid.NewGuid();
            name = "scratch";
            config = ScriptResources.Parse(command.Language, command.Code, null, command.Args,
                command.TimeoutSeconds, command.WorkingDirectory);
        }

        var environment = new Dictionary<string, string>
        {
            ["LOOPER_API_URL"] = runner.PublicUrl
        };
        var defaultWorkingDirectory = Path.Combine(AppContext.BaseDirectory, "workspaces", "scripts");

        try
        {
            var result = await runner.RunAsync(resourceId, name, config, defaultWorkingDirectory, environment, cancellationToken);
            return new ScriptRunResultDto(result.Command, result.ExitCode, result.Passed, result.DurationMs, result.Output);
        }
        finally
        {
            if (command.ResourceId is null) ScriptResources.RemoveMaterialized(resourceId);
        }
    }
}

public sealed class RunScriptEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/scripts/run", (RunScriptCommand command, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(command, ct));
}
