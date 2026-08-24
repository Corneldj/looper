using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Looper.Api.Domain;
using Looper.Api.Modules;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

/// <summary>
/// Runs a loop iteration through the Claude Agent SDK harness by spawning the Claude Code CLI
/// in headless mode (claude -p --output-format json) and translating Looper resources into
/// CLI capabilities: MCP servers, directories, rules, sub-agents, credentials — plus whatever
/// dynamic resource-type modules contribute.
/// </summary>
public sealed class ClaudeCliExecutor(
    IOptions<LooperOptions> options,
    ResourceModuleRegistry moduleRegistry,
    ILogger<ClaudeCliExecutor> logger) : IAgentExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentExecutionOutcome> ExecuteAsync(
        AgentExecutionContext context, RunLogWriter log, CancellationToken cancellationToken)
    {
        var agent = context.Agent;
        var resources = context.Resources;

        var (workingDirectory, additionalDirectories) = AgentWorkspace.Resolve(agent, resources);
        var contributions = await CollectModuleContributions(resources, log);

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.ClaudeCommand,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        BuildArguments(startInfo.ArgumentList, agent, resources, additionalDirectories, contributions);

        foreach (var (key, value) in AgentWorkspace.ResolveEnvironment(resources))
        {
            startInfo.Environment[key] = value;
        }
        foreach (var contribution in contributions)
        {
            foreach (var (key, value) in contribution.EnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        await log("info", $"Launching Claude Agent SDK run: model={agent.Model}, effort={agent.Effort}, cwd={workingDirectory}");
        await log("info", $"claude {string.Join(' ', RedactedArguments(startInfo.ArgumentList, agent))}");

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            var hint = $"Could not start '{options.Value.ClaudeCommand}'. Install Claude Code or set Looper:ClaudeCommand " +
                       $"to the CLI path in appsettings.json. ({ex.Message})";
            await log("error", hint);
            return Failure(hint, stopwatch.ElapsedMilliseconds);
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        stopwatch.Stop();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            foreach (var line in stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(20))
            {
                await log("warn", $"[stderr] {line.TrimEnd()}");
            }
        }

        return ParseResult(stdout, process.ExitCode, stopwatch.ElapsedMilliseconds, log).Result;
    }

    /// <summary>Asks each dynamic module what its resources add to this run. A faulty module skips, never sinks, the run.</summary>
    private async Task<IReadOnlyList<ResourceContribution>> CollectModuleContributions(
        IReadOnlyList<Resource> resources, RunLogWriter log)
    {
        var contributions = new List<ResourceContribution>();
        foreach (var resource in resources.Where(r => r.Type == ResourceType.Custom && r.CustomTypeKey is not null))
        {
            if (!moduleRegistry.TryGet(resource.CustomTypeKey!, out var module))
            {
                await log("warn", $"Resource '{resource.Name}' uses unknown type '{resource.CustomTypeKey}'; skipped.");
                continue;
            }

            try
            {
                contributions.Add(module.Contribute(new ResourceModuleContext(resource.ConfigJson)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Module {TypeKey} failed to contribute for resource {Resource}",
                    resource.CustomTypeKey, resource.Name);
                await log("warn", $"Resource '{resource.Name}' ({resource.CustomTypeKey}) failed to apply: {ex.Message}. Skipped.");
            }
        }

        return contributions;
    }

    private void BuildArguments(ICollection<string> args, LoopAgent agent, IReadOnlyList<Resource> resources,
        IReadOnlyList<string> additionalDirectories, IReadOnlyList<ResourceContribution> contributions)
    {
        args.Add("-p");
        args.Add(BuildPrompt(agent, resources, contributions));
        args.Add("--output-format");
        args.Add("json");
        args.Add("--model");
        args.Add(agent.Model);
        args.Add("--effort");
        args.Add(agent.Effort.ToString().ToLowerInvariant());
        args.Add("--max-turns");
        args.Add(agent.MaxTurns.ToString());

        if (agent.MaxBudgetUsd is { } budget and > 0)
        {
            args.Add("--max-budget-usd");
            args.Add(budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        args.Add("--permission-mode");
        args.Add(agent.BypassPermissions ? "bypassPermissions" : "acceptEdits");

        if (!string.IsNullOrWhiteSpace(agent.AllowedTools))
        {
            args.Add("--allowedTools");
            args.Add(agent.AllowedTools);
        }

        foreach (var dir in additionalDirectories
                     .Concat(contributions.SelectMany(c => c.AdditionalDirectories))
                     .Distinct())
        {
            args.Add("--add-dir");
            args.Add(dir);
        }

        var systemPrompt = BuildAppendedSystemPrompt(resources, contributions);
        if (systemPrompt is not null)
        {
            args.Add("--append-system-prompt");
            args.Add(systemPrompt);
        }

        var mcpConfig = BuildMcpConfig(resources, contributions);
        if (mcpConfig is not null)
        {
            args.Add("--mcp-config");
            args.Add(mcpConfig);
            args.Add("--strict-mcp-config");
        }

        var agentsConfig = BuildSubAgentsConfig(resources);
        if (agentsConfig is not null)
        {
            args.Add("--agents");
            args.Add(agentsConfig);
        }
    }

    private static string BuildPrompt(LoopAgent agent, IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceContribution> contributions)
    {
        var ragSections = resources
            .Where(r => r.Type == ResourceType.Rag)
            .Select(r => (r.Name, Config: ResourceConfig.Parse<RagConfig>(r)))
            .Where(r => !string.IsNullOrWhiteSpace(r.Config.Instructions)
                        || !string.IsNullOrWhiteSpace(r.Config.Path)
                        || !string.IsNullOrWhiteSpace(r.Config.Url))
            .ToList();

        var extraSections = contributions.SelectMany(c => c.PromptSections)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (ragSections.Count == 0 && extraSections.Count == 0) return agent.Prompt;

        var builder = new StringBuilder(agent.Prompt);
        if (ragSections.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine("Knowledge sources available for this task:");
            foreach (var (name, config) in ragSections)
            {
                builder.Append("- ").Append(name);
                if (!string.IsNullOrWhiteSpace(config.Path)) builder.Append($" (local: {config.Path})");
                if (!string.IsNullOrWhiteSpace(config.Url)) builder.Append($" (remote: {config.Url})");
                if (!string.IsNullOrWhiteSpace(config.Instructions)) builder.Append($" — {config.Instructions}");
                builder.AppendLine();
            }
        }

        foreach (var section in extraSections)
        {
            builder.AppendLine();
            builder.AppendLine(section.Trim());
        }

        return builder.ToString();
    }

    private static string? BuildAppendedSystemPrompt(IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceContribution> contributions)
    {
        var rules = resources
            .Where(r => r.Type == ResourceType.Rule)
            .Select(r => ResourceConfig.Parse<RuleConfig>(r).Text)
            .Concat(contributions.SelectMany(c => c.SystemPromptRules))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();

        return rules.Count == 0 ? null : string.Join("\n\n", rules);
    }

    private static string? BuildMcpConfig(IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceContribution> contributions)
    {
        var servers = new Dictionary<string, object>();

        foreach (var resource in resources.Where(r => r.Type == ResourceType.McpServer))
        {
            var config = ResourceConfig.Parse<McpServerConfig>(resource);
            var key = Slugify(resource.Name);

            if (config.Transport is "http" or "sse" && !string.IsNullOrWhiteSpace(config.Url))
            {
                servers[key] = new { type = config.Transport, url = config.Url };
            }
            else if (!string.IsNullOrWhiteSpace(config.Command))
            {
                servers[key] = new { command = config.Command, args = config.Args, env = config.Env };
            }
        }

        foreach (var (name, spec) in contributions.SelectMany(c => c.McpServers))
        {
            var key = Slugify(name);
            while (servers.ContainsKey(key)) key += "-x";

            if (spec.Transport is "http" or "sse" && !string.IsNullOrWhiteSpace(spec.Url))
            {
                servers[key] = new { type = spec.Transport, url = spec.Url };
            }
            else if (!string.IsNullOrWhiteSpace(spec.Command))
            {
                servers[key] = new
                {
                    command = spec.Command,
                    args = spec.Args ?? [],
                    env = spec.Env ?? new Dictionary<string, string>()
                };
            }
        }

        return servers.Count == 0
            ? null
            : JsonSerializer.Serialize(new { mcpServers = servers }, JsonOptions);
    }

    private static string? BuildSubAgentsConfig(IReadOnlyList<Resource> resources)
    {
        var agents = new Dictionary<string, object>();

        foreach (var resource in resources.Where(r => r.Type == ResourceType.SubAgent))
        {
            var config = ResourceConfig.Parse<SubAgentConfig>(resource);
            if (string.IsNullOrWhiteSpace(config.Prompt)) continue;

            var definition = new Dictionary<string, object>
            {
                ["description"] = config.Description ?? resource.Description,
                ["prompt"] = config.Prompt
            };
            if (!string.IsNullOrWhiteSpace(config.Tools))
            {
                definition["tools"] = config.Tools.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
            if (!string.IsNullOrWhiteSpace(config.Model)) definition["model"] = config.Model;

            agents[Slugify(resource.Name)] = definition;
        }

        return agents.Count == 0 ? null : JsonSerializer.Serialize(agents, JsonOptions);
    }

    private async Task<AgentExecutionOutcome> ParseResult(string stdout, int exitCode, long elapsedMs, RunLogWriter log)
    {
        using var document = ExtractResultObject(stdout);
        if (document is null)
        {
            var message = exitCode == 0
                ? "Claude CLI returned no parseable JSON result."
                : $"Claude CLI exited with code {exitCode} and produced no parseable JSON result.";
            await log("error", message + (string.IsNullOrWhiteSpace(stdout) ? "" : $" stdout: {Truncate(stdout, 2000)}"));
            return Failure(message, elapsedMs);
        }

        {
            var root = document.RootElement;

            var isError = (root.TryGetProperty("is_error", out var isErrorProp) && isErrorProp.ValueKind == JsonValueKind.True)
                          || exitCode != 0;
            var resultText = root.TryGetProperty("result", out var resultProp) ? resultProp.GetString() : null;
            var costUsd = root.TryGetProperty("total_cost_usd", out var costProp) ? costProp.GetDecimal() : 0m;
            var numTurns = root.TryGetProperty("num_turns", out var turnsProp) ? turnsProp.GetInt32() : 0;
            var durationMs = root.TryGetProperty("duration_ms", out var durationProp) ? durationProp.GetInt64() : elapsedMs;

            long inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheCreation = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                inputTokens = GetLong(usage, "input_tokens");
                outputTokens = GetLong(usage, "output_tokens");
                cacheRead = GetLong(usage, "cache_read_input_tokens");
                cacheCreation = GetLong(usage, "cache_creation_input_tokens");
            }

            if (isError)
            {
                var message = string.IsNullOrWhiteSpace(resultText) ? $"Run failed (exit code {exitCode})." : resultText;
                await log("error", Truncate(message!, 4000));
            }
            else
            {
                await log("info", $"Run completed: {numTurns} turns, ${costUsd:F4}, {inputTokens + cacheRead:N0} in / {outputTokens:N0} out tokens");
            }

            return new AgentExecutionOutcome(
                Success: !isError,
                ResultText: resultText,
                ErrorMessage: isError ? Truncate(resultText ?? $"Exit code {exitCode}", 4000) : null,
                CostUsd: costUsd,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                CacheReadTokens: cacheRead,
                CacheCreationTokens: cacheCreation,
                NumTurns: numTurns,
                DurationMs: durationMs);
        }
    }

    private static readonly string[] ResultMarkerKeys = ["result", "total_cost_usd", "is_error", "usage", "num_turns"];

    /// <summary>
    /// Finds the CLI result object in stdout, tolerating noise before or after it (npx banners,
    /// version-manager warnings, stray braces). Prefers an object carrying known result keys.
    /// </summary>
    internal static JsonDocument? ExtractResultObject(string stdout)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(stdout);
        JsonDocument? fallback = null;

        for (var index = Array.IndexOf(bytes, (byte)'{'); index >= 0; index = Array.IndexOf(bytes, (byte)'{', index + 1))
        {
            JsonDocument? document = null;
            try
            {
                var reader = new Utf8JsonReader(bytes.AsSpan(index), isFinalBlock: true, default);
                if (!JsonDocument.TryParseValue(ref reader, out document)) continue;
            }
            catch (JsonException)
            {
                continue;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                continue;
            }

            if (ResultMarkerKeys.Any(key => document.RootElement.TryGetProperty(key, out _)))
            {
                fallback?.Dispose();
                return document;
            }

            if (fallback is null) fallback = document;
            else document.Dispose();
        }

        return fallback;
    }

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number ? prop.GetInt64() : 0;

    private static AgentExecutionOutcome Failure(string message, long elapsedMs) =>
        new(false, null, message, 0, 0, 0, 0, 0, 0, elapsedMs);

    private static IEnumerable<string> RedactedArguments(IEnumerable<string> args, LoopAgent agent)
    {
        // The prompt can be long and MCP configs may embed env secrets; keep the log line compact and safe.
        string? previous = null;
        foreach (var arg in args)
        {
            var value = previous switch
            {
                "-p" => $"\"{Truncate(agent.Prompt.ReplaceLineEndings(" "), 120)}\"",
                "--mcp-config" => "<mcp-config>",
                "--agents" => "<sub-agents>",
                "--append-system-prompt" => "<rules>",
                _ => arg
            };
            yield return value;
            previous = arg;
        }
    }

    private static string Slugify(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return string.IsNullOrEmpty(slug) ? "server" : slug;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Process already gone.
        }
    }
}
