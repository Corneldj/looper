using System.Diagnostics;
using System.Text.Json;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// Runs Claude (headless CLI, same harness as agent runs) against a scratch copy of a script.
/// Claude edits the file in place — with permission to run the interpreter when the user allows
/// it — and the file on disk afterwards is the authoritative result, not Claude's narrative.
/// </summary>
public sealed class ScriptAssistant(IOptions<LooperOptions> options, ClaudeAuthProvider claudeAuth, ILogger<ScriptAssistant> logger)
{
    public sealed record AssistResult(bool Success, string? Code, string? Summary, string? Error, decimal CostUsd);

    public async Task<AssistResult> AssistAsync(string name, string description, ScriptLanguage language,
        string code, string instruction, bool allowRun, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(ScriptResources.ScriptsRoot, "_assist", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var fileName = $"{ScriptResources.Slug(name)}.{(language == ScriptLanguage.Bash ? "sh" : "py")}";
        var filePath = Path.Combine(workspace, fileName);
        await File.WriteAllTextAsync(filePath, code.Replace("\r\n", "\n"), cancellationToken);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = options.Value.ClaudeCommand,
                WorkingDirectory = workspace,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-p");
            startInfo.ArgumentList.Add(BuildPrompt(name, description, language, fileName, instruction, allowRun,
                options.Value.PythonCommand));
            startInfo.ArgumentList.Add("--output-format");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add("--model");
            startInfo.ArgumentList.Add("claude-opus-5");
            startInfo.ArgumentList.Add("--max-turns");
            startInfo.ArgumentList.Add(allowRun ? "20" : "8");
            startInfo.ArgumentList.Add("--permission-mode");
            startInfo.ArgumentList.Add("acceptEdits");
            if (allowRun)
            {
                var interpreter = language == ScriptLanguage.Bash ? "bash" : options.Value.PythonCommand;
                startInfo.ArgumentList.Add("--allowedTools");
                startInfo.ArgumentList.Add($"Bash({interpreter}:*)");
            }

            try
            {
                await claudeAuth.ApplyAsync(startInfo, cancellationToken);
            }
            catch (ClaudeAuthException ex)
            {
                return new AssistResult(false, null, null, ex.Message, 0);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(6));

            using var process = new Process { StartInfo = startInfo };
            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                return new AssistResult(false, null, null,
                    $"Could not start '{options.Value.ClaudeCommand}'. Install Claude Code or set Looper:ClaudeCommand. ({ex.Message})", 0);
            }

            process.StandardInput.Close();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            _ = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* gone */ }
                return new AssistResult(false, null, null, "Claude took too long to update the script (6 minute limit).", 0);
            }

            using var document = ClaudeCliExecutor.ExtractResultObject(await stdoutTask);
            if (document is null)
            {
                logger.LogWarning("Script assist produced no JSON result (exit {Code})", process.ExitCode);
                return new AssistResult(false, null, null, $"Claude CLI returned no result (exit code {process.ExitCode}).", 0);
            }

            var root = document.RootElement;
            var cost = root.TryGetProperty("total_cost_usd", out var costProp) ? costProp.GetDecimal() : 0m;
            var text = root.TryGetProperty("result", out var resultProp) ? resultProp.GetString() ?? "" : "";
            var isError = root.TryGetProperty("is_error", out var errorProp) && errorProp.ValueKind == JsonValueKind.True;
            if (isError)
            {
                return new AssistResult(false, null, null, string.IsNullOrWhiteSpace(text) ? "Claude reported an error." : text, cost);
            }

            // The file is the deliverable. A run that changed nothing is reported, not silently accepted.
            var updated = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, cancellationToken) : "";
            if (string.IsNullOrWhiteSpace(updated))
            {
                return new AssistResult(false, null, null, "Claude finished without writing the script file." +
                    (string.IsNullOrWhiteSpace(text) ? "" : $" It said: {Truncate(text, 600)}"), cost);
            }
            return new AssistResult(true, updated.TrimEnd() + "\n", Truncate(text.Trim(), 2000), null, cost);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    internal static string BuildPrompt(string name, string description, ScriptLanguage language, string fileName,
        string instruction, bool allowRun, string pythonCommand)
    {
        var languageName = language == ScriptLanguage.Bash ? "Bash" : "Python 3";
        var runNote = allowRun
            ? $"You MAY run it to verify your work ({(language == ScriptLanguage.Bash ? "bash" : pythonCommand)} {fileName}); " +
              "fix what fails. Use only the standard library and tools already installed — do not install packages."
            : "You may NOT run it in this session — reason carefully about correctness instead, and keep to the standard library.";

        return $"""
            You are editing a {languageName} script called "{name}" that belongs to Looper, an app that runs autonomous agent loops.
            {(string.IsNullOrWhiteSpace(description) ? "" : $"What the script is for: {description}\n")}
            The script lives in the current directory as {fileName}. Edit that file in place — it may be empty if this is a fresh script.

            THE USER'S INSTRUCTION:
            {instruction}

            Context the script can rely on when Looper runs it:
            - Environment variables LOOPER_API_URL (Looper's own API), LOOPER_RUN_ID and LOOPER_AGENT_ID are set on agent runs;
              credential resources attached to the agent are exposed as their configured env var names. Read them with os.environ /
              $VAR and fail with a clear message when one you need is missing.
            - Depending on how it's wired, the script runs on demand by the agent, BEFORE every loop iteration (its stdout is
              handed to the agent as context — print concise, useful output), or AFTER every iteration as a gate (exit non-zero
              to fail the run, with the reason on stdout/stderr).
            - Command-line arguments may be supplied; parse them with argparse (Python) or $1… (Bash) when the task needs any.
            - Keep it self-contained, robust and readable: a docstring or header comment stating what it does, explicit exit codes,
              no interactive prompts, no secrets in the source.

            {runNote}

            When you are done, reply with a short summary (a few sentences) of what the script now does and what you changed.
            """;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
