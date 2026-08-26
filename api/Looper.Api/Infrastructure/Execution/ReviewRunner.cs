using System.Diagnostics;
using System.Text.Json;
using Looper.Api.Domain;
using Microsoft.Extensions.Options;

namespace Looper.Api.Infrastructure.Execution;

public sealed record ReviewVerdict(
    bool Pass,
    string Summary,
    string? FixInstructions,
    decimal CostUsd,
    bool Inconclusive);

/// <summary>
/// Runs one independent review: a fresh-context reviewer (read-only tools, its own model,
/// no stake in declaring victory) inspects the worker's output against the rubric and
/// returns pass/fail with fix instructions. Deliberately not a sub-agent: the worker
/// neither invokes nor sees the reviewer — the harness does, and the harness gates.
/// An unparseable verdict fails closed: a gate that shrugs is not a gate.
/// </summary>
public sealed class ReviewRunner(IOptions<LooperOptions> options, ILogger<ReviewRunner> logger)
{
    public async Task<ReviewVerdict> ReviewAsync(
        LoopAgent agent,
        Resource reviewerResource,
        ReviewerConfig config,
        string workingDirectory,
        IReadOnlyList<string> additionalDirectories,
        string? workerReport,
        RunLogWriter log,
        CancellationToken cancellationToken)
    {
        var model = string.IsNullOrWhiteSpace(config.Model) ? agent.Model : config.Model;
        await log("info", $"Review '{reviewerResource.Name}' started on {model}.");

        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.ClaudeCommand,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(BuildReviewPrompt(agent, config, workerReport));
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(model);
        startInfo.ArgumentList.Add("--max-turns");
        startInfo.ArgumentList.Add("15");
        startInfo.ArgumentList.Add("--allowedTools");
        startInfo.ArgumentList.Add("Read,Grep,Glob");
        foreach (var dir in additionalDirectories)
        {
            startInfo.ArgumentList.Add("--add-dir");
            startInfo.ArgumentList.Add(dir);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            await log("error", $"Reviewer could not start the Claude CLI: {ex.Message}");
            return new ReviewVerdict(false, "Reviewer could not start.", null, 0, Inconclusive: true);
        }

        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        _ = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* gone */ }
            throw;
        }

        using var document = ClaudeCliExecutor.ExtractResultObject(await stdoutTask);
        if (document is null)
        {
            await log("error", $"Reviewer produced no parseable CLI output (exit code {process.ExitCode}); failing closed.");
            return new ReviewVerdict(false, "Reviewer produced no output.", null, 0, Inconclusive: true);
        }

        var root = document.RootElement;
        var cost = root.TryGetProperty("total_cost_usd", out var costProp) ? costProp.GetDecimal() : 0m;
        var resultText = root.TryGetProperty("result", out var resultProp) ? resultProp.GetString() : null;

        var verdict = ParseVerdict(resultText) with { CostUsd = cost };
        var label = verdict.Inconclusive ? "INCONCLUSIVE (failing closed)" : verdict.Pass ? "PASS" : "FAIL";
        await log(verdict.Pass ? "info" : "warn",
            $"Review '{reviewerResource.Name}': {label} — {Truncate(verdict.Summary, 400)}");
        if (!verdict.Pass && verdict.FixInstructions is not null)
        {
            await log("info", $"Fix instructions: {Truncate(verdict.FixInstructions, 1000)}");
        }

        logger.LogInformation("Review {Reviewer} for agent {Agent}: {Verdict} (${Cost})",
            reviewerResource.Name, agent.Name, label, cost);
        return verdict;
    }

    /// <summary>Extracts the last JSON object carrying a "verdict" key from the reviewer's prose.</summary>
    internal static ReviewVerdict ParseVerdict(string? resultText)
    {
        if (!string.IsNullOrWhiteSpace(resultText))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(resultText);
            JsonDocument? best = null;
            for (var index = Array.IndexOf(bytes, (byte)'{'); index >= 0; index = Array.IndexOf(bytes, (byte)'{', index + 1))
            {
                try
                {
                    var reader = new Utf8JsonReader(bytes.AsSpan(index), isFinalBlock: true, default);
                    if (!JsonDocument.TryParseValue(ref reader, out var candidate)) continue;
                    if (candidate.RootElement.ValueKind == JsonValueKind.Object
                        && candidate.RootElement.TryGetProperty("verdict", out _))
                    {
                        best?.Dispose();
                        best = candidate;
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }
                catch (JsonException)
                {
                    // keep scanning
                }
            }

            if (best is not null)
            {
                using (best)
                {
                    var root = best.RootElement;
                    var pass = string.Equals(root.GetProperty("verdict").GetString(), "pass", StringComparison.OrdinalIgnoreCase);
                    var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                    var fix = root.TryGetProperty("fixInstructions", out var f) ? f.GetString() : null;
                    return new ReviewVerdict(pass, summary, string.IsNullOrWhiteSpace(fix) ? null : fix, 0, Inconclusive: false);
                }
            }
        }

        return new ReviewVerdict(false, "Reviewer returned no parseable verdict.", null, 0, Inconclusive: true);
    }

    private static string BuildReviewPrompt(LoopAgent agent, ReviewerConfig config, string? workerReport) =>
        $$"""
        You are an independent reviewer. An autonomous agent just completed a work iteration in this
        directory; your job is to judge whether the work is acceptable. You have read-only access —
        inspect the actual files and changes, not just the report. Be skeptical: the worker's report
        claims success by default, and your value is catching what it glossed over.

        The worker's standing task:
        {{agent.Prompt}}

        The worker's report for this iteration:
        {{Truncate(workerReport ?? "(no report)", 4000)}}

        Review rubric — the work passes only if it meets ALL of this:
        {{config.Rubric}}

        End your response with EXACTLY one JSON object (no code fence) shaped:
        {"verdict":"pass"|"fail","summary":"<one short paragraph on what you checked and found>","fixInstructions":"<only when failing: concrete, actionable steps the worker must take>"}
        """;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
