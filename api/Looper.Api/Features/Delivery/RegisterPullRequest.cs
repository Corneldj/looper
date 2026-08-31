using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Delivery;
using Looper.Api.Modules.BuiltIn;
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
    string? Repository,
    string? Satisfies = null) : ICommand<PullRequestDto>;

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

        var agent = await db.Agents.AsNoTracking()
                .Include(a => a.Resources)
                .FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new ValidationException($"Unknown agent '{agentId}'.");

        var satisfiesAcs = VerifyCitations(agent, command.Satisfies);

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
            if (satisfiesAcs is not null) existing.SatisfiesAcs = MergeCitations(existing.SatisfiesAcs, satisfiesAcs);
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
            RepoPath = NormalizePath(command.RepoPath),
            SatisfiesAcs = satisfiesAcs
        };
        db.PullRequests.Add(pr);
        await db.SaveChangesAsync(cancellationToken);
        return pr.ToDto(agent.Name);
    }

    /// <summary>
    /// The traceability gate. An agent working under a Specification must cite the acceptance
    /// criteria its PR satisfies, and only criteria the spec actually declares — verified here,
    /// at the harness level, where the worker can't route around it. Returns the normalized
    /// citation string, or null when nothing was cited (allowed only without an enforced spec).
    /// </summary>
    private static string? VerifyCitations(LoopAgent agent, string? satisfies)
    {
        var specs = SpecificationTraceability.AttachedSpecs(agent.Resources);
        var cited = SpecificationTraceability.ParseCitations(satisfies);

        if (specs.Count == 0)
        {
            return cited.Count > 0 ? string.Join(", ", cited) : null;
        }

        var known = specs.SelectMany(SpecificationTraceability.AcceptanceCriteria)
            .Distinct()
            .ToList();
        var specIds = string.Join(", ", specs.Select(s => s.SpecId).Where(id => id.Length > 0));
        var enforced = specs.Any(s => !s.Advisory);

        if (cited.Count == 0)
        {
            if (!enforced) return null;
            throw new ValidationException(
                $"This agent works under specification {specIds} — cite the acceptance criteria this PR satisfies " +
                $"(\"satisfies\":\"AC-1,AC-3\"). Declared criteria: {(known.Count > 0 ? string.Join(", ", known) : "none — the spec declares no AC identifiers; fix the spec first")}.");
        }

        var unknown = cited.Where(ac => !known.Contains(ac)).ToList();
        if (unknown.Count > 0)
        {
            throw new ValidationException(
                $"Cited acceptance criteria not declared by specification {specIds}: {string.Join(", ", unknown)}. " +
                $"Declared criteria: {(known.Count > 0 ? string.Join(", ", known) : "none")}. " +
                "Cite only criteria the spec declares; if your work matches none, raise the spec gap instead.");
        }

        return string.Join(", ", cited);
    }

    private static string MergeCitations(string? existing, string incoming)
    {
        var merged = SpecificationTraceability.ParseCitations(existing)
            .Concat(SpecificationTraceability.ParseCitations(incoming))
            .Distinct();
        return string.Join(", ", merged);
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
