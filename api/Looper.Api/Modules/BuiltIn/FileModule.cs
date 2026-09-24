using System.Text;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// One file the agent must know about, by exact path: an input it reads (a brief, a data file,
/// a company description) or a document it maintains across iterations (a report, a changelog).
/// A Folder resource grants a directory; this one names a single file, can put its contents
/// straight into the prompt, and can declare it read-only. A missing file fails the run before
/// the model starts — a run built on an input that isn't there would look like a success.
/// </summary>
public sealed class FileModule : IResourceTypeModule
{
    public const string TypeKey_ = "File";

    /// <summary>Inline contents beyond this are cut; the agent reads the rest from disk.</summary>
    public const int InlineLimit = 24_000;

    public string TypeKey => TypeKey_;
    public string DisplayName => "File";
    public string Icon => "📄";
    public string Blurb => "A single file by exact path — an input to read or a document to maintain — with optional inline contents and a read-only rule.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("path", "File", ResourceFieldKind.File, Required: true,
            Hint: "Absolute path to the file. The agent gets access to the file's folder and the path in $LOOPER_FILE_<NAME>.",
            Placeholder: "C:\\work\\brief\\company_description.md"),
        new("inline", "Include contents in the prompt", ResourceFieldKind.Boolean,
            Hint: $"For small text files: the contents go into every run's prompt, so the agent starts already knowing them " +
                  $"(the first {InlineLimit:N0} characters; it reads the rest from disk)."),
        new("readOnly", "Read-only", ResourceFieldKind.Boolean,
            Hint: "The agent is told never to modify, rename or delete it. Advisory — the CLI grants access per folder, not per file."),
        new("createIfMissing", "Create if missing", ResourceFieldKind.Boolean,
            Hint: "Create an empty file (and its folder) before the run when it doesn't exist yet — for a document the agent " +
                  "builds up over iterations. Otherwise a missing file fails the run before the model starts.")
    ];

    public void PrepareRun(ResourceModuleContext context)
    {
        var config = FileConfig.Parse(context)
            ?? throw new InvalidOperationException("No file path is set.");
        if (!config.IsAbsolute)
        {
            throw new InvalidOperationException($"'{config.Path}' is not an absolute path — give the full path from the root of the drive.");
        }

        var path = config.FullPath;
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException($"'{path}' is a folder, not a file — attach it as a Folder resource instead.");
        }
        if (File.Exists(path)) return;
        if (!config.CreateIfMissing)
        {
            throw new InvalidOperationException(
                $"The file '{path}' does not exist. Fix the path, create the file, or turn on 'Create if missing'.");
        }

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, "");
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var config = FileConfig.Parse(context);
        if (config is null) return contribution;

        var path = config.FullPath;
        var name = context.ResourceName.Length > 0 ? context.ResourceName : System.IO.Path.GetFileName(path);
        var envVar = EnvVarName(name);
        contribution.EnvironmentVariables[envVar] = path;

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) contribution.AdditionalDirectories.Add(directory);

        var section = new StringBuilder();
        section.Append($"FILE \"{name}\"");
        if (context.ResourceDescription.Trim().Length > 0) section.Append($" — {context.ResourceDescription.Trim()}");
        section.Append($".\nPath: {path} (also ${envVar}).");
        section.Append(config.ReadOnly
            ? "\nIt is READ-ONLY: read it; never modify, rename or delete it."
            : "\nYou may update it in place; keep its format, and never move or rename it.");
        if (config.Inline)
        {
            section.Append("\nIts current contents:\n---\n").Append(ReadInline(path)).Append("\n---");
        }
        else
        {
            section.Append("\nRead it before you start.");
        }
        contribution.PromptSections.Add(section.ToString());

        // A standing rule outlives the prompt section in a long run: it sits in the system prompt.
        if (config.ReadOnly)
        {
            contribution.SystemPromptRules.Add($"The file {path} (\"{name}\") is read-only: never modify, rename or delete it.");
        }

        return contribution;
    }

    /// <summary>LOOPER_FILE_&lt;NAME&gt; — the env var carrying the file's path into a run and its scripts.</summary>
    public static string EnvVarName(string name)
    {
        var slug = new string(name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        return "LOOPER_FILE_" + (slug.Length == 0 ? "FILE" : slug);
    }

    /// <summary>
    /// The contents for the prompt: bounded, and never a binary. A read failure is reported in
    /// place — the section still tells the agent where the file is.
    /// </summary>
    internal static string ReadInline(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var probe = new byte[(int)Math.Min(8192, stream.Length)];
            var probed = stream.Read(probe, 0, probe.Length);
            if (Array.IndexOf(probe, (byte)0, 0, probed) >= 0)
            {
                return $"[binary file, {stream.Length:N0} bytes — not inlined; read it from disk if you need it]";
            }

            stream.Position = 0;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[InlineLimit + 1];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            if (read == 0) return "[the file is empty]";
            return read > InlineLimit
                ? new string(buffer, 0, InlineLimit) + "\n[… truncated — read the rest from disk]"
                : new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"[contents could not be read: {ex.Message}]";
        }
    }
}

/// <summary>Parsed view of a File resource; null when no path is configured.</summary>
public sealed record FileConfig(string Path, bool Inline, bool ReadOnly, bool CreateIfMissing)
{
    public bool IsAbsolute => System.IO.Path.IsPathRooted(Path);

    /// <summary>Normalized absolute path; a relative one resolves against the API's own directory, which PrepareRun refuses.</summary>
    public string FullPath => System.IO.Path.GetFullPath(Path);

    public static FileConfig? Parse(ResourceModuleContext context)
    {
        var path = context.GetString("path")?.Trim();
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            _ = System.IO.Path.GetFullPath(path); // reject paths the OS cannot even normalize
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        return new FileConfig(path, context.GetBool("inline"), context.GetBool("readOnly"), context.GetBool("createIfMissing"));
    }
}
