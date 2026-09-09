using Looper.Api.Domain;
using Looper.Api.Infrastructure.Execution;

namespace Looper.Api.Modules.BuiltIn;

// ============================================================================
// Events as resources. Raising and listening used to depend on the model (an agent
// deciding to curl the bus) or on free-text topics typed into an agent. These two
// resource types make both sides deterministic and visible on the canvas:
//   📣 Event Raiser  — attached to an agent, the HARNESS raises its topic when the run
//                      ends the way the resource says (succeeded / failed / always).
//   📡 Event Listener — attached to an agent, the DISPATCHER wakes that agent whenever
//                      a matching topic is raised, whatever the agent's trigger mode.
// ============================================================================

/// <summary>Raises a named event when the attached agent's run completes — by the harness, not the model.</summary>
public sealed class EventRaiserModule : IResourceTypeModule
{
    public const string TypeKey_ = "EventRaiser";

    public const string WhenSucceeded = "succeeded";
    public const string WhenFailed = "failed";
    public const string WhenAlways = "always";

    public string TypeKey => TypeKey_;
    public string DisplayName => "Event Raiser";
    public string Icon => "📣";
    public string Blurb => "Raises a named event when the agent's run completes — deterministically, by Looper, so other loops can chain off it.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("topic", "Event", ResourceFieldKind.Text, Required: true,
            Hint: "A dotted lowercase key such as newsletter.sent or prd.approved. Pick an existing event or create a new one.",
            Placeholder: "newsletter.sent"),
        new("when", "Raise when the run", ResourceFieldKind.Select, Required: true, Options: [WhenSucceeded, WhenFailed, WhenAlways],
            Hint: "succeeded = the run finished and every gate passed · failed = it failed or a gate failed · always = both."),
        new("payload", "Payload", ResourceFieldKind.Multiline,
            Hint: "Handed to listeners verbatim. Placeholders: {agent}, {run}, {status}, {result} (the run's final result, trimmed).",
            Placeholder: "{agent} finished: {result}")
    ];

    public void PrepareRun(ResourceModuleContext context)
    {
        // Validated at save time, like a path: a raiser with a bad topic must not exist.
        var config = EventResources.ParseRaiser(context);
        if (config.Topic.Length > 0 && !EventDispatcher.IsValidTopic(config.Topic))
        {
            throw new ArgumentException(
                $"'{config.Topic}' is not a valid event topic — use dotted lowercase keys (letters, digits, dashes), e.g. newsletter.sent.");
        }
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var config = EventResources.ParseRaiser(context);
        if (config.Topic.Length == 0) return contribution;

        var when = config.When switch
        {
            WhenFailed => "fails (or a gate fails)",
            WhenAlways => "completes, whatever the outcome",
            _ => "succeeds and every gate passes"
        };
        contribution.PromptSections.Add(
            $"EVENT '{config.Topic}': Looper raises this event automatically when this run {when} — you do not need to " +
            "raise it yourself, and you must not raise it early. Other loops may be listening for it, so make sure your " +
            "result text states plainly what was done: it travels with the event.");
        return contribution;
    }
}

/// <summary>Wakes the attached agent whenever a matching event is raised — a subscription you can see on the canvas.</summary>
public sealed class EventListenerModule : IResourceTypeModule
{
    public const string TypeKey_ = "EventListener";

    public string TypeKey => TypeKey_;
    public string DisplayName => "Event Listener";
    public string Icon => "📡";
    public string Blurb => "Wakes the agent whenever a matching event is raised — wire one loop to another without typing topics into the agent.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("topic", "Listens for", ResourceFieldKind.Text, Required: true,
            Hint: "An exact event, or a prefix ending in .* — e.g. newsletter.sent or agent.docs-gardener.*. Every finished run " +
                  "raises agent.<name>.succeeded / .failed, so loops can chain.",
            Placeholder: "newsletter.sent")
    ];

    public void PrepareRun(ResourceModuleContext context)
    {
        var pattern = EventResources.ListenerPattern(context);
        if (pattern.Length > 0 && !EventDispatcher.IsValidPattern(pattern))
        {
            throw new ArgumentException(
                $"'{pattern}' is not a valid event pattern — use a dotted lowercase key, optionally ending in .* (e.g. agent.docs-gardener.*).");
        }
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var pattern = EventResources.ListenerPattern(context);
        if (pattern.Length == 0) return contribution;

        contribution.PromptSections.Add(
            $"EVENT LISTENER '{pattern}': this loop is woken by events matching that pattern. When this iteration was " +
            "started by one, the TRIGGERING EVENT(S) section above is the reason you are running — address it directly.");
        return contribution;
    }
}

public sealed record EventRaiserConfig(string Topic, string When, string Payload);

/// <summary>Deterministic helpers shared by the modules, the dispatcher, the harness and the topic catalog.</summary>
public static class EventResources
{
    public static bool IsRaiser(Resource resource) =>
        resource.Type == ResourceType.Custom &&
        string.Equals(resource.CustomTypeKey, EventRaiserModule.TypeKey_, StringComparison.OrdinalIgnoreCase);

    public static bool IsListener(Resource resource) =>
        resource.Type == ResourceType.Custom &&
        string.Equals(resource.CustomTypeKey, EventListenerModule.TypeKey_, StringComparison.OrdinalIgnoreCase);

    public static EventRaiserConfig ParseRaiser(ResourceModuleContext context)
    {
        var when = context.GetString("when")?.Trim().ToLowerInvariant() switch
        {
            EventRaiserModule.WhenFailed => EventRaiserModule.WhenFailed,
            EventRaiserModule.WhenAlways => EventRaiserModule.WhenAlways,
            _ => EventRaiserModule.WhenSucceeded
        };
        return new EventRaiserConfig(
            Topic: context.GetString("topic")?.Trim() ?? "",
            When: when,
            Payload: context.GetString("payload") ?? "");
    }

    public static EventRaiserConfig ParseRaiser(Resource resource) => ParseRaiser(new ResourceModuleContext(resource.ConfigJson));

    public static string ListenerPattern(ResourceModuleContext context) => context.GetString("topic")?.Trim() ?? "";

    public static string ListenerPattern(string configJson) => ListenerPattern(new ResourceModuleContext(configJson));

    /// <summary>Raisers attached to an agent, with valid topics only.</summary>
    public static IReadOnlyList<(Resource Resource, EventRaiserConfig Config)> Raisers(IEnumerable<Resource> resources) =>
        resources.Where(IsRaiser)
            .Select(r => (Resource: r, Config: ParseRaiser(r)))
            .Where(x => EventDispatcher.IsValidTopic(x.Config.Topic))
            .ToList();

    /// <summary>Listener patterns attached to an agent, valid ones only.</summary>
    public static IReadOnlyList<string> ListenerPatterns(IEnumerable<Resource> resources) =>
        resources.Where(IsListener)
            .Select(r => ListenerPattern(r.ConfigJson))
            .Where(EventDispatcher.IsValidPattern)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Does the raiser fire for this outcome? succeeded = run ok AND gates ok; failed = anything else.</summary>
    public static bool Fires(EventRaiserConfig config, bool runSucceeded, bool? gatesPassed)
    {
        var succeeded = runSucceeded && gatesPassed != false;
        return config.When switch
        {
            EventRaiserModule.WhenAlways => true,
            EventRaiserModule.WhenFailed => !succeeded,
            _ => succeeded
        };
    }

    /// <summary>Fills the payload template; an empty template yields a sensible default line.</summary>
    public static string RenderPayload(EventRaiserConfig config, string agentName, Guid runId, bool runSucceeded,
        bool? gatesPassed, string? resultText)
    {
        var status = runSucceeded && gatesPassed != false ? "succeeded" : "failed";
        var result = (resultText ?? "").Trim();
        if (result.Length > 1500) result = result[..1500] + "…";
        var template = string.IsNullOrWhiteSpace(config.Payload)
            ? "{agent} {status}. {result}"
            : config.Payload;
        return template
            .Replace("{agent}", agentName, StringComparison.Ordinal)
            .Replace("{run}", runId.ToString(), StringComparison.Ordinal)
            .Replace("{status}", status, StringComparison.Ordinal)
            .Replace("{result}", result, StringComparison.Ordinal)
            .Trim();
    }
}
