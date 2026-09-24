using System.Globalization;
using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Features.Resources;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Looper.Api.Features.Settings;

/// <summary>
/// What the UI sees. The API key itself never leaves the server — only whether one is stored and
/// its tail. The run limits are the effective values: what is stored, else the appsettings fallback.
/// </summary>
public sealed record SettingsDto(
    ClaudeAuthMode ClaudeAuthMode,
    bool HasApiKey,
    string? ApiKeyHint,
    /// <summary>Wall-clock cap per run in minutes; 0 means no limit.</summary>
    int RunTimeoutMinutes,
    /// <summary>USD cap for runs whose agent sets no budget of its own; null means none.</summary>
    decimal? DefaultMaxBudgetUsd,
    DateTime? UpdatedAtUtc);

public static class SettingsMapper
{
    /// <summary>Reads every setting fresh — a save is visible on the very next read.</summary>
    public static async Task<SettingsDto> ReadAsync(LooperDbContext db, LooperOptions options, CancellationToken cancellationToken)
    {
        var auth = await ClaudeAuthProvider.ReadAsync(db, cancellationToken);
        var limits = await RunLimits.ReadAsync(db, options, cancellationToken);
        return new SettingsDto(auth.Mode, auth.HasApiKey, auth.ApiKeyHint, limits.TimeoutMinutes, limits.DefaultMaxBudgetUsd,
            Latest(auth.UpdatedAtUtc, limits.UpdatedAtUtc));
    }

    private static DateTime? Latest(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}

// ---------- read ----------

public sealed record GetSettingsQuery : IQuery<SettingsDto>;

public sealed class GetSettingsHandler(LooperDbContext db, IOptions<LooperOptions> options)
    : IQueryHandler<GetSettingsQuery, SettingsDto>
{
    public Task<SettingsDto> Handle(GetSettingsQuery query, CancellationToken cancellationToken) =>
        SettingsMapper.ReadAsync(db, options.Value, cancellationToken);
}

// ---------- update ----------

/// <summary>
/// <paramref name="ApiKey"/>: a new key replaces the stored one; null or the secret sentinel keeps
/// it. <paramref name="ClearApiKey"/> removes the stored key (and is refused while the mode needs one).
/// <paramref name="RunTimeoutMinutes"/> and <paramref name="DefaultMaxBudgetUsd"/>: null leaves the
/// stored value alone, so a save that only touches credentials cannot reset a limit.
/// <paramref name="ClearDefaultMaxBudget"/> removes the default budget.
/// </summary>
public sealed record UpdateSettingsCommand(
    ClaudeAuthMode ClaudeAuthMode,
    string? ApiKey,
    bool ClearApiKey = false,
    int? RunTimeoutMinutes = null,
    decimal? DefaultMaxBudgetUsd = null,
    bool ClearDefaultMaxBudget = false)
    : ICommand<SettingsDto>;

public sealed class UpdateSettingsValidator : AbstractValidator<UpdateSettingsCommand>
{
    public UpdateSettingsValidator()
    {
        RuleFor(c => c.ClaudeAuthMode).IsInEnum();
        RuleFor(c => c.ApiKey)
            .Must(key => ClaudeAuth.ValidateApiKey(key) is null)
            .WithMessage(c => ClaudeAuth.ValidateApiKey(c.ApiKey) ?? "")
            .When(c => c.ApiKey is not null && c.ApiKey != SecretMasker.Sentinel && c.ApiKey.Length > 0);
        RuleFor(c => c)
            .Must(c => !(c.ClearApiKey && !string.IsNullOrEmpty(c.ApiKey) && c.ApiKey != SecretMasker.Sentinel))
            .WithMessage("Either replace the API key or clear it, not both.");

        RuleFor(c => c.RunTimeoutMinutes)
            .InclusiveBetween(0, RunLimits.MaxTimeoutMinutes)
            .When(c => c.RunTimeoutMinutes.HasValue)
            .WithMessage($"The run time limit must be between 0 (no limit) and {RunLimits.MaxTimeoutMinutes} minutes.");
        RuleFor(c => c.DefaultMaxBudgetUsd)
            .GreaterThan(0)
            .When(c => c.DefaultMaxBudgetUsd.HasValue)
            .WithMessage("The default run budget must be more than $0 — clear it to run without a default cap.");
        RuleFor(c => c)
            .Must(c => !(c.ClearDefaultMaxBudget && c.DefaultMaxBudgetUsd.HasValue))
            .WithMessage("Either set the default run budget or clear it, not both.");
    }
}

public sealed class UpdateSettingsHandler(LooperDbContext db, IOptions<LooperOptions> options)
    : ICommandHandler<UpdateSettingsCommand, SettingsDto>
{
    public async Task<SettingsDto> Handle(UpdateSettingsCommand command, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var rows = await db.Settings
            .Where(s => s.Key == AppSettingKeys.ClaudeAuthMode || s.Key == AppSettingKeys.ClaudeApiKey
                        || s.Key == AppSettingKeys.RunTimeoutMinutes || s.Key == AppSettingKeys.DefaultMaxBudgetUsd)
            .ToListAsync(cancellationToken);
        var keyRow = rows.FirstOrDefault(r => r.Key == AppSettingKeys.ClaudeApiKey);

        // Work out the key that will be stored after this call, then check the mode against it —
        // "use an API key" with no key to use is refused up front, not discovered at the next run.
        var incoming = command.ApiKey is null || command.ApiKey == SecretMasker.Sentinel || command.ApiKey.Length == 0
            ? null
            : command.ApiKey.Trim();
        var resulting = command.ClearApiKey ? null : incoming ?? keyRow?.Value;
        if (command.ClaudeAuthMode == ClaudeAuthMode.ApiKey && string.IsNullOrWhiteSpace(resulting))
        {
            throw new ValidationException("Paste an Anthropic API key to use API-key billing, or keep using the Claude Code subscription.");
        }

        Upsert(rows, AppSettingKeys.ClaudeAuthMode, command.ClaudeAuthMode.ToString(), now);
        if (command.ClearApiKey)
        {
            if (keyRow is not null) db.Settings.Remove(keyRow);
        }
        else if (incoming is not null)
        {
            Upsert(rows, AppSettingKeys.ClaudeApiKey, incoming, now);
        }

        // Run limits: stored culture-invariant so the value the run reads is the value that was typed.
        if (command.RunTimeoutMinutes is { } minutes)
        {
            Upsert(rows, AppSettingKeys.RunTimeoutMinutes, minutes.ToString(CultureInfo.InvariantCulture), now);
        }
        var budgetRow = rows.FirstOrDefault(r => r.Key == AppSettingKeys.DefaultMaxBudgetUsd);
        if (command.ClearDefaultMaxBudget)
        {
            if (budgetRow is not null) db.Settings.Remove(budgetRow);
        }
        else if (command.DefaultMaxBudgetUsd is { } budget)
        {
            Upsert(rows, AppSettingKeys.DefaultMaxBudgetUsd, budget.ToString(CultureInfo.InvariantCulture), now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await SettingsMapper.ReadAsync(db, options.Value, cancellationToken);
    }

    private void Upsert(List<AppSetting> rows, string key, string value, DateTime now)
    {
        var row = rows.FirstOrDefault(r => r.Key == key);
        if (row is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value, UpdatedAtUtc = now });
        }
        else if (row.Value != value)
        {
            row.Value = value;
            row.UpdatedAtUtc = now;
        }
    }
}

public sealed class SettingsEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Query(new GetSettingsQuery(), ct));

        app.MapPut("/api/settings", (UpdateSettingsCommand command, IDispatcher dispatcher, CancellationToken ct) =>
            dispatcher.Send(command, ct));
    }
}
