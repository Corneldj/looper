using System.Text.RegularExpressions;
using Looper.Api.Domain;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// The contract of record for an agent's work: a specification carrying stable REQ-n
/// (requirement) and AC-n (acceptance criterion) identifiers. Attaching one closes the
/// traceability gap in the PRD-to-PR pipeline — every PR the agent registers must cite
/// the acceptance criteria it satisfies, and Looper verifies the citations against the
/// spec at registration time (a harness gate, not an honor system).
/// </summary>
public sealed class SpecificationModule : IResourceTypeModule
{
    public const string TypeKey_ = "Specification";

    public string TypeKey => TypeKey_;
    public string DisplayName => "Specification";
    public string Icon => "📐";
    public string Blurb => "REQ/AC identifiers every PR must cite — verified traceability from spec to shipped work.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("specId", "Spec identifier", ResourceFieldKind.Text, Required: true,
            Hint: "Stable id agents and humans cite, e.g. SPEC-CHECKOUT-1. Never renumber it.",
            Placeholder: "SPEC-1"),
        new("content", "Specification", ResourceFieldKind.Multiline,
            Hint: "The spec itself. Mark requirements REQ-1, REQ-2, … and acceptance criteria AC-1, AC-2, … — " +
                  "those identifiers are what PRs cite and what Looper verifies against.",
            Placeholder: "REQ-1 The cart persists across sessions.\n  AC-1 Items survive a browser restart.\n  AC-2 …"),
        new("path", "Spec file or folder", ResourceFieldKind.Path,
            Hint: "Optional: a spec document (or folder of .md specs) on disk instead of — or in addition to — " +
                  "the inline text. The agent gets read access; AC identifiers are parsed from it too."),
        new("advisory", "Advisory only", ResourceFieldKind.Boolean,
            Hint: "Recommend citations but don't reject PRs that omit them. Leave off to enforce — the default, " +
                  "and the point.")
    ];

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var config = SpecificationTraceability.Parse(context);
        if (config.SpecId.Length == 0 && config.Content.Length == 0 && config.Path is null) return contribution;

        if (config.Path is { } path)
        {
            contribution.EnvironmentVariables["LOOPER_SPEC_PATH"] = path;
            var directory = Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) contribution.AdditionalDirectories.Add(directory);
        }

        var acs = SpecificationTraceability.AcceptanceCriteria(config);
        var identifierNote = acs.Count > 0
            ? $"Its acceptance criteria are: {string.Join(", ", acs)}."
            : "It declares no AC-n identifiers yet — flag that to the user before building against it.";

        var body = config.Content.Length > 0
            ? (config.Content.Length > 6000
                ? config.Content[..6000] + "\n[… spec truncated — read the full text at the spec file]"
                : config.Content)
            : null;

        contribution.PromptSections.Add(
            $"SPECIFICATION {config.SpecId} — the contract your work is measured against. {identifierNote}" +
            (config.Path is not null ? $" Full spec on disk at {config.Path} (also $LOOPER_SPEC_PATH) — read it before building." : "") +
            (body is not null ? $"\n---\n{body}\n---" : "") +
            "\nTRACEABILITY" + (config.Advisory ? " (advisory)" : " (enforced)") + ": every PR you register must cite " +
            "the acceptance criteria it satisfies — register with the satisfies field: " +
            "curl -s -X POST \"$LOOPER_API_URL/api/delivery/prs\" -H 'Content-Type: application/json' " +
            "-d \"{\\\"runId\\\":\\\"$LOOPER_RUN_ID\\\",\\\"url\\\":\\\"<pr url>\\\",\\\"title\\\":\\\"<pr title>\\\"," +
            "\\\"repoPath\\\":\\\"$PWD\\\",\\\"satisfies\\\":\\\"AC-1,AC-3\\\"}\". " +
            (config.Advisory
                ? "Citations are checked against the spec when present. "
                : "Looper REJECTS registrations that omit citations or cite criteria not in the spec. ") +
            "Cite only criteria your change actually delivers — reviewers and humans audit against them. " +
            "If the work you're doing matches no acceptance criterion, that is a spec gap: raise it to the user " +
            "(user action request or escalation) instead of inventing a citation. Also reference the AC ids in the " +
            "PR description itself so human reviewers see the same traceability.");

        return contribution;
    }
}

/// <summary>Parsed view of a Specification resource, plus the identifier discipline itself.</summary>
public sealed record SpecificationConfig(string SpecId, string Content, string? Path, bool Advisory);

/// <summary>
/// Deterministic spec/PR traceability: identifier extraction and citation verification.
/// Used by the module (protocol text) and by the PR-registration gate (enforcement).
/// </summary>
public static partial class SpecificationTraceability
{
    public static SpecificationConfig Parse(ResourceModuleContext context)
    {
        var path = context.GetString("path");
        return new SpecificationConfig(
            SpecId: context.GetString("specId")?.Trim() ?? "",
            Content: context.GetString("content") ?? "",
            Path: string.IsNullOrWhiteSpace(path) ? null : path.Trim(),
            Advisory: context.GetBool("advisory"));
    }

    /// <summary>AC-n identifiers declared by a spec — inline content plus any on-disk document(s).</summary>
    public static IReadOnlyList<string> AcceptanceCriteria(SpecificationConfig config) =>
        ExtractAcs(config.Content + "\n" + ReadSpecFiles(config.Path));

    /// <summary>Distinct AC-n tokens in declaration order, normalized to upper case.</summary>
    public static IReadOnlyList<string> ExtractAcs(string text) =>
        AcPattern().Matches(text)
            .Select(m => m.Value.ToUpperInvariant())
            .Distinct()
            .ToList();

    /// <summary>Parses a citation string ("ac-1, AC-3 AC-3") into distinct normalized AC ids.</summary>
    public static IReadOnlyList<string> ParseCitations(string? satisfies) =>
        ExtractAcs(satisfies ?? "");

    /// <summary>The specifications attached to an agent, parsed. Empty list = no traceability gate.</summary>
    public static IReadOnlyList<SpecificationConfig> AttachedSpecs(IEnumerable<Resource> resources) =>
        resources
            .Where(r => r.Type == ResourceType.Custom &&
                        string.Equals(r.CustomTypeKey, SpecificationModule.TypeKey_, StringComparison.OrdinalIgnoreCase))
            .Select(r => Parse(new Modules.ResourceModuleContext(r.ConfigJson)))
            .ToList();

    private static string ReadSpecFiles(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        const int maxBytes = 200_000;
        try
        {
            if (File.Exists(path))
            {
                return ReadBounded(path, maxBytes);
            }
            if (Directory.Exists(path))
            {
                var parts = new List<string>();
                var budget = maxBytes;
                foreach (var file in Directory.EnumerateFiles(path, "*.md").Order().Take(20))
                {
                    var text = ReadBounded(file, budget);
                    parts.Add(text);
                    budget -= text.Length;
                    if (budget <= 0) break;
                }
                return string.Join("\n", parts);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable spec files contribute no identifiers; inline content still counts.
        }
        return "";
    }

    private static string ReadBounded(string file, int maxBytes)
    {
        using var stream = new StreamReader(file);
        var buffer = new char[Math.Max(0, maxBytes)];
        var read = stream.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, read);
    }

    [GeneratedRegex(@"\bAC-\d+\b", RegexOptions.IgnoreCase)]
    private static partial Regex AcPattern();
}
