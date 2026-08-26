using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Delivery;

/// <summary>
/// Manual maintenance for PRs that GitHub sync can't cover (local-only repos, other forges):
/// status transitions, review churn and human-rework counts, size, and the survival inputs.
/// </summary>
public sealed record UpdatePullRequestCommand(
    Guid Id,
    string Title,
    PrStatus Status,
    int Additions,
    int Deletions,
    int ReviewRounds,
    int ReviewComments,
    int HumanCommits,
    string? RepoPath,
    string? MergeCommitSha) : ICommand<PullRequestDto>;

public sealed class UpdatePullRequestValidator : AbstractValidator<UpdatePullRequestCommand>
{
    public UpdatePullRequestValidator()
    {
        RuleFor(c => c.Title).NotEmpty().MaximumLength(300);
        RuleFor(c => c.Additions).GreaterThanOrEqualTo(0);
        RuleFor(c => c.Deletions).GreaterThanOrEqualTo(0);
        RuleFor(c => c.ReviewRounds).GreaterThanOrEqualTo(0);
        RuleFor(c => c.ReviewComments).GreaterThanOrEqualTo(0);
        RuleFor(c => c.HumanCommits).GreaterThanOrEqualTo(0);
    }
}

public sealed class UpdatePullRequestHandler(LooperDbContext db)
    : ICommandHandler<UpdatePullRequestCommand, PullRequestDto>
{
    public async Task<PullRequestDto> Handle(UpdatePullRequestCommand command, CancellationToken cancellationToken)
    {
        var row = await db.PullRequests
            .Where(pr => pr.Id == command.Id)
            .Select(pr => new { Pr = pr, AgentName = pr.Agent.Name })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Pull request", command.Id);

        var pr = row.Pr;
        pr.Title = command.Title.Trim();
        pr.Additions = command.Additions;
        pr.Deletions = command.Deletions;
        pr.ReviewRounds = command.ReviewRounds;
        pr.ReviewComments = command.ReviewComments;
        pr.HumanCommits = command.HumanCommits;
        pr.RepoPath = string.IsNullOrWhiteSpace(command.RepoPath) ? null : command.RepoPath.Trim();
        pr.MergeCommitSha = string.IsNullOrWhiteSpace(command.MergeCommitSha) ? null : command.MergeCommitSha.Trim();

        if (pr.Status != command.Status)
        {
            pr.Status = command.Status;
            pr.MergedAtUtc = command.Status == PrStatus.Merged ? pr.MergedAtUtc ?? DateTime.UtcNow : pr.MergedAtUtc;
            pr.ClosedAtUtc = command.Status == PrStatus.Closed ? pr.ClosedAtUtc ?? DateTime.UtcNow : pr.ClosedAtUtc;
            if (command.Status == PrStatus.Open)
            {
                pr.MergedAtUtc = null;
                pr.ClosedAtUtc = null;
            }
        }

        pr.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return pr.ToDto(row.AgentName);
    }
}

public sealed class UpdatePullRequestEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPut("/api/delivery/prs/{id:guid}", (Guid id, UpdatePullRequestBody body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new UpdatePullRequestCommand(
                id, body.Title, body.Status, body.Additions, body.Deletions,
                body.ReviewRounds, body.ReviewComments, body.HumanCommits,
                body.RepoPath, body.MergeCommitSha), ct));

    public sealed record UpdatePullRequestBody(
        string Title, PrStatus Status, int Additions, int Deletions,
        int ReviewRounds, int ReviewComments, int HumanCommits,
        string? RepoPath, string? MergeCommitSha);
}
