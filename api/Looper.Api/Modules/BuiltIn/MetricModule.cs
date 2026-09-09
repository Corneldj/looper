using Looper.Api.Domain;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// A user-defined outcome metric. Pull requests were Looper's first end-result metric, but a
/// loop's result can be anything — campaign sign-ups, ticket resolution time, revenue, defect
/// counts. A Metric resource names the outcome, says how values combine (a gauge, a running
/// total, an average) and which way is good; agents report values through an injected protocol,
/// scripts by printing `@metric name=value`, people from the dashboard. Every metric is
/// discoverable on the dashboard the moment it exists.
/// </summary>
public sealed class MetricModule : IResourceTypeModule
{
    public const string TypeKey_ = "Metric";

    public string TypeKey => TypeKey_;
    public string DisplayName => "Metric";
    public string Icon => "📈";
    public string Blurb => "An outcome you care about — sign-ups, conversion, revenue, anything measurable — reported by agents and scripts, tracked on the dashboard.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("unit", "Unit", ResourceFieldKind.Text,
            Hint: "Shown next to values: %, $, sign-ups, ms, … Leave empty for a plain count.",
            Placeholder: "sign-ups"),
        new("aggregation", "How values combine", ResourceFieldKind.Select, Required: true, Options: ["latest", "sum", "average"],
            Hint: "latest = a gauge, the newest reading is the current value (conversion rate, MRR) · " +
                  "sum = a running total, each report adds (sign-ups, emails sent) · average = the mean of the reports (response time)."),
        new("direction", "Which way is good", ResourceFieldKind.Select, Required: true, Options: ["higher", "lower"],
            Hint: "Drives the trend colour on the dashboard."),
        new("target", "Target", ResourceFieldKind.Number,
            Hint: "Optional goal; the dashboard shows progress towards it.", Placeholder: "1000"),
        new("instructions", "How to measure it", ResourceFieldKind.Multiline,
            Hint: "Guidance for the agent: where the number comes from, when to report, what counts. Injected into its prompt.",
            Placeholder: "After each campaign send, read the sign-up count from the analytics API and report the new sign-ups since your last report.")
    ];

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        if (context.ResourceId is not { } id) return contribution;
        var config = MetricResources.Parse(context);

        contribution.EnvironmentVariables[MetricResources.EnvVarName(context.ResourceName)] = id.ToString();

        var unit = string.IsNullOrWhiteSpace(config.Unit) ? "" : $" (unit: {config.Unit})";
        var semantics = config.Aggregation switch
        {
            MetricAggregation.Sum => "Values ADD UP over time: report only the new amount since your last report, never a running total.",
            MetricAggregation.Average => "Values are AVERAGED: report each individual measurement.",
            _ => "The LATEST value is the current reading: report the figure as it stands now (a rate, a total, a level)."
        };
        var goal = (config.Direction == MetricDirection.Lower ? "Lower is better" : "Higher is better") +
                   (config.Target is { } target ? $"; the target is {target}{(string.IsNullOrWhiteSpace(config.Unit) ? "" : " " + config.Unit)}." : ".");

        contribution.PromptSections.Add(
            $"METRIC '{context.ResourceName}'{unit} — an outcome the user tracks on the dashboard." +
            (string.IsNullOrWhiteSpace(context.ResourceDescription) ? "" : $" {context.ResourceDescription.Trim()}") +
            (string.IsNullOrWhiteSpace(config.Instructions) ? "" : $"\nHow to measure it: {config.Instructions.Trim()}") +
            $"\n{semantics} {goal} Report a measurement whenever you have a real one with the Looper tool record_metric " +
            $"(metric \"{context.ResourceName}\"). " +
            $"(A script can instead print a line `@metric {MetricResources.Slug(context.ResourceName)}=<number> <note>` and Looper " +
            $"records it; the metric id is in ${MetricResources.EnvVarName(context.ResourceName)}.) " +
            "Report only measured facts — never estimates, projections or hopes; if you could not measure it this iteration, report nothing.");

        return contribution;
    }
}

public enum MetricAggregation
{
    Latest,
    Sum,
    Average
}

public enum MetricDirection
{
    Higher,
    Lower
}

/// <summary>Parsed view of a Metric resource.</summary>
public sealed record MetricConfig(
    string Unit,
    MetricAggregation Aggregation,
    MetricDirection Direction,
    double? Target,
    string Instructions);

/// <summary>Deterministic helpers shared by the module, the metrics feature and the script recorder.</summary>
public static class MetricResources
{
    public static bool IsMetric(Resource resource) =>
        resource.Type == ResourceType.Custom &&
        string.Equals(resource.CustomTypeKey, MetricModule.TypeKey_, StringComparison.OrdinalIgnoreCase);

    public static MetricConfig Parse(ResourceModuleContext context) => new(
        Unit: context.GetString("unit")?.Trim() ?? "",
        Aggregation: ParseAggregation(context.GetString("aggregation")),
        Direction: string.Equals(context.GetString("direction")?.Trim(), "lower", StringComparison.OrdinalIgnoreCase)
            ? MetricDirection.Lower
            : MetricDirection.Higher,
        Target: context.GetNumber("target"),
        Instructions: context.GetString("instructions") ?? "");

    public static MetricConfig Parse(Resource resource) => Parse(new ResourceModuleContext(resource.ConfigJson));

    public static MetricAggregation ParseAggregation(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "sum" => MetricAggregation.Sum,
            "average" or "avg" or "mean" => MetricAggregation.Average,
            _ => MetricAggregation.Latest
        };

    public static string Slug(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "metric" : slug;
    }

    /// <summary>LOOPER_METRIC_&lt;NAME&gt; — carries the metric's id into a run for scripts and curl.</summary>
    public static string EnvVarName(string name) =>
        "LOOPER_METRIC_" + Slug(name).Replace('-', '_').ToUpperInvariant();

    /// <summary>
    /// Finds the metric a reporter meant: by id, by exact name (case-insensitive), or by slug —
    /// so `@metric sign-ups=12` reaches "Sign-ups" and curl can use either form.
    /// </summary>
    public static Resource? Match(IEnumerable<Resource> metrics, string key)
    {
        var trimmed = key.Trim();
        if (trimmed.Length == 0) return null;
        var candidates = metrics.Where(IsMetric).ToList();
        if (Guid.TryParse(trimmed, out var id))
        {
            return candidates.FirstOrDefault(r => r.Id == id);
        }
        return candidates.FirstOrDefault(r => string.Equals(r.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(r => Slug(r.Name) == Slug(trimmed));
    }
}
