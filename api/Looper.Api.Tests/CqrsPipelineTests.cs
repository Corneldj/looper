using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Cqrs.Decorators;
using Microsoft.Extensions.DependencyInjection;

namespace Looper.Api.Tests;

/// <summary>Verifies the decorator pipeline composes exactly as Program.cs wires it.</summary>
public class CqrsPipelineTests
{
    public sealed record EchoCommand(string Text) : ICommand<string>;

    public sealed class EchoHandler : ICommandHandler<EchoCommand, string>
    {
        public Task<string> Handle(EchoCommand command, CancellationToken cancellationToken) =>
            Task.FromResult($"echo:{command.Text}");
    }

    public sealed class EchoValidator : AbstractValidator<EchoCommand>
    {
        public EchoValidator()
        {
            RuleFor(c => c.Text).NotEmpty();
        }
    }

    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddScoped<ICommandHandler<EchoCommand, string>, EchoHandler>();
        services.AddScoped<IValidator<EchoCommand>, EchoValidator>();
        // Same order as Program.cs: Validation inner, Logging outer.
        services.Decorate(typeof(ICommandHandler<,>), typeof(ValidationCommandHandlerDecorator<,>));
        services.Decorate(typeof(ICommandHandler<,>), typeof(LoggingCommandHandlerDecorator<,>));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Dispatches_through_the_decorated_pipeline()
    {
        await using var provider = BuildContainer();
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.Send(new EchoCommand("hi"));

        Assert.Equal("echo:hi", result);
    }

    [Fact]
    public async Task Validation_decorator_rejects_invalid_commands()
    {
        await using var provider = BuildContainer();
        using var scope = provider.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Assert.ThrowsAsync<ValidationException>(() => dispatcher.Send(new EchoCommand("")));
    }

    [Fact]
    public void Handler_resolution_is_decorated_outermost_logging()
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<ICommandHandler<EchoCommand, string>>();

        Assert.IsType<LoggingCommandHandlerDecorator<EchoCommand, string>>(handler);
    }
}
