using System.Text.Json;
using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Resources;

public sealed record UpdateResourceCommand(
    Guid Id, string Name, string Description, string ConfigJson) : ICommand<ResourceDto>;

public sealed class UpdateResourceValidator : AbstractValidator<UpdateResourceCommand>
{
    public UpdateResourceValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.ConfigJson).Must(BeValidJson).WithMessage("Config must be a valid JSON object.");
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

public sealed class UpdateResourceHandler(LooperDbContext db, Looper.Api.Modules.ResourceModuleRegistry registry)
    : ICommandHandler<UpdateResourceCommand, ResourceDto>
{
    public async Task<ResourceDto> Handle(UpdateResourceCommand command, CancellationToken cancellationToken)
    {
        var resource = await db.Resources
            .Where(r => r.Id == command.Id)
            .Select(r => new { Resource = r, AgentCount = r.Agents.Count })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Resource", command.Id);

        var entity = resource.Resource;
        entity.Name = command.Name.Trim();
        entity.Description = command.Description.Trim();
        entity.ConfigJson = SecretMasker.PreserveSecrets(entity, command.ConfigJson, registry);
        entity.UpdatedAtUtc = DateTime.UtcNow;

        // Re-scaffold in case the storage path changed (idempotent for graph modules).
        if (entity.Type == Looper.Api.Domain.ResourceType.Custom && entity.CustomTypeKey is not null
            && registry.TryGet(entity.CustomTypeKey, out var module))
        {
            try
            {
                module.PrepareRun(new Looper.Api.Modules.ResourceModuleContext(
                    entity.ConfigJson, null, null, entity.Id, entity.Name, entity.Description));
            }
            catch (Exception ex)
            {
                throw new FluentValidation.ValidationException(
                    $"The resource could not be prepared: {ex.Message}");
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return entity.ToDto(resource.AgentCount, registry);
    }
}

public sealed class UpdateResourceEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/api/resources/{id:guid}", (Guid id, UpdateResourceBody body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new UpdateResourceCommand(id, body.Name, body.Description, body.ConfigJson), ct));

    public sealed record UpdateResourceBody(string Name, string Description, string ConfigJson);
}
