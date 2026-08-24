using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;

namespace Looper.Api.Features.ResourceTypes;

/// <summary>Power-user path: paste module source directly instead of generating it with Claude.</summary>
public sealed record CreateResourceTypeFromSourceCommand(string SourceCode) : ICommand<GeneratedResourceTypeDto>;

public sealed class CreateResourceTypeFromSourceValidator : AbstractValidator<CreateResourceTypeFromSourceCommand>
{
    public CreateResourceTypeFromSourceValidator()
    {
        RuleFor(c => c.SourceCode).NotEmpty()
            .Must(s => s.Contains("IResourceTypeModule"))
            .WithMessage("The source must implement Looper.Api.Modules.IResourceTypeModule.");
    }
}

public sealed class CreateResourceTypeFromSourceHandler(
    LooperDbContext db,
    ResourceModuleInstaller installer) : ICommandHandler<CreateResourceTypeFromSourceCommand, GeneratedResourceTypeDto>
{
    public async Task<GeneratedResourceTypeDto> Handle(
        CreateResourceTypeFromSourceCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var (module, _) = await installer.InstallAsync(command.SourceCode, null, db, cancellationToken);
            return new GeneratedResourceTypeDto(module.ToDto(), command.SourceCode, 0m);
        }
        catch (ModuleInstallException ex)
        {
            throw new ValidationException("The module did not compile:\n" + string.Join("\n", ex.Errors));
        }
    }
}

public sealed class CreateResourceTypeFromSourceEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/resource-types",
            async (CreateResourceTypeFromSourceCommand command, IDispatcher dispatcher, CancellationToken ct) =>
            {
                var dto = await dispatcher.Send(command, ct);
                return Results.Created($"/api/resource-types/{dto.Type.TypeKey}", dto);
            });
}
