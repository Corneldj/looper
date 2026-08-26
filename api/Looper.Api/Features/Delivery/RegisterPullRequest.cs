using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Delivery;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Delivery;

/// <summary>
/// Registers a PR an agent produced. Called by the agent itself mid-run (the executor injects
/// the curl protocol with $LOOPER_RUN_ID) or from the UI for manual logging. Upserts by URL so
/// an agent re-reporting the same PR updates rather than duplicates.
/// </summary>
public sealed record RegisterPullRequestCommand(
    Guid? RunId,
    Guid? AgentId,
    string? Url,
    string? Title,
    string? RepoPath,
    string? Repository) : ICommand<PullRequestDto>;

public sealed class RegisterPullRequestValidator : AbstractValidator<RegisterPullRequestCommand>
{
    public RegisterPullRequestValidator()
    {
        RuleFor(c => c).Must(c => c.RunId is not null || c.AgentId is not null)
            .WithMessage("Provide runId (preferred — the executor injects $LOOPER_RUN_ID) or agentId.");
        RuleFor(c => c).Must(c => !string.IsNullOrWhiteSpace(c.Url) || !string.IsNullOrWhiteSpace(c.Title))
            .WithMessage("Provide the PR's url, or at least a title for a PR without one.");
    }
}

public sealed class RegisterPullRequestHandler(LooperDbContext db)
    : ICommandHandler<RegisterPullRequestCommand, PullRequestDto>
{
    public async Task<PullRequestDto> Handle(RegisterPullRequestCommand command, CancellationToken cancellationToken)
    {
        Guid agentId;
        Guid? runId = command.RunId;
        if (runId is not null)
        {
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
                ?? throw new ValidationException($"Unknown run '{runId}'.");
            agentId = run.AgentId;
        }
        else
        {
            agentId = command.AgentId!.Value;
        }

        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new ValidationException($"Unknown agent '{agentId}'.");

        var url = string.IsNullOrWhiteSpace(command.Url) ? null : command.Url.Trim();
        var existing = url is null
            ? null
            : await db.PullRequests.FirstOrDefaultAsync(pr => pr.Url == url, cancellationToken);

        var repository = command.Repository?.Trim()
            ?? (GitHubPrInspector.ParseUrl(url) is ({ } owner, { } repo, _) ? $"{owner}/{repo}" : "");

        if (existing is not null)
        {
            if (!string.IsNullOrWhiteSpace(command.Title)) existing.Title = command.Title.Trim();
            existing.RepoPath ??= NormalizePath(command.RepoPath);
            existing.RunId ??= runId;
            if (repository.Length > 0) existing.Repository = repository;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return existing.ToDto(agent.Name);
        }

        var pr = new AgentPullRequest
        {
            AgentId = agentId,
            RunId = runId,
            Title = command.Title?.Trim() ?? url ?? "",
            Url = url,
            Repository = repository,
            Number = GitHubPrInspector.ParseUrl(url)?.Number,
            RepoPath = NormalizePath(command.RepoPath)
        };
        db.PullRequests.Add(pr);
        await db.SaveChangesAsync(cancellationToken);
        return pr.ToDto(agent.Name);
    }

    private static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(path.Trim());
}

public sealed class RegisterPullRequestEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/delivery/prs", async (RegisterPullRequestCommand command, IDispatcher dispatcher, CancellationToken ct) =>
        {
            var dto = await dispatcher.Send(command, ct);
            return Results.Created($"/api/delivery/prs/{dto.Id}", dto);
        });
}
