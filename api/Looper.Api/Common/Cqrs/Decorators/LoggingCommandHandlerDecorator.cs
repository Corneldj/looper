using System.Diagnostics;

namespace Looper.Api.Common.Cqrs.Decorators;

public sealed class LoggingCommandHandlerDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    ILogger<LoggingCommandHandlerDecorator<TCommand, TResult>> logger) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var name = typeof(TCommand).Name;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await inner.Handle(command, cancellationToken);
            logger.LogInformation("Command {Command} handled in {Elapsed}ms", name, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex) when (ex is not FluentValidation.ValidationException)
        {
            logger.LogError(ex, "Command {Command} failed after {Elapsed}ms", name, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
