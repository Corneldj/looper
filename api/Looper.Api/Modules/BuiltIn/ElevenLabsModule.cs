using FluentValidation;
using Looper.Api.Domain;
using Looper.Api.Infrastructure.Audio;

namespace Looper.Api.Modules.BuiltIn;

/// <summary>
/// An ElevenLabs account as a tool: the resource holds the API key, the attached agent gets
/// generate_speech and list_voices. The key stays on the API machine — Looper makes the call,
/// writes the audio file and hands the model the path — so a run produces voice-over without
/// ever holding a credential, and every generation is a recorded harness action.
/// </summary>
public sealed class ElevenLabsModule : IResourceTypeModule
{
    public const string TypeKey_ = "ElevenLabs";

    public string TypeKey => TypeKey_;
    public string DisplayName => "ElevenLabs voice";
    public string Icon => "🎙️";
    public string Blurb => "Text-to-speech through your ElevenLabs account: the agent gets a generate_speech tool that writes MP3s; the API key never leaves Looper.";

    public IReadOnlyList<ResourceField> Fields { get; } =
    [
        new("apiKey", "ElevenLabs API key", ResourceFieldKind.Password, Required: true,
            Hint: "From elevenlabs.io → Profile → API keys. Used only by Looper's generate_speech tool; the agent never sees it."),
        new("voiceId", "Default voice id", ResourceFieldKind.Text,
            Hint: "Voice used when the agent does not pick one (it can call list_voices). Empty = Rachel, an ElevenLabs premade voice.",
            Placeholder: ElevenLabsClient.DefaultVoiceId),
        new("modelId", "Model", ResourceFieldKind.Select, Options: ElevenLabsClient.ModelIds,
            Hint: "eleven_multilingual_v2 for quality across languages; turbo/flash for speed and cost; eleven_v3 for the most expressive delivery. Empty = multilingual v2."),
        new("outputFolder", "Output folder", ResourceFieldKind.Path,
            Hint: "Where generated audio is written; the agent gets access to it. Empty = an 'audio' folder inside the run's working directory."),
        new("instructions", "Guidance", ResourceFieldKind.Multiline,
            Hint: "Optional: when to generate audio and what it is for, e.g. \"one MP3 per voice-over line, named 01_hook, 02_…\".")
    ];

    /// <summary>A run built on a voice it cannot use would look like a success while missing its premise: refuse to start.</summary>
    public void PrepareRun(ResourceModuleContext context)
    {
        if (!ElevenLabsResources.Parse(context).HasApiKey)
        {
            throw new InvalidOperationException("No ElevenLabs API key is stored on this resource. Paste one in the workbench.");
        }
    }

    public ResourceContribution Contribute(ResourceModuleContext context)
    {
        var contribution = new ResourceContribution();
        var config = ElevenLabsResources.Parse(context);
        if (!config.HasApiKey) return contribution;

        if (config.OutputFolder is { } folder) contribution.AdditionalDirectories.Add(folder);

        var name = context.ResourceName.Length > 0 ? context.ResourceName : DisplayName;
        contribution.PromptSections.Add(
            $"VOICE / AUDIO — \"{name}\": turn text into speech with the Looper tool generate_speech(text, fileName?, voiceId?). " +
            "Looper calls ElevenLabs with the stored key (you never need or see it), writes an MP3 and returns its path. " +
            $"Default voice {config.VoiceId}, model {config.ModelId}; call list_voices to choose another. " +
            $"Audio is written to {(config.OutputFolder ?? "an 'audio' folder in your working directory")}. " +
            $"Keep each call under {ElevenLabsClient.MaxCharacters:N0} characters — one line or paragraph per file is the reliable pattern." +
            (string.IsNullOrWhiteSpace(config.Instructions) ? "" : $"\nGuidance from the user: {config.Instructions.Trim()}"));

        return contribution;
    }
}

/// <summary>Parsed view of an ElevenLabs resource. The key is read here and handed to the client — nowhere else.</summary>
public sealed record ElevenLabsConfig(string? ApiKey, string VoiceId, string ModelId, string? OutputFolder, string? Instructions)
{
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
}

public static class ElevenLabsResources
{
    public static bool IsElevenLabs(Resource resource) =>
        resource.Type == ResourceType.Custom &&
        string.Equals(resource.CustomTypeKey, ElevenLabsModule.TypeKey_, StringComparison.OrdinalIgnoreCase);

    public static ElevenLabsConfig Parse(ResourceModuleContext context)
    {
        var voice = context.GetString("voiceId")?.Trim();
        var model = context.GetString("modelId")?.Trim();
        var folder = context.GetString("outputFolder")?.Trim();
        return new ElevenLabsConfig(
            context.GetString("apiKey")?.Trim(),
            string.IsNullOrWhiteSpace(voice) ? ElevenLabsClient.DefaultVoiceId : voice,
            string.IsNullOrWhiteSpace(model) ? ElevenLabsClient.DefaultModelId : model,
            string.IsNullOrWhiteSpace(folder) ? null : folder,
            context.GetString("instructions"));
    }

    /// <summary>The ElevenLabs resources on a run, by name — the tools' choice list.</summary>
    public static IReadOnlyList<Resource> Attached(IEnumerable<Resource> resources) =>
        resources.Where(IsElevenLabs).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// The resource a tool call means: the only one attached, or the one it names. Refusals are
    /// validation errors so the model reads them as tool errors and can correct the call.
    /// </summary>
    public static Resource Resolve(IEnumerable<Resource> resources, string? name)
    {
        var attached = Attached(resources);
        if (attached.Count == 0) throw new ValidationException("No ElevenLabs resource is attached to this agent.");

        var names = string.Join(", ", attached.Select(r => $"\"{r.Name}\""));
        if (string.IsNullOrWhiteSpace(name))
        {
            return attached.Count == 1
                ? attached[0]
                : throw new ValidationException($"Say which ElevenLabs resource: {names}.");
        }
        return attached.FirstOrDefault(r => string.Equals(r.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? throw new ValidationException($"No ElevenLabs resource named \"{name}\" on this agent. Attached: {names}.");
    }
}
