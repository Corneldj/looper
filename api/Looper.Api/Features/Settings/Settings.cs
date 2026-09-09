using FluentValidation;
using Looper.Api.Common.Cqrs;
using Looper.Api.Common.Endpoints;
using Looper.Api.Domain;
using Looper.Api.Features.Resources;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Execution;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Settings;

/// <summary>What the UI sees. The API key itself never leaves the server — only whether one is stored and its tail.</summary>
public sealed record SettingsDto(ClaudeAuthMode ClaudeAuthMode, bool HasApiKey, string? ApiKeyHint, DateTime? UpdatedAtUtc);

public static class SettingsMapper
{
    public static SettingsDto ToDto(this ClaudeAuthSettings auth) =>
        new(auth.Mode, auth.HasApiKey, auth.ApiKeyHint, auth.UpdatedAtUtc);
}

// ---------- read ----------

public sealed record GetSettingsQuery : IQuery<SettingsDto>;

public sealed class GetSettingsHandler(LooperDbContext db) : IQueryHandler<GetSettingsQuery, SettingsDto>
{
    public async Task<SettingsDto> Handle(GetSettingsQuery query, CancellationToken cancellationToken) =>
        (await ClaudeAuthProvider.ReadAsync(db, cancellationToken)).ToDto();
}

// ---------- update ----------

/// <summary>
/// <paramref name="ApiKey"/>: a new key replaces the stored one; null or the secret sentinel keeps
/// it. <paramref name="ClearApiKey"/> removes the stored key (and is refused while the mode needs one).
/// </summary>
public sealed record UpdateSettingsCommand(ClaudeAuthMode ClaudeAuthMode, string? ApiKey, bool ClearApiKey = false)
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
    }
}

public sealed class UpdateSettingsHandler(LooperDbContext db) : ICommandHandler<UpdateSettingsCommand, SettingsDto>
{
    public async Task<SettingsDto> Handle(UpdateSettingsCommand command, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var rows = await db.Settings
            .Where(s => s.Key == AppSettingKeys.ClaudeAuthMode || s.Key == AppSettingKeys.ClaudeApiKey)
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

        await db.SaveChangesAsync(cancellationToken);
        return (await ClaudeAuthProvider.ReadAsync(db, cancellationToken)).ToDto();
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
