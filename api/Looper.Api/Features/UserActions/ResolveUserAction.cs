using System.Text.Json;
using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.UserActions;

/// <summary>
/// The human completes the request. Every iteration starts from a clean context, so the
/// answer is never smuggled into the next prompt as a message: it becomes a deterministic
/// edit to a resource the agent loads every run — a standing rule in its own decisions
/// rule set, or an item in its memory graph's inbox — or it is recorded nowhere at all.
/// Resolving unparks the schedule; the next iteration fires immediately when the agent is enabled.
/// </summary>
public sealed record ResolveUserActionCommand(Guid Id, string? Response, string? RecordAs = null) : ICommand<UserActionDto>;

public static class RecordAs
{
    public const string Rule = "rule";
    public const string Memory = "memory";
    public const string None = "none";

    /// <summary>An answer is a rule unless told otherwise; no answer means nothing to record.</summary>
    public static string Normalize(string? recordAs, string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return None;
        return recordAs?.Trim().ToLowerInvariant() switch
        {
            Memory => Memory,
            None => None,
            _ => Rule
        };
    }
}

public sealed class ResolveUserActionValidator : AbstractValidator<ResolveUserActionCommand>
{
    public ResolveUserActionValidator()
    {
        RuleFor(c => c.Response).MaximumLength(20_000);
        RuleFor(c => c.RecordAs)
            .Must(r => r is null || new[] { RecordAs.Rule, RecordAs.Memory, RecordAs.None }.Contains(r.Trim().ToLowerInvariant()))
            .WithMessage("recordAs must be 'rule', 'memory' or 'none'.");
    }
}

public sealed class ResolveUserActionHandler(LooperDbContext db)
    : ICommandHandler<ResolveUserActionCommand, UserActionDto>
{
    // Relaxed escaping keeps the stored rule readable ("user's", not "user\u0027s").
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<UserActionDto> Handle(ResolveUserActionCommand command, CancellationToken cancellationToken)
    {
        var request = await db.UserActionRequests
            .Include(r => r.Agent).ThenInclude(a => a.Resources)
            .FirstOrDefaultAsync(r => r.Id == command.Id, cancellationToken)
            ?? throw new NotFoundException("User action request", command.Id);

        var agent = request.Agent;
        if (request.Status == UserActionStatus.Open)
        {
            var response = string.IsNullOrWhiteSpace(command.Response) ? null : command.Response.Trim();
            var recordAs = RecordAs.Normalize(command.RecordAs, response);

            request.ResolutionNote = recordAs switch
            {
                RecordAs.Rule => RecordAsRule(agent, request, response!),
                RecordAs.Memory => RecordInMemory(agent, request, response!),
                _ => response is null
                    ? "Completed by the user; nothing recorded — the agent verifies the state itself."
                    : "Answer kept for the record only; not handed to the agent."
            };
            request.Status = UserActionStatus.Resolved;
            request.ResolvedAtUtc = DateTime.UtcNow;
            request.Response = response;

            // Unpark: when nothing else blocks the agent and it's enabled, run at the next tick.
            var stillBlocked = await db.UserActionRequests.AnyAsync(
                r => r.AgentId == agent.Id && r.Id != request.Id && r.Status == UserActionStatus.Open && r.Blocking, cancellationToken);
            if (!stillBlocked && agent.Enabled)
            {
                agent.NextRunAtUtc = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        return request.ToDto(agent.Name, agent.Resources.Any(GraphInfrastructure.IsMemoryGraph));
    }

    /// <summary>
    /// Appends the answer to the agent's own "&lt;agent&gt; decisions" rule set — created and attached
    /// on first use — so it joins the system prompt of every future run, deterministically.
    /// </summary>
    private string RecordAsRule(LoopAgent agent, UserActionRequest request, string response)
    {
        var name = DecisionsRuleSetName(agent.Name);
        var set = agent.Resources.FirstOrDefault(r =>
            r.Type == ResourceType.RuleSet && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (set is null)
        {
            set = new Resource
            {
                WorkflowId = agent.WorkflowId,
                Name = name,
                Type = ResourceType.RuleSet,
                Description = "Standing decisions from resolved user action requests — part of every run's system prompt.",
                ConfigJson = """{"rules":[]}"""
            };
            db.Resources.Add(set);
            agent.Resources.Add(set);
        }

        var config = ResourceConfig.Parse<RuleSetConfig>(set);
        config.Rules.Add(new RuleItem { Text = $"{response} (the user's decision on: {request.Title})", Enabled = true });
        set.ConfigJson = JsonSerializer.Serialize(config, Json);
        set.UpdatedAtUtc = DateTime.UtcNow;
        return $"Recorded as a standing rule in '{set.Name}'.";
    }

    /// <summary>Drops the answer into the attached memory graph's inbox; the curator folds it into canonical memory.</summary>
    private static string RecordInMemory(LoopAgent agent, UserActionRequest request, string response)
    {
        var graph = agent.Resources.FirstOrDefault(GraphInfrastructure.IsMemoryGraph)
            ?? throw new ValidationException("This agent has no memory graph attached — record the answer as a rule instead.");
        var path = new Modules.ResourceModuleContext(graph.ConfigJson).GetString("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ValidationException($"The memory graph '{graph.Name}' has no storage path.");
        }
        GraphInfrastructure.WriteInboxItem(path, "decision",
            $"The user decided on '{request.Title}': {response}", "user", request.RunId?.ToString());
        return $"Dropped into the inbox of '{graph.Name}' for its curator.";
    }

    public static string DecisionsRuleSetName(string agentName) => $"{agentName.Trim()} decisions";
}

public sealed class ResolveUserActionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/user-actions/{id:guid}/resolve",
            (Guid id, ResolveBody body, IDispatcher dispatcher, CancellationToken ct) =>
                dispatcher.Send(new ResolveUserActionCommand(id, body.Response, body.RecordAs), ct));

    public sealed record ResolveBody(string? Response, string? RecordAs = null);
}
