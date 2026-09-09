using System.Text;
using Looper.Api.Domain;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// A runnable script (Python or Bash) kept as a resource: written in the editor — with Claude's
/// help if wanted — and executed as part of an agent's loop. The script is the deterministic
/// half of a loop: fetch inputs, transform data, verify outputs, notify, all without spending
/// tokens. It always runs deterministically, by the harness: BEFORE every iteration with its
/// output fed into the prompt, or AFTER every iteration as a gate (non-zero exit fails the
/// run, exactly like a Testing Action). The model never decides whether it runs.
/// </summary>
public sealed class ScriptModule : IResourceTypeModule
{
    public const string TypeKey_ = "Script";

    public const string TriggerBefore = "before";
    public const string TriggerAfter = "after";

    public string TypeKey => TypeKey_;
    public string DisplayName => "Script";
    public string Icon => "📜";
    public string Blurb => "A Python or Bash script Looper runs before or after every loop iteration — write it with Claude's help, test it in place.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("language", "Language", ResourceFieldKind.Select, Required: true, Options: ["python", "bash"],
            Hint: "Python runs with the configured interpreter (python3 by default); Bash with /bin/bash."),
        new("code", "Script", ResourceFieldKind.Multiline, Required: true,
            Hint: "The script itself. It runs with LOOPER_API_URL, LOOPER_RUN_ID and LOOPER_AGENT_ID set, plus any " +
                  "credential resources attached to the same agent.",
            Placeholder: "#!/usr/bin/env python3\nimport json, sys\nprint(json.dumps({\"ok\": True}))"),
        new("trigger", "When it runs", ResourceFieldKind.Select, Required: true, Options: [TriggerBefore, TriggerAfter],
            Hint: "before = Looper runs it before every iteration and hands the output to the model · " +
                  "after = Looper runs it after every iteration as a gate (non-zero exit fails the run). Always deterministic."),
        new("args", "Default arguments", ResourceFieldKind.Text,
            Hint: "Appended to the command line when Looper runs the script (shell syntax).", Placeholder: "--verbose"),
        new("timeoutSeconds", "Timeout (s)", ResourceFieldKind.Number,
            Hint: "Wall-clock limit when Looper runs it; empty = the testing-action default."),
        new("workingDirectory", "Working directory", ResourceFieldKind.Path,
            Hint: "Where Looper runs it. Empty = the agent's working directory.")
    ];

    public void PrepareRun(ResourceModuleContext context)
    {
        // The stored code is the source of truth; the file on disk is a projection of it,
        // regenerated before every run so edits in the resource always win.
        if (context.ResourceId is not { } id) return;
        var config = ScriptResources.Parse(context);
        if (config.Code.Length == 0) return;
        ScriptResources.Materialize(id, context.ResourceName, config);
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var config = ScriptResources.Parse(context);
        if (context.ResourceId is not { } id || config.Code.Length == 0) return contribution;

        var path = ScriptResources.ScriptPath(id, context.ResourceName, config);
        var env = ScriptResources.EnvVarName(context.ResourceName);
        contribution.EnvironmentVariables[env] = path;
        contribution.AdditionalDirectories.Add(Path.GetDirectoryName(path)!);

        var purpose = string.IsNullOrWhiteSpace(context.ResourceDescription) ? "" : $" Purpose: {context.ResourceDescription.Trim()}";
        var runHint = config.Language == ScriptLanguage.Bash
            ? $"bash \"${env}\""
            : $"python3 \"${env}\"";
        var whenNote = config.Trigger == TriggerAfter
            ? "Looper runs it after this iteration as a gate: a non-zero exit fails the run. Run it yourself before you finish to make sure it passes."
            : "Looper already ran it before this iteration — its output is in the SCRIPT OUTPUT section of this prompt. You may re-run it if you need fresh results.";

        contribution.PromptSections.Add(
            $"SCRIPT '{context.ResourceName}' ({config.Language.ToString().ToLowerInvariant()}) at {path} (also ${env}).{purpose} " +
            $"Run it with: {runHint}" + (string.IsNullOrWhiteSpace(config.Args) ? "" : $" {config.Args.Trim()}") + ". " +
            whenNote + " The file is regenerated from the resource before every run — do not edit it; if the script " +
            "needs changes, say so in your result (or raise a user action request) so the user can update the resource.");

        return contribution;
    }
}

public enum ScriptLanguage
{
    Python,
    Bash
}

/// <summary>Parsed view of a Script resource.</summary>
public sealed record ScriptConfig(
    ScriptLanguage Language,
    string Code,
    string Trigger,
    string Args,
    int? TimeoutSeconds,
    string? WorkingDirectory)
{
    public string Extension => Language == ScriptLanguage.Bash ? "sh" : "py";
}

/// <summary>
/// Deterministic helpers shared by the module (prompt + materialization), the harness runner
/// (stage execution) and the Scripts feature (manual runs, Claude assist).
/// </summary>
public static class ScriptResources
{
    /// <summary>Where materialized scripts live: one folder per resource id beside the API binary.</summary>
    public static string ScriptsRoot { get; } = Path.Combine(AppContext.BaseDirectory, "scripts");

    public static ScriptConfig Parse(ResourceModuleContext context) => Parse(
        context.GetString("language"), context.GetString("code"), context.GetString("trigger"),
        context.GetString("args"), context.GetNumber("timeoutSeconds"), context.GetString("workingDirectory"));

    public static ScriptConfig Parse(string? language, string? code, string? trigger, string? args,
        double? timeoutSeconds, string? workingDirectory)
    {
        // Before or after — never "when the model feels like it". Anything else (including the
        // retired on-demand mode) runs before, the safe default: its output informs the model.
        var normalizedTrigger = string.Equals(trigger?.Trim(), ScriptModule.TriggerAfter, StringComparison.OrdinalIgnoreCase)
            ? ScriptModule.TriggerAfter
            : ScriptModule.TriggerBefore;
        return new ScriptConfig(
            Language: ParseLanguage(language),
            Code: code ?? "",
            Trigger: normalizedTrigger,
            Args: args?.Trim() ?? "",
            TimeoutSeconds: timeoutSeconds is > 0 and var t ? (int)Math.Round(t) : null,
            WorkingDirectory: string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim());
    }

    public static ScriptLanguage ParseLanguage(string? language) =>
        string.Equals(language?.Trim(), "bash", StringComparison.OrdinalIgnoreCase) ? ScriptLanguage.Bash : ScriptLanguage.Python;

    public static bool IsScript(Resource resource) =>
        resource.Type == ResourceType.Custom &&
        string.Equals(resource.CustomTypeKey, ScriptModule.TypeKey_, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The Script resources attached to an agent, parsed; optionally only one trigger stage.
    /// Empty scripts are left out unless asked for — the harness asks, so it can fail them closed.
    /// </summary>
    public static IReadOnlyList<(Resource Resource, ScriptConfig Config)> Scripts(
        IEnumerable<Resource> resources, string? trigger = null, bool includeEmpty = false) =>
        resources
            .Where(r => r.Type == ResourceType.Custom &&
                        string.Equals(r.CustomTypeKey, ScriptModule.TypeKey_, StringComparison.OrdinalIgnoreCase))
            .Select(r => (Resource: r, Config: Parse(new ResourceModuleContext(r.ConfigJson))))
            .Where(s => (includeEmpty || s.Config.Code.Length > 0) && (trigger is null || s.Config.Trigger == trigger))
            .ToList();

    public static string Slug(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "script" : slug;
    }

    /// <summary>LOOPER_SCRIPT_&lt;NAME&gt; — the env var carrying the script's path into a run.</summary>
    public static string EnvVarName(string name) =>
        "LOOPER_SCRIPT_" + Slug(name).Replace('-', '_').ToUpperInvariant();

    public static string ScriptDirectory(Guid resourceId) => Path.Combine(ScriptsRoot, resourceId.ToString("N"));

    public static string ScriptPath(Guid resourceId, string name, ScriptConfig config) =>
        Path.Combine(ScriptDirectory(resourceId), $"{Slug(name)}.{config.Extension}");

    /// <summary>
    /// Writes the script to its on-disk home (idempotent: rewrites only when the content changed,
    /// removes stale siblings from renames/language switches) and returns the path.
    /// </summary>
    public static string Materialize(Guid resourceId, string name, ScriptConfig config)
    {
        var directory = ScriptDirectory(resourceId);
        Directory.CreateDirectory(directory);
        var path = ScriptPath(resourceId, name, config);
        var content = NormalizeCode(config);

        foreach (var stale in Directory.EnumerateFiles(directory).Where(f => !string.Equals(f, path, StringComparison.Ordinal)))
        {
            try { File.Delete(stale); } catch (IOException) { /* best effort */ }
        }

        if (!File.Exists(path) || File.ReadAllText(path) != content)
        {
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                       UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        return path;
    }

    /// <summary>Removes a deleted resource's materialized folder. Best effort.</summary>
    public static void RemoveMaterialized(Guid resourceId)
    {
        try
        {
            var directory = ScriptDirectory(resourceId);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale folder is harmless; the next materialization of that id (never) would overwrite it.
        }
    }

    /// <summary>The shell command Looper runs the script with (interpreter + quoted path + user args).</summary>
    public static string BuildCommand(ScriptConfig config, string scriptPath, string pythonCommand)
    {
        var interpreter = config.Language == ScriptLanguage.Bash ? "/bin/bash" : pythonCommand;
        var command = $"{interpreter} {ShellQuote(scriptPath)}";
        return string.IsNullOrWhiteSpace(config.Args) ? command : $"{command} {config.Args.Trim()}";
    }

    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string NormalizeCode(ScriptConfig config)
    {
        var code = config.Code.Replace("\r\n", "\n");
        return code.EndsWith('\n') ? code : code + "\n";
    }
}
