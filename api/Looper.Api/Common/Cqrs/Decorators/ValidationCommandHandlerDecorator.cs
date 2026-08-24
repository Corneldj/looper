using FluentValidation;

namespace Looper.Api.Common.Cqrs.Decorators;

/// <summary>
/// Runs every registered <see cref="IValidator{T}"/> for the command before the inner handler.
/// Throws <see cref="ValidationException"/> (mapped to HTTP 400 by the exception middleware) on failure.
/// </summary>
public sealed class ValidationCommandHandlerDecorator<TCommand, TResult>(
    ICommandHandler<TCommand, TResult> inner,
    IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    public async Task<TResult> Handle(TCommand command, CancellationToken cancellationToken)
    {
        var failures = new List<FluentValidation.Results.ValidationFailure>();
        foreach (var validator in validators)
        {
            var result = await validator.ValidateAsync(command, cancellationToken);
            if (!result.IsValid) failures.AddRange(result.Errors);
        }

        if (failures.Count > 0) throw new ValidationException(failures);

        return await inner.Handle(command, cancellationToken);
    }
}
