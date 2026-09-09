using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;

namespace Looper.Api.Features.ResourceTypes;

public sealed record GeneratedResourceTypeDto(ResourceTypeDto Type, string SourceCode, decimal CostUsd);

// ============================================================================
// Two ways in, one job loop: the UI starts a job and polls it (so it can show
// attempts and cancel); the architect and scripts call the synchronous endpoint,
// which starts the same job and waits — and cancels it if the caller disconnects.
// ============================================================================

public sealed record GenerateResourceTypeCommand(string Description) : ICommand<GeneratedResourceTypeDto>;

public sealed class GenerateResourceTypeValidator : AbstractValidator<GenerateResourceTypeCommand>
{
    public GenerateResourceTypeValidator()
    {
        RuleFor(c => c.Description).NotEmpty().MinimumLength(10)
            .WithMessage("Describe what the resource type should do (at least a sentence).");
    }
}

public sealed class GenerateResourceTypeHandler(ResourceTypeGenerationJobs jobs)
    : ICommandHandler<GenerateResourceTypeCommand, GeneratedResourceTypeDto>
{
    public async Task<GeneratedResourceTypeDto> Handle(GenerateResourceTypeCommand command, CancellationToken cancellationToken)
    {
        var job = await jobs.WaitAsync(jobs.Start(command.Description), cancellationToken);
        return job.Phase == GenerationPhase.Installed && job.Result is not null
            ? job.Result
            : throw new ValidationException(job.Error ?? "Generation failed.");
    }
}

public sealed class GenerateResourceTypeEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/resource-types/generate",
            (GenerateResourceTypeCommand command, IDispatcher dispatcher, CancellationToken ct) =>
                dispatcher.Send(command, ct));
}

// ---------- jobs: start / poll / cancel ----------

public sealed record StartGenerationJobCommand(string Description) : ICommand<GenerationJobDto>;

public sealed class StartGenerationJobValidator : AbstractValidator<StartGenerationJobCommand>
{
    public StartGenerationJobValidator()
    {
        RuleFor(c => c.Description).NotEmpty().MinimumLength(10)
            .WithMessage("Describe what the resource type should do (at least a sentence).");
    }
}

public sealed class StartGenerationJobHandler(ResourceTypeGenerationJobs jobs) : ICommandHandler<StartGenerationJobCommand, GenerationJobDto>
{
    public Task<GenerationJobDto> Handle(StartGenerationJobCommand command, CancellationToken cancellationToken) =>
        Task.FromResult(jobs.Start(command.Description).ToDto());
}

public sealed record GetGenerationJobQuery(Guid Id) : IQuery<GenerationJobDto>;

public sealed class GetGenerationJobHandler(ResourceTypeGenerationJobs jobs) : IQueryHandler<GetGenerationJobQuery, GenerationJobDto>
{
    public Task<GenerationJobDto> Handle(GetGenerationJobQuery query, CancellationToken cancellationToken) =>
        jobs.TryGet(query.Id, out var job)
            ? Task.FromResult(job.ToDto())
            : throw new NotFoundException("Generation job", query.Id);
}

public sealed record CancelGenerationJobCommand(Guid Id) : ICommand<GenerationJobDto>;

public sealed class CancelGenerationJobHandler(ResourceTypeGenerationJobs jobs) : ICommandHandler<CancelGenerationJobCommand, GenerationJobDto>
{
    public async Task<GenerationJobDto> Handle(CancelGenerationJobCommand command, CancellationToken cancellationToken)
    {
        if (!jobs.TryGet(command.Id, out var job)) throw new NotFoundException("Generation job", command.Id);
        if (jobs.Cancel(command.Id))
        {
            // Give the loop a moment to observe the cancellation so the caller sees the terminal state.
            await Task.WhenAny(job.Completion.Task, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
        }
        return job.ToDto();
    }
}

public sealed class GenerationJobEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/resource-types/generate/jobs", async (StartGenerationJobCommand command, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(command, ct);
            return Results.Accepted($"/api/resource-types/generate/jobs/{dto.Id}", dto);
        });

        app.MapGet("/api/resource-types/generate/jobs/{id:guid}", (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetGenerationJobQuery(id), ct));

        app.MapDelete("/api/resource-types/generate/jobs/{id:guid}", (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new CancelGenerationJobCommand(id), ct));
    }
}
