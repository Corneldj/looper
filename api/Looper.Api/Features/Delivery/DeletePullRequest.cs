using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Delivery;

public sealed record DeletePullRequestCommand(Guid Id) : ICommand<bool>;

public sealed class DeletePullRequestHandler(LooperDbContext db)
    : ICommandHandler<DeletePullRequestCommand, bool>
{
    public async Task<bool> Handle(DeletePullRequestCommand command, CancellationToken cancellationToken)
    {
        var pr = await db.PullRequests.FirstOrDefaultAsync(p => p.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("Pull request", command.Id);
        db.PullRequests.Remove(pr);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class DeletePullRequestEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapDelete("/api/delivery/prs/{id:guid}", async (Guid id, IDispatcher dispatcher, CancellationToken ct) =>
        {
            await dispatcher.Send(new DeletePullRequestCommand(id), ct);
            return Results.NoContent();
        });
}
