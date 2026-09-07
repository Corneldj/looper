using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Infrastructure.Execution;

namespace Looper.Api.Features.Scripts;

public sealed record ScriptAssistResultDto(string Code, string Summary, decimal CostUsd);

/// <summary>
/// The agent inside the script editor: hands the current script and an instruction to Claude,
/// who edits the file in a scratch workspace (optionally running it to verify) — and the API
/// reads the resulting file back. The result is a proposal: nothing is saved until the user
/// clicks save in the editor.
/// </summary>
public sealed record AssistScriptCommand(
    string Name,
    string? Description,
    string? Language,
    string? Code,
    string Instruction,
    bool AllowRun) : ICommand<ScriptAssistResultDto>;

public sealed class AssistScriptValidator : AbstractValidator<AssistScriptCommand>
{
    public AssistScriptValidator()
    {
        RuleFor(c => c.Instruction).NotEmpty().MinimumLength(5)
            .WithMessage("Tell Claude what the script should do (at least a few words).");
        RuleFor(c => c.Name).MaximumLength(200);
    }
}

public sealed class AssistScriptHandler(ScriptAssistant assistant)
    : ICommandHandler<AssistScriptCommand, ScriptAssistResultDto>
{
    public async Task<ScriptAssistResultDto> Handle(AssistScriptCommand command, CancellationToken cancellationToken)
    {
        var result = await assistant.AssistAsync(
            string.IsNullOrWhiteSpace(command.Name) ? "script" : command.Name.Trim(),
            command.Description ?? "",
            Modules.BuiltIn.ScriptResources.ParseLanguage(command.Language),
            command.Code ?? "",
            command.Instruction.Trim(),
            command.AllowRun,
            cancellationToken);

        if (!result.Success)
        {
            throw new ValidationException(result.Error ?? "Claude could not update the script.");
        }
        return new ScriptAssistResultDto(result.Code!, result.Summary ?? "", result.CostUsd);
    }
}

public sealed class AssistScriptEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/scripts/assist", (AssistScriptCommand command, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(command, ct));
}
