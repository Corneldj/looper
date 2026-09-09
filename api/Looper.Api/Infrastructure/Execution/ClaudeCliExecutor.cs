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
    ClaudeAuthProvider claudeAuth,
    ResourceModuleRegistry moduleRegistry,
    GraphContextService graphContext,
    ILogger<ClaudeCliExecutor> logger) : IAgentExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentExecutionOutcome> ExecuteAsync(
        AgentExecutionContext context, RunLogWriter log, CancellationToken cancellationToken)
    {
        var agent = context.Agent;
        var resources = context.Resources;

        var (workingDirectory, additionalDirectories) = AgentWorkspace.Resolve(agent, resources);
        var (contributions, resourceErrors) = await CollectModuleContributions(agent, resources, log);

        // Decoupled memory: retrieval happens harness-side, before the model sees anything —
        // the run starts already knowing what the memory layer holds about its task.
        var memoryContext = await graphContext.BuildPreambleSectionsAsync(
            agent, resources, context.TriggerEvents, log, cancellationToken);

        // Dynamic Workspaces pools: the agent claims per-unit directories under each pool's
        // root at run time, so the root itself must exist and be reachable via --add-dir.
        var poolRoots = new List<string>();
        foreach (var (_, poolConfig) in WorkspacePools(resources))
        {
            try
            {
                var root = Path.GetFullPath(poolConfig.RootPath.Trim());
                Directory.CreateDirectory(root);
                poolRoots.Add(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                resourceErrors.Add($"Workspace pool root '{poolConfig.RootPath}' is not usable: {ex.Message}");
            }
        }
        additionalDirectories = additionalDirectories.Concat(poolRoots).Distinct().ToList();

        // Fail closed: a run that starts without a resource it was given would look like a
        // success while missing its premises. Better an honest failure the user can fix.
        if (resourceErrors.Count > 0)
        {
            var message = "The run was not started because resources failed to apply: " + string.Join(" | ", resourceErrors);
            await log("error", message);
            return Failure(Truncate(message, 4000), 0);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.ClaudeCommand,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };

        BuildArguments(startInfo.ArgumentList, agent, resources, additionalDirectories, contributions, context.FixInstructions, context.TriggerEvents, memoryContext, context.ScriptOutputs,
            Mcp.LooperTools.ServerUrl(options.Value.PublicUrl, context.RunId));

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

        // The PRD-to-PR feedback channel: the run can report the PRs it ships and escalate
        // to a human, which is what the delivery metrics are computed from.
        startInfo.Environment["LOOPER_API_URL"] = options.Value.PublicUrl.TrimEnd('/');
        startInfo.Environment["LOOPER_RUN_ID"] = context.RunId.ToString();
        startInfo.Environment["LOOPER_AGENT_ID"] = agent.Id.ToString();

        // Credentials come from Settings (subscription login or a stored API key), never from
        // whatever happens to be in the server's environment.
        string auth;
        try
        {
            auth = await claudeAuth.ApplyAsync(startInfo, cancellationToken);
        }
        catch (ClaudeAuthException ex)
        {
            await log("error", ex.Message);
            return Failure(ex.Message, 0);
        }

        await log("info", $"Launching Claude Agent SDK run: model={agent.Model}, effort={agent.Effort}, auth={auth}, cwd={workingDirectory}");
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

    /// <summary>
    /// Asks each dynamic module what its resources add to this run. A resource that cannot be
    /// applied is reported as an error — the caller fails the run rather than running without it.
    /// </summary>
    private async Task<(IReadOnlyList<ResourceContribution> Contributions, List<string> Errors)> CollectModuleContributions(
        LoopAgent agent, IReadOnlyList<Resource> resources, RunLogWriter log)
    {
        var contributions = new List<ResourceContribution>();
        var errors = new List<string>();
        foreach (var resource in resources.Where(r => r.Type == ResourceType.Custom && r.CustomTypeKey is not null))
        {
            if (!moduleRegistry.TryGet(resource.CustomTypeKey!, out var module))
            {
                errors.Add($"Resource '{resource.Name}' uses the resource type '{resource.CustomTypeKey}', which is not installed.");
                continue;
            }

            try
            {
                var moduleContext = new ResourceModuleContext(resource.ConfigJson, agent.Id, agent.Name,
                    resource.Id, resource.Name, resource.Description);
                module.PrepareRun(moduleContext);
                contributions.Add(module.Contribute(moduleContext));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Module {TypeKey} failed to contribute for resource {Resource}",
                    resource.CustomTypeKey, resource.Name);
                errors.Add($"Resource '{resource.Name}' ({resource.CustomTypeKey}) failed to apply: {ex.Message}");
            }
        }

        return (contributions, errors);
    }

    private void BuildArguments(ICollection<string> args, LoopAgent agent, IReadOnlyList<Resource> resources,
        IReadOnlyList<string> additionalDirectories, IReadOnlyList<ResourceContribution> contributions,
        string? fixInstructions, string? triggerEvents = null,
        IReadOnlyList<string>? memoryContext = null, string? scriptOutputs = null, string? looperServerUrl = null)
    {
        args.Add("-p");
        args.Add(BuildPrompt(agent, resources, contributions, fixInstructions, triggerEvents, memoryContext, scriptOutputs));
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
            // A tool allowlist never locks the run out of Looper's own tools — they are how it reports.
            args.Add("--allowedTools");
            args.Add(agent.AllowedTools.Trim().TrimEnd(',') + "," + Mcp.LooperTools.QualifiedName("*").TrimEnd('*').TrimEnd('_'));
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

        var mcpConfig = BuildMcpConfig(resources, contributions, looperServerUrl);
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

    internal static string BuildPrompt(LoopAgent agent, IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceContribution> contributions, string? fixInstructions = null,
        string? triggerEvents = null,
        IReadOnlyList<string>? memoryContext = null, string? scriptOutputs = null)
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
        extraSections.AddRange(WorkspacePools(resources).Select(p => BuildWorkspaceProtocol(p.Resource, p.Config)));
        foreach (var actionResource in resources.Where(r => r.Type == ResourceType.UserAction))
        {
            extraSections.Add(BuildUserActionProtocol(ResourceConfig.Parse<UserActionConfig>(actionResource)));
        }
        // Before-run script output sits right after the memory preamble: both are harness-provided
        // inputs the agent should read before the resource protocols that follow.
        if (!string.IsNullOrWhiteSpace(scriptOutputs))
        {
            extraSections.Insert(0, scriptOutputs);
        }
        if (memoryContext is { Count: > 0 })
        {
            extraSections.InsertRange(0, memoryContext);
        }
        if (!string.IsNullOrWhiteSpace(triggerEvents))
        {
            extraSections.Insert(0,
                "TRIGGERING EVENT(S) — this iteration was started by the following event(s); they are the reason you are running, so address them directly:\n"
                + triggerEvents);
        }
        if (!string.IsNullOrWhiteSpace(fixInstructions))
        {
            extraSections.Add(
                "REVISION REQUESTED — an independent reviewer examined your previous iteration and did not accept it. " +
                "Address these instructions before anything else, then re-verify your work:\n" + fixInstructions);
        }

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

    /// <summary>Standing instructions for reporting shipped work and handing off — always present on real runs.</summary>
    internal const string DeliveryProtocol =
        "LOOPER TOOLS: Looper is connected as an MCP server named 'looper' (tools appear as mcp__looper__<name>); each resource " +
        "you were given that needs a call from you shows up as one of its tools. Three are always there: " +
        "report_deliverable — the moment you finish anything with a link (a pull request, a page, a document), register it; " +
        "never invent a link and only register work you actually finished. " +
        "escalate — when you are blocked on something only a human can decide or authorize; then stop cleanly. " +
        "raise_event — to signal other loops with a named event (dotted lowercase topic) for real, completed facts only. " +
        "Use these tools rather than calling Looper's HTTP API yourself.";

    private static string? BuildAppendedSystemPrompt(IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceContribution> contributions) =>
        string.Join("\n\n", CollectRules(resources, contributions).Append(DeliveryProtocol));

    internal static IEnumerable<(Resource Resource, WorkspacePoolConfig Config)> WorkspacePools(
        IReadOnlyList<Resource> resources) =>
        resources
            .Where(r => r.Type == ResourceType.WorkspacePool)
            .Select(r => (Resource: r, Config: ResourceConfig.Parse<WorkspacePoolConfig>(r)))
            .Where(p => !string.IsNullOrWhiteSpace(p.Config.RootPath));

    internal static string BuildWorkspaceProtocol(Resource pool, WorkspacePoolConfig config) =>
        $"DYNAMIC WORKSPACES — pool '{pool.Name}' (root {config.RootPath}). When your task is a distinct unit of work " +
        "(a feature, a fix, a project), claim a dedicated workspace for it with the Looper tool claim_workspace instead of working " +
        "in a shared directory — it returns the workspace path; cd there and read WORKBRIEF.md first (claiming the same unit again " +
        "returns the same workspace, so iterations resume where the last one left off). Before finishing an iteration, append a " +
        "handoff note to WORKBRIEF.md. When the unit is fully complete, call finish_workspace; list_workspaces shows what this " +
        "pool already holds.";

    internal static string BuildUserActionProtocol(UserActionConfig config) =>
        "USER ACTION REQUESTS: when you are blocked by something only the user can do or decide — unclear requirements, " +
        "a choice between real alternatives, a credential or approval you lack — raise a request with the Looper tool ask_user " +
        "instead of guessing or failing. " +
        "Then finish the iteration cleanly, summarising what you completed and what waits on the user. Raising a request is NOT " +
        "a failure — your loop simply pauses until the user completes it. You start every iteration from a clean context: the " +
        "user's answer is never handed to you as a message. It is recorded into your resources — as a standing rule in your " +
        "system prompt, or in your memory graph's inbox — so check your rules (and memory, if you have one) before raising the " +
        "same request again. If the user completed the action without recording anything, verify the state yourself and " +
        "continue; if it was not done correctly, raise a new request stating exactly what is still missing." +
        (string.IsNullOrWhiteSpace(config.Instructions) ? "" : " Guidance from the user on when to raise: " + config.Instructions);

    /// <summary>Standing rules from Rule resources, enabled Rule Set entries, and module contributions, in resource order.</summary>
    internal static IEnumerable<string> CollectRules(IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceContribution> contributions)
    {
        var rules = new List<string>();
        foreach (var resource in resources)
        {
            if (resource.Type == ResourceType.Rule)
            {
                rules.Add(ResourceConfig.Parse<RuleConfig>(resource).Text);
            }
            else if (resource.Type == ResourceType.RuleSet)
            {
                rules.AddRange(ResourceConfig.Parse<RuleSetConfig>(resource).Rules
                    .Where(rule => rule.Enabled)
                    .Select(rule => rule.Text));
            }
        }

        return rules
            .Concat(contributions.SelectMany(c => c.SystemPromptRules))
            .Where(t => !string.IsNullOrWhiteSpace(t));
    }

    internal static string? BuildMcpConfig(IReadOnlyList<Resource> resources,
        IReadOnlyList<ResourceContribution> contributions, string? looperServerUrl = null)
    {
        var servers = new Dictionary<string, object>();

        // Looper's own tools first, so a user server that happens to be called "looper" gets renamed, not the other way round.
        if (!string.IsNullOrWhiteSpace(looperServerUrl))
        {
            servers[Mcp.LooperTools.ServerName] = new { type = "http", url = looperServerUrl };
        }

        foreach (var resource in resources.Where(r => r.Type == ResourceType.McpServer))
        {
            var config = ResourceConfig.Parse<McpServerConfig>(resource);
            var key = Slugify(resource.Name);
            while (servers.ContainsKey(key)) key += "-x";   // Looper's own server keeps its name

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

        var outcome = InterpretResult(document.RootElement, exitCode, elapsedMs);
        if (!outcome.Success)
        {
            await log("error", outcome.ErrorMessage!);
        }
        else
        {
            await log("info", $"Run completed: {outcome.NumTurns} turns, ${outcome.CostUsd:F4}, " +
                              $"{outcome.InputTokens + outcome.CacheReadTokens:N0} in / {outcome.OutputTokens:N0} out tokens");
        }
        return outcome;
    }

    /// <summary>
    /// Reads the CLI's result object into an outcome, failing closed on every way a run can end
    /// without having done the work: a non-zero exit, an is_error flag, an error subtype (the
    /// turn or budget cap was hit before the task finished), or no result text at all.
    /// </summary>
    internal static AgentExecutionOutcome InterpretResult(JsonElement root, int exitCode, long elapsedMs)
    {
        var isErrorFlag = root.TryGetProperty("is_error", out var isErrorProp) && isErrorProp.ValueKind == JsonValueKind.True;
        var subtype = root.TryGetProperty("subtype", out var subtypeProp) && subtypeProp.ValueKind == JsonValueKind.String
            ? subtypeProp.GetString() ?? ""
            : "";
        var resultText = root.TryGetProperty("result", out var resultProp) && resultProp.ValueKind == JsonValueKind.String
            ? resultProp.GetString()
            : null;
        var costUsd = root.TryGetProperty("total_cost_usd", out var costProp) && costProp.ValueKind == JsonValueKind.Number ? costProp.GetDecimal() : 0m;
        var numTurns = root.TryGetProperty("num_turns", out var turnsProp) && turnsProp.ValueKind == JsonValueKind.Number ? turnsProp.GetInt32() : 0;
        var durationMs = root.TryGetProperty("duration_ms", out var durationProp) && durationProp.ValueKind == JsonValueKind.Number ? durationProp.GetInt64() : elapsedMs;

        long inputTokens = 0, outputTokens = 0, cacheRead = 0, cacheCreation = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            inputTokens = GetLong(usage, "input_tokens");
            outputTokens = GetLong(usage, "output_tokens");
            cacheRead = GetLong(usage, "cache_read_input_tokens");
            cacheCreation = GetLong(usage, "cache_creation_input_tokens");
        }

        var lastMessage = string.IsNullOrWhiteSpace(resultText) ? "" : $" Last message: {Truncate(resultText.Trim(), 2000)}";
        string? failure = null;
        if (exitCode != 0)
        {
            failure = string.IsNullOrWhiteSpace(resultText) ? $"Run failed (exit code {exitCode})." : resultText.Trim();
        }
        else if (isErrorFlag)
        {
            failure = string.IsNullOrWhiteSpace(resultText) ? "Run failed: the CLI reported an error without a message." : resultText.Trim();
        }
        else if (subtype.StartsWith("error", StringComparison.OrdinalIgnoreCase))
        {
            failure = subtype switch
            {
                "error_max_turns" => $"Stopped at the turn limit after {numTurns} turns before finishing the task — raise max turns or narrow the prompt.",
                "error_max_budget_usd" or "error_max_budget" => "Stopped at the budget cap before finishing the task.",
                _ => $"The CLI ended with '{subtype}' instead of a result."
            } + lastMessage;
        }
        else if (string.IsNullOrWhiteSpace(resultText))
        {
            failure = "The run ended without reporting anything — treated as a failure, never as a silent success.";
        }

        return new AgentExecutionOutcome(
            Success: failure is null,
            ResultText: resultText,
            ErrorMessage: failure is null ? null : Truncate(failure, 4000),
            CostUsd: costUsd,
            InputTokens: inputTokens,
            OutputTokens: outputTokens,
            CacheReadTokens: cacheRead,
            CacheCreationTokens: cacheCreation,
            NumTurns: numTurns,
            DurationMs: durationMs);
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
