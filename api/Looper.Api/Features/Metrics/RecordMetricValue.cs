using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Metrics;

/// <summary>
/// Records one measurement. Called by agents mid-run (the Metric module injects the curl with
/// $LOOPER_RUN_ID), by the dashboard for manual entries, and by anything else that can POST.
/// The metric is addressed by id, name or slug so every reporter can use what it has.
/// </summary>
public sealed record RecordMetricValueCommand(
    string Metric,
    double Value,
    string? Note,
    Guid? RunId,
    Guid? AgentId,
    string? Source) : ICommand<MetricValueDto>;

public sealed class RecordMetricValueValidator : AbstractValidator<RecordMetricValueCommand>
{
    public RecordMetricValueValidator()
    {
        RuleFor(c => c.Metric).NotEmpty().WithMessage("Name the metric (id, name or slug).");
        RuleFor(c => c.Value).Must(double.IsFinite).WithMessage("The value must be a finite number.");
        RuleFor(c => c.Note).MaximumLength(500);
    }
}

public sealed class RecordMetricValueHandler(LooperDbContext db)
    : ICommandHandler<RecordMetricValueCommand, MetricValueDto>
{
    public async Task<MetricValueDto> Handle(RecordMetricValueCommand command, CancellationToken cancellationToken)
    {
        var metrics = await db.Resources.AsNoTracking()
            .Where(r => r.Type == ResourceType.Custom && r.CustomTypeKey == MetricModule.TypeKey_)
            .ToListAsync(cancellationToken);
        var metric = MetricResources.Match(metrics, command.Metric)
            ?? throw (Guid.TryParse(command.Metric.Trim(), out var id)
                ? new NotFoundException("Metric", id)
                : new ValidationException($"No metric named '{command.Metric.Trim()}'. Existing metrics: " +
                                          (metrics.Count == 0 ? "none" : string.Join(", ", metrics.Select(m => $"'{m.Name}'"))) + "."));

        Guid? agentId = command.AgentId;
        if (command.RunId is { } runId)
        {
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
                ?? throw new ValidationException($"Unknown run '{runId}'.");
            agentId = run.AgentId;
        }

        var source = Enum.TryParse<MetricSource>(command.Source, ignoreCase: true, out var parsed)
            ? parsed
            : command.RunId is not null ? MetricSource.Agent : MetricSource.Api;

        var value = new MetricValue
        {
            ResourceId = metric.Id,
            AgentId = agentId,
            RunId = command.RunId,
            Value = command.Value,
            Note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim(),
            Source = source
        };
        db.MetricValues.Add(value);
        await db.SaveChangesAsync(cancellationToken);

        var agentName = agentId is { } aid
            ? await db.Agents.AsNoTracking().Where(a => a.Id == aid).Select(a => a.Name).FirstOrDefaultAsync(cancellationToken)
            : null;
        return value.ToDto(agentName);
    }
}

public sealed class RecordMetricValueEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/metrics/values", async (RecordMetricValueCommand command, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(command, ct);
            return Results.Created($"/api/metrics/{dto.ResourceId}/values", dto);
        });
}
