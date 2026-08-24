namespace Looper.Api.Common.Cqrs;

/// <summary>Marker for a state-changing operation handled by exactly one <see cref="ICommandHandler{TCommand,TResult}"/>.</summary>
public interface ICommand<TResult>;

/// <summary>Marker for a read-only operation handled by exactly one <see cref="IQueryHandler{TQuery,TResult}"/>.</summary>
public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<TResult> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}
