using FluentValidation;
using Looper.Api.Common;
using Looper.Api.Common.Cqrs;
using Looper.Api.Domain;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Audio;
using Looper.Api.Infrastructure.Execution;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Features.Audio;

/// <summary>An audio file Looper generated for a run: what the model gets back from generate_speech.</summary>
public sealed record GeneratedAudioDto(string Path, string FileName, long Bytes, int Characters, string VoiceId, string ModelId);

public sealed record VoiceDto(string VoiceId, string Name, string? Category, string? Description, IReadOnlyDictionary<string, string> Labels);

// ---------- generate ----------

/// <summary>
/// Turns text into an MP3 through the ElevenLabs resource attached to the run's agent. The
/// resource's key is read here and sent to ElevenLabs; the model only ever sees the file path.
/// <paramref name="Resource"/> names the resource when the agent has more than one.
/// </summary>
public sealed record GenerateSpeechCommand(
    Guid RunId,
    Guid AgentId,
    string Text,
    string? FileName = null,
    string? VoiceId = null,
    string? Resource = null) : ICommand<GeneratedAudioDto>;

public sealed class GenerateSpeechValidator : AbstractValidator<GenerateSpeechCommand>
{
    public GenerateSpeechValidator()
    {
        RuleFor(c => c.Text).NotEmpty().WithMessage("Give the text to speak.");
        RuleFor(c => c.Text).MaximumLength(ElevenLabsClient.MaxCharacters)
            .WithMessage($"ElevenLabs takes at most {ElevenLabsClient.MaxCharacters:N0} characters per request — split the script into several calls.");
    }
}

public sealed class GenerateSpeechHandler(LooperDbContext db, ElevenLabsClient client)
    : ICommandHandler<GenerateSpeechCommand, GeneratedAudioDto>
{
    public async Task<GeneratedAudioDto> Handle(GenerateSpeechCommand command, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == command.AgentId, cancellationToken)
            ?? throw new NotFoundException("Agent", command.AgentId);
        var resources = agent.Resources.ToList();

        var resource = ElevenLabsResources.Resolve(resources, command.Resource);
        var config = ElevenLabsResources.Parse(new ResourceModuleContext(resource.ConfigJson));
        if (!config.HasApiKey)
        {
            throw new ValidationException($"The ElevenLabs resource \"{resource.Name}\" has no API key stored. Add one in the workbench.");
        }

        var voiceId = string.IsNullOrWhiteSpace(command.VoiceId) ? config.VoiceId : command.VoiceId.Trim();
        var folder = config.OutputFolder ?? Path.Combine(AgentWorkspace.Resolve(agent, resources).WorkingDirectory, "audio");
        Directory.CreateDirectory(folder);

        var audio = await client.SynthesizeAsync(config.ApiKey!, voiceId, config.ModelId, command.Text, cancellationToken);
        var path = UniquePath(folder, AudioFileName(command.FileName, command.Text));
        await File.WriteAllBytesAsync(path, audio, cancellationToken);

        // A harness action, recorded like every other: the run log shows what was made and where.
        db.RunLogs.Add(new RunLogEntry
        {
            RunId = command.RunId,
            Level = "info",
            Message = $"generate_speech: wrote {path} ({audio.Length:N0} bytes, {command.Text.Length:N0} characters, voice {voiceId}, {config.ModelId})."
        });
        await db.SaveChangesAsync(cancellationToken);

        return new GeneratedAudioDto(path, Path.GetFileName(path), audio.Length, command.Text.Length, voiceId, config.ModelId);
    }

    /// <summary>
    /// A safe file name: a requested name loses any folder part and extension and is slugged (underscores kept, so "01_hook" stays "01_hook"); without
    /// one, the opening words of the text name the file. Never empty, never a path.
    /// </summary>
    internal static string AudioFileName(string? requested, string text)
    {
        var basis = string.IsNullOrWhiteSpace(requested)
            ? text
            : Path.GetFileNameWithoutExtension(requested.Trim());
        var slug = new string(basis.Select(c => char.IsLetterOrDigit(c) || c == '_' ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 60) slug = slug[..60].TrimEnd('-');
        return (slug.Length == 0 ? "speech" : slug) + ".mp3";
    }

    /// <summary>An existing take is never overwritten: the next one gets -2, -3, …</summary>
    internal static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; ; n++)
        {
            candidate = Path.Combine(folder, $"{stem}-{n}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}

// ---------- voices ----------

public sealed record ListVoicesQuery(Guid AgentId, string? Resource = null) : IQuery<IReadOnlyList<VoiceDto>>;

public sealed class ListVoicesHandler(LooperDbContext db, ElevenLabsClient client)
    : IQueryHandler<ListVoicesQuery, IReadOnlyList<VoiceDto>>
{
    public async Task<IReadOnlyList<VoiceDto>> Handle(ListVoicesQuery query, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.Resources)
            .FirstOrDefaultAsync(a => a.Id == query.AgentId, cancellationToken)
            ?? throw new NotFoundException("Agent", query.AgentId);

        var resource = ElevenLabsResources.Resolve(agent.Resources, query.Resource);
        var config = ElevenLabsResources.Parse(new ResourceModuleContext(resource.ConfigJson));
        if (!config.HasApiKey)
        {
            throw new ValidationException($"The ElevenLabs resource \"{resource.Name}\" has no API key stored. Add one in the workbench.");
        }

        var voices = await client.ListVoicesAsync(config.ApiKey!, cancellationToken);
        return voices.Select(v => new VoiceDto(v.VoiceId, v.Name, v.Category, v.Description, v.Labels)).ToList();
    }
}
