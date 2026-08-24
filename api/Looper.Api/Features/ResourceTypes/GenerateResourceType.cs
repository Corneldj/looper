using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;

namespace Looper.Api.Features.ResourceTypes;

public sealed record GenerateResourceTypeCommand(string Description) : ICommand<GeneratedResourceTypeDto>;

public sealed record GeneratedResourceTypeDto(ResourceTypeDto Type, string SourceCode, decimal CostUsd);

public sealed class GenerateResourceTypeValidator : AbstractValidator<GenerateResourceTypeCommand>
{
    public GenerateResourceTypeValidator()
    {
        RuleFor(c => c.Description).NotEmpty().MinimumLength(10)
            .WithMessage("Describe what the resource type should do (at least a sentence).");
    }
}

/// <summary>
/// Claude writes the module (via the Agent SDK headless CLI), Roslyn compiles it —
/// with one self-repair round when the first attempt does not compile — and the
/// installer loads it live. New resource types appear in the picker immediately.
/// </summary>
public sealed class GenerateResourceTypeHandler(
    LooperDbContext db,
    ClaudeModuleSourceGenerator generator,
    ResourceModuleInstaller installer) : ICommandHandler<GenerateResourceTypeCommand, GeneratedResourceTypeDto>
{
    public async Task<GeneratedResourceTypeDto> Handle(GenerateResourceTypeCommand command, CancellationToken cancellationToken)
    {
        var totalCost = 0m;

        var attempt = await generator.GenerateAsync(command.Description, null, null, cancellationToken);
        totalCost += attempt.CostUsd;
        if (!attempt.Success)
        {
            throw new ValidationException(attempt.Error ?? "Generation failed.");
        }

        var source = attempt.Source!;
        var compiled = installer.TryCompile(source);
        if (!compiled.Success)
        {
            // One self-repair round: hand the compiler errors back to Claude.
            var repair = await generator.GenerateAsync(command.Description, compiled.Errors, source, cancellationToken);
            totalCost += repair.CostUsd;
            if (!repair.Success)
            {
                throw new ValidationException(repair.Error ?? "Generation failed on the repair attempt.");
            }
            source = repair.Source!;
        }

        try
        {
            var (module, _) = await installer.InstallAsync(source, command.Description, db, cancellationToken);
            return new GeneratedResourceTypeDto(module.ToDto(), source, totalCost);
        }
        catch (ModuleInstallException ex)
        {
            throw new ValidationException(
                "The generated module did not pass validation:\n" + string.Join("\n", ex.Errors));
        }
    }
}

public sealed class GenerateResourceTypeEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/resource-types/generate",
            (GenerateResourceTypeCommand command, IDispatcher dispatcher, CancellationToken ct) =>
                dispatcher.Send(command, ct));
}
