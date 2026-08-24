using System.Diagnostics;

namespace Looper.Api.Common.Cqrs.Decorators;

public sealed class LoggingQueryHandlerDecorator<TQuery, TResult>(
    IQueryHandler<TQuery, TResult> inner,
    ILogger<LoggingQueryHandlerDecorator<TQuery, TResult>> logger) : IQueryHandler<TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    public async Task<TResult> Handle(TQuery query, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await inner.Handle(query, cancellationToken);
        if (stopwatch.ElapsedMilliseconds > 500)
        {
            logger.LogWarning("Slow query {Query}: {Elapsed}ms", typeof(TQuery).Name, stopwatch.ElapsedMilliseconds);
        }
        return result;
    }
}
