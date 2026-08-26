using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Delivery;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Delivery;

/// <summary>On-demand refresh of one PR (GitHub state + survival when due) from the UI.</summary>
public sealed record SyncPullRequestCommand(Guid Id) : ICommand<PullRequestDto>;

public sealed class SyncPullRequestHandler(LooperDbContext db, PullRequestSynchronizer synchronizer)
    : ICommandHandler<SyncPullRequestCommand, PullRequestDto>
{
    public async Task<PullRequestDto> Handle(SyncPullRequestCommand command, CancellationToken cancellationToken)
    {
        var row = await db.PullRequests
            .Where(pr => pr.Id == command.Id)
            .Select(pr => new { Pr = pr, AgentName = pr.Agent.Name })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Pull request", command.Id);

        await synchronizer.SyncAsync(row.Pr, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return row.Pr.ToDto(row.AgentName);
    }
}

public sealed class SyncPullRequestEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/delivery/prs/{id:guid}/sync", (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new SyncPullRequestCommand(id), ct));
}
