using System.Text.Json.Nodes;
using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Features.Resources;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Boards;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Boards;

/// <summary>A ticket as the picker lists it. <paramref name="Listed"/> is false for a selected ticket the board's filters no longer match.</summary>
public sealed record TicketSummaryDto(
    int Id, string Title, string Type, string State, string AssignedTo, IReadOnlyList<string> Tags, string Url,
    DateTime? ChangedAtUtc, bool Listed);

public sealed record JiraIssueDto(string Key, string Summary);

/// <summary>A work item in full, as the get_work_item tool hands it to the model.</summary>
public sealed record WorkItemDto(
    int Id, string Title, string Type, string State, string AssignedTo, IReadOnlyList<string> Tags,
    string AreaPath, string Iteration, int? Priority, string Url,
    string Description, string AcceptanceCriteria, string ReproSteps);

// ---------- the ticket picker ----------

/// <summary>
/// The board behind an Azure DevOps tickets resource: its open work items, with the editor's
/// current (possibly unsaved) filters. <paramref name="ResourceId"/> lets an edit keep using the
/// stored token without the user pasting it again.
/// </summary>
public sealed record ListBoardTicketsQuery(Guid? ResourceId, string? ConfigJson) : IQuery<IReadOnlyList<TicketSummaryDto>>;

public sealed class ListBoardTicketsHandler(LooperDbContext db, ResourceModuleRegistry registry, AzureDevOpsClient azure)
    : IQueryHandler<ListBoardTicketsQuery, IReadOnlyList<TicketSummaryDto>>
{
    public async Task<IReadOnlyList<TicketSummaryDto>> Handle(ListBoardTicketsQuery query, CancellationToken cancellationToken)
    {
        var json = await EditorConfig.WithStoredSecretAsync(db, registry, query.ResourceId, query.ConfigJson,
            AzureDevOpsTicketsModule.TypeKey_, "organization", cancellationToken);
        var config = AzureDevOpsTicketsResources.Parse(new ResourceModuleContext(json));
        if (!config.HasConnection)
        {
            throw new ValidationException("Fill in the organization, project and personal access token first.");
        }

        try
        {
            var listed = await azure.QueryAsync(config.Connection, config.Filter, cancellationToken);
            var tickets = listed.Select(i => Summary(i, listed: true)).ToList();

            // A selected ticket the filters no longer match (closed since, reassigned) still shows, so it can be unticked.
            var unlisted = config.TicketIds.Where(id => listed.All(i => i.Id != id)).ToList();
            if (unlisted.Count > 0)
            {
                var extra = await azure.GetSummariesAsync(config.Connection, unlisted, cancellationToken);
                tickets.InsertRange(0, extra.OfType<WorkItem>().Select(i => Summary(i, listed: false)));
            }
            return tickets;
        }
        catch (AzureDevOpsException ex)
        {
            throw new ValidationException(ex.Message);
        }
    }

    private static TicketSummaryDto Summary(WorkItem item, bool listed) =>
        new(item.Id, item.Title, item.Type, item.State, item.AssignedTo, item.Tags, item.Url, item.ChangedAtUtc, listed);
}

// ---------- the Jira issue picker ----------

/// <summary>Jira's issue typeahead. A null <paramref name="ConfigJson"/> searches with the resource as stored — the canvas card's case.</summary>
public sealed record SearchJiraIssuesQuery(Guid? ResourceId, string? ConfigJson, string Text) : IQuery<IReadOnlyList<JiraIssueDto>>;

public sealed class SearchJiraIssuesHandler(LooperDbContext db, ResourceModuleRegistry registry, JiraTempoClient jira)
    : IQueryHandler<SearchJiraIssuesQuery, IReadOnlyList<JiraIssueDto>>
{
    public const int Limit = 8;

    public async Task<IReadOnlyList<JiraIssueDto>> Handle(SearchJiraIssuesQuery query, CancellationToken cancellationToken)
    {
        var json = await EditorConfig.WithStoredSecretAsync(db, registry, query.ResourceId, query.ConfigJson,
            JiraTimeTrackingModule.TypeKey_, "baseUrl", cancellationToken);
        var config = JiraTimeTrackingResources.Parse(new ResourceModuleContext(json));
        if (!config.HasConnection)
        {
            throw new ValidationException("Fill in the Jira address and personal access token first.");
        }
        var text = query.Text?.Trim() ?? "";
        if (text.Length < 2) return [];

        try
        {
            var issues = await jira.SearchIssuesAsync(config.Connection, text, Limit, cancellationToken);
            return issues.Select(i => new JiraIssueDto(i.Key, i.Summary)).ToList();
        }
        catch (JiraException ex)
        {
            throw new ValidationException(ex.Message);
        }
    }
}

// ---------- the get_work_item tool ----------

/// <summary>A work item read live for a running agent, through the tickets resource attached to it.</summary>
public sealed record GetWorkItemQuery(Guid AgentId, int Id, string? Resource = null) : IQuery<WorkItemDto>;

public sealed class GetWorkItemHandler(LooperDbContext db, AzureDevOpsClient azure) : IQueryHandler<GetWorkItemQuery, WorkItemDto>
{
    /// <summary>Beyond this a field is cut: a tool result lands in the model's context whole.</summary>
    public const int FieldLimit = 30_000;

    public async Task<WorkItemDto> Handle(GetWorkItemQuery query, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == query.AgentId, cancellationToken)
            ?? throw new NotFoundException("Agent", query.AgentId);

        var resource = AzureDevOpsTicketsResources.Resolve(agent.Resources, query.Resource);
        var config = AzureDevOpsTicketsResources.Parse(resource);
        if (!config.HasConnection)
        {
            throw new ValidationException($"The tickets resource \"{resource.Name}\" has no organization, project or token stored.");
        }

        var item = (await azure.GetAsync(config.Connection, [query.Id], cancellationToken))[0]
                   ?? throw new ValidationException($"Work item #{query.Id} was not found, or the token cannot see it.");
        return new WorkItemDto(item.Id, item.Title, item.Type, item.State, item.AssignedTo, item.Tags, item.AreaPath,
            item.Iteration, item.Priority, item.Url, Cap(item.Description), Cap(item.AcceptanceCriteria), Cap(item.ReproSteps));
    }

    private static string Cap(string text) => text.Length <= FieldLimit ? text : text[..FieldLimit] + "\n[… cut at 30,000 characters]";
}

// ---------- the canvas cards ----------

/// <summary>
/// The prompt box on a one-off prompt's workbench card. It writes the text alone, so nothing else
/// the resource holds can be clobbered by a card that never saw it.
/// </summary>
public sealed record SetOneOffPromptCommand(Guid ResourceId, string? Text) : ICommand<ResourceDto>;

public sealed class SetOneOffPromptValidator : AbstractValidator<SetOneOffPromptCommand>
{
    public SetOneOffPromptValidator()
    {
        RuleFor(c => c.Text).MaximumLength(OneOffPromptModule.MaxLength)
            .WithMessage($"Keep the instructions under {OneOffPromptModule.MaxLength:N0} characters — they travel on the run's command line.");
    }
}

public sealed class SetOneOffPromptHandler(LooperDbContext db, ResourceModuleRegistry registry)
    : ICommandHandler<SetOneOffPromptCommand, ResourceDto>
{
    public async Task<ResourceDto> Handle(SetOneOffPromptCommand command, CancellationToken cancellationToken)
    {
        var (resource, agentCount) = await CardResource.LoadAsync(db, command.ResourceId, OneOffPromptModule.TypeKey_, cancellationToken);
        resource.ConfigJson = BoardHarness.WithValue(resource.ConfigJson, "text", command.Text ?? "");
        resource.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return resource.ToDto(agentCount, registry);
    }
}

/// <summary>The issue chooser on a Jira time-tracking resource's workbench card: the key and title alone. An empty key clears both.</summary>
public sealed record SetJiraIssueCommand(Guid ResourceId, string? IssueKey, string? IssueSummary) : ICommand<ResourceDto>;

public sealed class SetJiraIssueValidator : AbstractValidator<SetJiraIssueCommand>
{
    public SetJiraIssueValidator()
    {
        RuleFor(c => c.IssueKey)
            .Must(key => string.IsNullOrWhiteSpace(key) || JiraTimeTrackingModule.IsIssueKey(key.Trim().ToUpperInvariant()))
            .WithMessage(c => $"'{c.IssueKey}' is not a Jira issue key — keys look like PROJ-123.");
    }
}

public sealed class SetJiraIssueHandler(LooperDbContext db, ResourceModuleRegistry registry)
    : ICommandHandler<SetJiraIssueCommand, ResourceDto>
{
    public async Task<ResourceDto> Handle(SetJiraIssueCommand command, CancellationToken cancellationToken)
    {
        var (resource, agentCount) = await CardResource.LoadAsync(db, command.ResourceId, JiraTimeTrackingModule.TypeKey_, cancellationToken);
        var key = command.IssueKey?.Trim().ToUpperInvariant() ?? "";
        var summary = key.Length == 0 ? "" : command.IssueSummary?.Trim() ?? "";
        resource.ConfigJson = BoardHarness.WithValue(BoardHarness.WithValue(resource.ConfigJson, "issueKey", key), "issueSummary", summary);
        resource.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return resource.ToDto(agentCount, registry);
    }
}

/// <summary>The ticket selection on an Azure DevOps tickets resource's workbench card, in pick order — the first is the time-tracking reference.</summary>
public sealed record SetTicketSelectionCommand(Guid ResourceId, IReadOnlyList<int>? TicketIds) : ICommand<ResourceDto>;

public sealed class SetTicketSelectionValidator : AbstractValidator<SetTicketSelectionCommand>
{
    public SetTicketSelectionValidator()
    {
        RuleFor(c => c.TicketIds).Must(ids => ids is null || ids.All(id => id > 0))
            .WithMessage("Ticket ids are positive numbers, e.g. 12345.");
        RuleFor(c => c.TicketIds).Must(ids => ids is null || ids.Distinct().Count() <= AzureDevOpsTicketsResources.MaxSelected)
            .WithMessage($"Select at most {AzureDevOpsTicketsResources.MaxSelected} tickets for one loop.");
    }
}

public sealed class SetTicketSelectionHandler(LooperDbContext db, ResourceModuleRegistry registry)
    : ICommandHandler<SetTicketSelectionCommand, ResourceDto>
{
    public async Task<ResourceDto> Handle(SetTicketSelectionCommand command, CancellationToken cancellationToken)
    {
        var (resource, agentCount) = await CardResource.LoadAsync(db, command.ResourceId, AzureDevOpsTicketsModule.TypeKey_, cancellationToken);
        var ids = AzureDevOpsTicketsResources.FormatIds((command.TicketIds ?? []).Distinct());
        resource.ConfigJson = BoardHarness.WithValue(resource.ConfigJson, "ticketIds", ids);
        resource.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return resource.ToDto(agentCount, registry);
    }
}

/// <summary>
/// The config keys a resource's canvas card owns. The card writes them through its own endpoint and
/// the edit form leaves them out unless it changed them itself; an update that leaves them out keeps
/// what is stored — so saving the form never puts back a prompt a run has taken since, or undoes an
/// issue or a ticket picked meanwhile.
/// </summary>
public static class CardFields
{
    public static IReadOnlyList<string> For(Resource resource) =>
        resource.Type != ResourceType.Custom ? []
        : string.Equals(resource.CustomTypeKey, OneOffPromptModule.TypeKey_, StringComparison.OrdinalIgnoreCase) ? OneOffPromptResources.CardKeys
        : string.Equals(resource.CustomTypeKey, JiraTimeTrackingModule.TypeKey_, StringComparison.OrdinalIgnoreCase) ? JiraTimeTrackingResources.CardKeys
        : string.Equals(resource.CustomTypeKey, AzureDevOpsTicketsModule.TypeKey_, StringComparison.OrdinalIgnoreCase) ? AzureDevOpsTicketsResources.CardKeys
        : [];

    /// <summary>The incoming config, with every card key it leaves out taken from the stored one.</summary>
    public static string KeepOmitted(Resource stored, string incomingJson)
    {
        var keys = For(stored);
        if (keys.Count == 0 || Parse(incomingJson) is not { } incoming) return incomingJson;

        var storedConfig = Parse(stored.ConfigJson);
        foreach (var key in keys)
        {
            if (incoming.Any(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))) continue;
            var kept = storedConfig?.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (kept?.Value is { } value) incoming[kept.Value.Key] = value.DeepClone();
        }
        return incoming.ToJsonString();
    }

    private static JsonObject? Parse(string json)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

internal static class CardResource
{
    /// <summary>The resource a card edits, tracked for the update, with its agent count for the DTO.</summary>
    public static async Task<(Resource Resource, int AgentCount)> LoadAsync(LooperDbContext db, Guid id, string typeKey,
        CancellationToken cancellationToken)
    {
        var found = await db.Resources
            .Where(r => r.Id == id)
            .Select(r => new { Resource = r, AgentCount = r.Agents.Count })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Resource", id);
        if (found.Resource.Type != ResourceType.Custom ||
            !string.Equals(found.Resource.CustomTypeKey, typeKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException($"Resource '{found.Resource.Name}' is not a {typeKey} resource.");
        }
        return (found.Resource, found.AgentCount);
    }
}

// ---------- shared ----------

internal static class EditorConfig
{
    /// <summary>
    /// The editor's config with the stored token filled in where the form still shows the masked
    /// value — or, when there is no form (a canvas card), the stored config itself. The stored token
    /// only ever goes to the address it was saved with: pointing an edit at another organization or
    /// Jira needs the token pasted again.
    /// </summary>
    public static async Task<string> WithStoredSecretAsync(LooperDbContext db, ResourceModuleRegistry registry,
        Guid? resourceId, string? configJson, string typeKey, string addressKey, CancellationToken cancellationToken)
    {
        if (resourceId is not { } id)
        {
            return configJson ?? throw new ValidationException("Give the resource, or the form's config.");
        }

        var stored = await db.Resources.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                     ?? throw new NotFoundException("Resource", id);
        if (stored.Type != ResourceType.Custom || !string.Equals(stored.CustomTypeKey, typeKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException($"Resource '{stored.Name}' is not a {typeKey} resource.");
        }
        if (configJson is null) return stored.ConfigJson;

        var incoming = new ResourceModuleContext(configJson);
        if (incoming.GetString("pat") == SecretMasker.Sentinel &&
            !string.Equals(Address(incoming, addressKey), Address(new ResourceModuleContext(stored.ConfigJson), addressKey),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("The stored token belongs to the address it was saved with — paste the token again to use a different one.");
        }
        return SecretMasker.PreserveSecrets(stored, configJson, registry);
    }

    private static string Address(ResourceModuleContext context, string key) => context.GetString(key)?.Trim().TrimEnd('/') ?? "";
}

public sealed class BoardEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/boards/tickets", (TicketsBody body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new ListBoardTicketsQuery(body.ResourceId, body.ConfigJson), ct));
        app.MapPost("/api/boards/jira-issues", (JiraIssuesBody body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new SearchJiraIssuesQuery(body.ResourceId, body.ConfigJson, body.Query), ct));

        // The canvas cards: each writes only the keys it owns.
        app.MapPut("/api/boards/prompts/{id:guid}", (Guid id, PromptBody body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new SetOneOffPromptCommand(id, body.Text), ct));
        app.MapPut("/api/boards/time-trackers/{id:guid}/issue", (Guid id, IssueBody body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new SetJiraIssueCommand(id, body.IssueKey, body.IssueSummary), ct));
        app.MapPut("/api/boards/ticket-selections/{id:guid}", (Guid id, SelectionBody body, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(new SetTicketSelectionCommand(id, body.TicketIds), ct));
    }

    public sealed record TicketsBody(Guid? ResourceId, string? ConfigJson);

    public sealed record SelectionBody(IReadOnlyList<int>? TicketIds);

    public sealed record JiraIssuesBody(Guid? ResourceId, string? ConfigJson, string Query);

    public sealed record PromptBody(string? Text);

    public sealed record IssueBody(string? IssueKey, string? IssueSummary);
}
