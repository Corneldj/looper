using System.Text.Json;
using Looper.Api.Domain;

namespace Looper.Api.Features.Runs;

public sealed record RunSummaryDto(
    Guid Id,
    Guid AgentId,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    RunStatus Status,
    RunTrigger Trigger,
    bool Escalated,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    int NumTurns,
    long DurationMs,
    bool? TestsPassed,
    string? ErrorMessage);

public sealed record RunLogEntryDto(
    DateTime TimestampUtc,
    string Level,
    string Message);

/// <summary>Mirrors the shape serialized into <see cref="AgentRun.TestResultsJson"/> by the testing action runner.</summary>
public sealed record TestingActionResultDto(
    string Name,
    string Command,
    int ExitCode,
    bool Passed,
    long DurationMs,
    string Output);

public sealed record RunDetailDto(
    Guid Id,
    Guid AgentId,
    string AgentName,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc,
    RunStatus Status,
    RunTrigger Trigger,
    bool Escalated,
    decimal CostUsd,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheCreationTokens,
    int NumTurns,
    long DurationMs,
    bool? TestsPassed,
    string? ErrorMessage,
    string? EscalationReason,
    string? ResultText,
    List<TestingActionResultDto>? TestResults,
    List<RunLogEntryDto> Logs);

public static class RunMapper
{
    private const int SummaryErrorMaxLength = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static RunSummaryDto ToSummaryDto(this AgentRun run) => new(
        run.Id,
        run.AgentId,
        run.StartedAtUtc,
        run.CompletedAtUtc,
        run.Status,
        run.Trigger,
        run.Escalated,
        run.CostUsd,
        run.InputTokens,
        run.OutputTokens,
        run.CacheReadTokens,
        run.NumTurns,
        run.DurationMs,
        run.TestsPassed,
        Truncate(run.ErrorMessage, SummaryErrorMaxLength));

    public static RunDetailDto ToDetailDto(this AgentRun run, string agentName, IEnumerable<RunLogEntry> logs) => new(
        run.Id,
        run.AgentId,
        agentName,
        run.StartedAtUtc,
        run.CompletedAtUtc,
        run.Status,
        run.Trigger,
        run.Escalated,
        run.CostUsd,
        run.InputTokens,
        run.OutputTokens,
        run.CacheReadTokens,
        run.CacheCreationTokens,
        run.NumTurns,
        run.DurationMs,
        run.TestsPassed,
        run.ErrorMessage,
        run.EscalationReason,
        run.ResultText,
        ParseTestResults(run.TestResultsJson),
        logs.Select(l => new RunLogEntryDto(l.TimestampUtc, l.Level, l.Message)).ToList());

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private static List<TestingActionResultDto>? ParseTestResults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<TestingActionResultDto>>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
