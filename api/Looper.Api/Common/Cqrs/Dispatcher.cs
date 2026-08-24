using System.Collections.Concurrent;
using System.Reflection;

namespace Looper.Api.Common.Cqrs;

public interface IDispatcher
{
    Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);
    Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the decorated handler pipeline for a message from the container and invokes it.
/// Handler MethodInfos are cached per message type; the decorator chain itself is composed by DI (Scrutor).
/// </summary>
public sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, (Type HandlerType, MethodInfo Handle)> Cache = new();

    public Task<TResult> Send<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default) =>
        Invoke<TResult>(typeof(ICommandHandler<,>), command, cancellationToken);

    public Task<TResult> Query<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default) =>
        Invoke<TResult>(typeof(IQueryHandler<,>), query, cancellationToken);

    private Task<TResult> Invoke<TResult>(Type openHandlerType, object message, CancellationToken cancellationToken)
    {
        var (handlerType, handle) = Cache.GetOrAdd(message.GetType(), messageType =>
        {
            var closed = openHandlerType.MakeGenericType(messageType, typeof(TResult));
            return (closed, closed.GetMethod("Handle")!);
        });

        var handler = services.GetRequiredService(handlerType);
        return (Task<TResult>)handle.Invoke(handler, [message, cancellationToken])!;
    }
}
