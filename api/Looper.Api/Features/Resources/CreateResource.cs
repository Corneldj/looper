using System.Text.Json;
using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Modules;

namespace Looper.Api.Features.Resources;

public sealed record CreateResourceCommand(
    string Name, ResourceType Type, string? CustomTypeKey, string Description, string ConfigJson) : ICommand<ResourceDto>;

public sealed class CreateResourceValidator : AbstractValidator<CreateResourceCommand>
{
    public CreateResourceValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.ConfigJson).Must(BeValidJson).WithMessage("Config must be a valid JSON object.");
        RuleFor(c => c.CustomTypeKey).NotEmpty().When(c => c.Type == ResourceType.Custom)
            .WithMessage("A custom resource must name its resource type.");
    }

    private static bool BeValidJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class CreateResourceHandler(LooperDbContext db, ResourceModuleRegistry registry)
    : ICommandHandler<CreateResourceCommand, ResourceDto>
{
    public async Task<ResourceDto> Handle(CreateResourceCommand command, CancellationToken cancellationToken)
    {
        var resource = new Resource
        {
            Name = command.Name.Trim(),
            Type = command.Type,
            Description = command.Description.Trim(),
            ConfigJson = command.ConfigJson
        };

        if (command.Type == ResourceType.Custom)
        {
            if (!registry.TryGet(command.CustomTypeKey!, out var module))
            {
                throw new ValidationException($"Unknown resource type '{command.CustomTypeKey}'.");
            }
            resource.CustomTypeKey = module.TypeKey;

            // Scaffold the workspace now (graph folders seed their toolkit/protocol, scripts land
            // on disk) so the user can inspect it immediately and dry-run agents find it in place.
            // An unwritable path is a config error worth failing the save for.
            try
            {
                module.PrepareRun(new Looper.Api.Modules.ResourceModuleContext(
                    command.ConfigJson, null, null, resource.Id, resource.Name, resource.Description));
            }
            catch (Exception ex)
            {
                throw new ValidationException($"The resource's workspace could not be prepared: {ex.Message}");
            }
        }

        db.Resources.Add(resource);
        await db.SaveChangesAsync(cancellationToken);
        return resource.ToDto(agentCount: 0, registry);
    }
}

public sealed class CreateResourceEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/resources", async (CreateResourceCommand command, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(command, ct);
            return Results.Created($"/api/resources/{dto.Id}", dto);
        });
}
