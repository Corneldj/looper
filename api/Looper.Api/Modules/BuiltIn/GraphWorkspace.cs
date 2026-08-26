using System.Reflection;
using System.Text.Json;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// Bootstraps a graph resource's storage folder with everything a fresh-context loop
/// iteration needs to find on disk: the loopergraph toolkit (the read/write path as a
/// tool, not a raw folder), a seeded edge-type ontology, and the protocol README.
/// Idempotent: the toolkit upgrades when its version line changes; ontology and README
/// are seeded once and then belong to the user/agent.
/// </summary>
public static class GraphWorkspace
{
    public const string ToolFileName = "loopergraph.py";

    private static readonly Lazy<string> ToolSource = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Looper.Api.Modules.BuiltIn.Assets.loopergraph.py")
            ?? throw new InvalidOperationException("Embedded loopergraph.py asset is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static string ToolVersionLine =>
        ToolSource.Value.Split('\n', 3).Skip(1).FirstOrDefault()?.Trim() ?? "";

    public static void Ensure(string path, IReadOnlyDictionary<string, string>? ontologySeed, string readme)
    {
        Directory.CreateDirectory(path);

        var toolPath = Path.Combine(path, ToolFileName);
        if (!File.Exists(toolPath) || !FileHasVersion(toolPath, ToolVersionLine))
        {
            File.WriteAllText(toolPath, ToolSource.Value);
        }

        if (ontologySeed is not null)
        {
            var ontologyPath = Path.Combine(path, "ontology.json");
            if (!File.Exists(ontologyPath))
            {
                var payload = new { edge_types = ontologySeed };
                File.WriteAllText(ontologyPath,
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            }
        }

        var readmePath = Path.Combine(path, "README.md");
        if (!File.Exists(readmePath))
        {
            File.WriteAllText(readmePath, readme);
        }
    }

    /// <summary>Seed an empty execution graph so exec-status works from the first iteration.</summary>
    public static void EnsureExecutionFile(string path)
    {
        var executionPath = Path.Combine(path, "execution.json");
        if (!File.Exists(executionPath))
        {
            File.WriteAllText(executionPath, "{\n  \"nodes\": []\n}\n");
        }
    }

    private static bool FileHasVersion(string toolPath, string versionLine)
    {
        try
        {
            using var reader = new StreamReader(toolPath);
            reader.ReadLine();
            return reader.ReadLine()?.Trim() == versionLine;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
