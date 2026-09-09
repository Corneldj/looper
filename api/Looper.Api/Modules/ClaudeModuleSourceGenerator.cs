using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.Extensions.Options;

namespace Looper.Api.Modules;

/// <summary>
/// Asks Claude (via the Claude Agent SDK headless CLI, same harness the agent runner uses)
/// to write a resource-type module for a natural-language description.
/// </summary>
public sealed partial class ClaudeModuleSourceGenerator(
    IOptions<LooperOptions> options,
    ClaudeAuthProvider claudeAuth,
    ILogger<ClaudeModuleSourceGenerator> logger) : IModuleSourceGenerator
{
    public async Task<ModuleGenerationResult> GenerateAsync(string description, IReadOnlyList<string>? previousErrors,
        string? previousSource, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.ClaudeCommand,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(BuildPrompt(description, previousErrors, previousSource));
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add("claude-opus-5");
        startInfo.ArgumentList.Add("--max-turns");
        startInfo.ArgumentList.Add("3");
        startInfo.ArgumentList.Add("--append-system-prompt");
        startInfo.ArgumentList.Add("You write a single self-contained C# file. Respond with ONLY one ```csharp code block and nothing else.");

        try
        {
            await claudeAuth.ApplyAsync(startInfo, cancellationToken);
        }
        catch (ClaudeAuthException ex)
        {
            return new ModuleGenerationResult(false, null, ex.Message, 0);
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
            return new ModuleGenerationResult(false, null,
                $"Could not start '{options.Value.ClaudeCommand}'. Install Claude Code (code.claude.com) or set Looper:ClaudeCommand. ({ex.Message})", 0);
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
            return new ModuleGenerationResult(false, null, "Claude took too long to generate the module (6 minute limit).", 0);
        }

        var stdout = await stdoutTask;
        using var document = Infrastructure.Execution.ClaudeCliExecutor.ExtractResultObject(stdout);
        if (document is null)
        {
            logger.LogWarning("Module generation produced no JSON result (exit {Code})", process.ExitCode);
            return new ModuleGenerationResult(false, null, $"Claude CLI returned no result (exit code {process.ExitCode}).", 0);
        }

        var root = document.RootElement;
        var cost = root.TryGetProperty("total_cost_usd", out var costProp) ? costProp.GetDecimal() : 0m;
        var text = root.TryGetProperty("result", out var resultProp) ? resultProp.GetString() : null;
        var isError = root.TryGetProperty("is_error", out var errorProp) && errorProp.ValueKind == JsonValueKind.True;

        if (isError || string.IsNullOrWhiteSpace(text))
        {
            return new ModuleGenerationResult(false, null, string.IsNullOrWhiteSpace(text) ? "Claude returned an empty response." : text, cost);
        }

        var source = ExtractCode(text);
        return string.IsNullOrWhiteSpace(source)
            ? new ModuleGenerationResult(false, null, "Claude's response contained no C# code block.", cost)
            : new ModuleGenerationResult(true, source, null, cost);
    }

    private static string BuildPrompt(string description, IReadOnlyList<string>? previousErrors, string? previousSource)
    {
        var retrySection = previousErrors is { Count: > 0 }
            ? $"""

              Your previous attempt failed to compile. Fix these errors and return the complete corrected file:
              {string.Join("\n", previousErrors)}

              Previous source:
              ```csharp
              {previousSource}
              ```
              """
            : "";

        return $$"""
            Write a C# resource-type module for Looper, a loop-engineering app. A resource type describes a capability users can attach to AI agents. The user wants this new resource type:

            "{{description}}"

            Requirements:
            - One file, one public sealed class implementing Looper.Api.Modules.IResourceTypeModule.
            - Target .NET 10, nullable enabled. Only use the BCL and Looper.Api.Modules types. No NuGet packages, no file/network IO in property getters.
            - The contract (already referenced, do not redeclare it):

            namespace Looper.Api.Modules;
            public interface IResourceTypeModule
            {
                string TypeKey { get; }          // stable PascalCase identifier, letters/digits only
                string DisplayName { get; }
                string Icon { get; }             // one emoji
                string Blurb { get; }            // one sentence
                IReadOnlyList<ResourceField> Fields { get; }
                ResourceContribution Contribute(ResourceModuleContext context);
                void PrepareRun(ResourceModuleContext context) { }  // optional: idempotent workspace setup before each run
            }
            public enum ResourceFieldKind { Text, Multiline, Number, Boolean, Password, Select, Path }
            public sealed record ResourceField(string Key, string Label, ResourceFieldKind Kind, bool Required = false, string? Hint = null, string[]? Options = null, string? Placeholder = null);
            // ResourceModuleContext: string ConfigJson; T? GetConfig<T>() where T : class; string? GetString(string key); bool GetBool(string key); double? GetNumber(string key)
            // ResourceContribution (all optional to fill): Dictionary<string,string> EnvironmentVariables; List<string> SystemPromptRules; List<string> PromptSections; List<string> AdditionalDirectories; Dictionary<string,McpServerSpec> McpServers
            // McpServerSpec(string Transport = "stdio", string? Command = null, string[]? Args = null, Dictionary<string,string>? Env = null, string? Url = null)

            Guidance:
            - Fields drive the form users fill in; field Keys are camelCase and become the JSON config keys.
            - Use ResourceFieldKind.Password for any secret; expose secrets to the run ONLY as EnvironmentVariables.
            - Use ResourceFieldKind.Path for filesystem locations — the UI attaches a folder browser to those fields.
            - In Contribute, read config values with context.GetString/GetBool/GetNumber, handle missing values gracefully (skip, don't throw).
            - PromptSections should tell the agent what the resource is and how to use it (mention env var names it can rely on).
            - Example shape:

            ```csharp
            using Looper.Api.Modules;

            public sealed class SlackWebhookModule : IResourceTypeModule
            {
                public string TypeKey => "SlackWebhook";
                public string DisplayName => "Slack Webhook";
                public string Icon => "💬";
                public string Blurb => "Lets the agent post updates to a Slack channel.";

                public IReadOnlyList<ResourceField> Fields { get; } =
                [
                    new("webhookUrl", "Webhook URL", ResourceFieldKind.Password, Required: true, Hint: "Incoming webhook URL from Slack."),
                    new("channel", "Channel", ResourceFieldKind.Text, Placeholder: "#deploys")
                ];

                public ResourceContribution Contribute(ResourceModuleContext context)
                {
                    var contribution = new ResourceContribution();
                    var url = context.GetString("webhookUrl");
                    if (string.IsNullOrWhiteSpace(url)) return contribution;

                    contribution.EnvironmentVariables["SLACK_WEBHOOK_URL"] = url;
                    var channel = context.GetString("channel");
                    contribution.PromptSections.Add(
                        $"A Slack incoming webhook is available via the SLACK_WEBHOOK_URL environment variable{(string.IsNullOrWhiteSpace(channel) ? "" : $" for {channel}")}. Post concise status updates there with curl when you finish significant work.");
                    return contribution;
                }
            }
            ```
            {{retrySection}}
            Respond with ONLY the complete C# file in a single ```csharp code block.
            """;
    }

    private static string? ExtractCode(string text)
    {
        var match = CodeBlock().Match(text);
        if (match.Success) return match.Groups[1].Value.Trim();

        // No fences — accept raw source if it plausibly is the file itself.
        var trimmed = text.Trim();
        return trimmed.Contains("IResourceTypeModule") && trimmed.Contains("class") ? trimmed : null;
    }

    [GeneratedRegex("```(?:csharp|cs)?\\s*\\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();
}
