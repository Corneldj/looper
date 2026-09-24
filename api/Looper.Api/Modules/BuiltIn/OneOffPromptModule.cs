using Looper.Api.Domain;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// Extra instructions for the next run only — the "Instructions / context for Claude" box on
/// Ticket Filler's Board page, and like it the box sits on the page: the resource's workbench card.
/// The next real run takes the text into its prompt and clears it as it starts, so a note written
/// for one run never leaks into the next; the run log keeps a copy.
/// </summary>
public sealed class OneOffPromptModule : IResourceTypeModule
{
    public const string TypeKey_ = "OneOffPrompt";

    public string TypeKey => TypeKey_;
    public string DisplayName => "One-off prompt";
    public string Icon => "📝";
    public string Blurb => "Extra instructions or context for the next run only: type them on the resource's card; they go into that run's prompt and are cleared as it starts.";

    /// <summary>Beyond this the box refuses more: the prompt travels as a command-line argument, which the OS bounds.</summary>
    public const int MaxLength = 8_000;

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("text", "Instructions for the next run", ResourceFieldKind.Multiline,
            Hint: "Typed in the box on the resource's workbench card. Taken by the next real run and cleared as it starts — that " +
                  "run's log keeps a copy. Dry runs leave it in place. Attached to several agents? The first one to run takes it.",
            Placeholder: "e.g. Follow the pattern used in inventory-listing…")
    ];

    /// <summary>Nothing static to add: Looper takes the text itself as a run starts (see BoardHarness).</summary>
    public ResourceContribution Contribute(ResourceModuleContext context) => new();
}

public static class OneOffPromptResources
{
    /// <summary>The keys the resource's canvas card owns: the edit form leaves them alone (see CardFields).</summary>
    public static readonly string[] CardKeys = ["text"];

    public static bool IsOneOffPrompt(Resource resource) =>
        resource.Type == ResourceType.Custom &&
        string.Equals(resource.CustomTypeKey, OneOffPromptModule.TypeKey_, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<Resource> Attached(IEnumerable<Resource> resources) =>
        resources.Where(IsOneOffPrompt).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The waiting instructions; empty when there are none.</summary>
    public static string Text(string configJson) => new ResourceModuleContext(configJson).GetString("text")?.Trim() ?? "";

    /// <summary>
    /// The prompt section for the instructions a run took. They come from the person who started
    /// the run, so they outrank anything ambiguous in the standing task or the tickets.
    /// </summary>
    public static string PromptSection(IReadOnlyList<(string Name, string Text)> prompts)
    {
        const string lead =
            "INSTRUCTIONS FOR THIS RUN — the person running this loop added the following for this run only. " +
            "They apply to the whole task and take precedence over anything ambiguous in the task description or the tickets:";
        return prompts.Count == 1
            ? $"{lead}\n\n{prompts[0].Text}"
            : lead + string.Concat(prompts.Select(p => $"\n\nFrom \"{p.Name}\":\n{p.Text}"));
    }
}
