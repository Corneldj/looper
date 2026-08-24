using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure.Execution;

namespace Looper.Api.Features.SystemStatus;

/// <summary>
/// Runs the official Claude Code installer (Looper:ClaudeInstallCommand) on the machine
/// hosting the API. No user input reaches the shell; the command is fixed configuration.
/// </summary>
public sealed record InstallClaudeCommand : ICommand<ClaudeInstallResultDto>;

public sealed record ClaudeInstallResultDto(bool Success, string Output, ClaudeStatusDto Status);

public sealed class InstallClaudeHandler(ClaudeCliStatusService statusService)
    : ICommandHandler<InstallClaudeCommand, ClaudeInstallResultDto>
{
    public async Task<ClaudeInstallResultDto> Handle(InstallClaudeCommand command, CancellationToken cancellationToken)
    {
        var (success, output, status) = await statusService.InstallAsync(cancellationToken);
        return new ClaudeInstallResultDto(
            success,
            output,
            new ClaudeStatusDto(status.Available, status.Version, status.Command, status.Error));
    }
}

public sealed class InstallClaudeEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/system/install-claude", (IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new InstallClaudeCommand(), ct));
}
