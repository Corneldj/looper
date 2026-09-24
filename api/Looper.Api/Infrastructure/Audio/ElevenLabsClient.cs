using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Looper.Api.Infrastructure.Audio;

/// <summary>ElevenLabs refused or failed a request. The message is safe to show the model: never the key.</summary>
public sealed class ElevenLabsException(string message) : Exception(message);

public sealed record ElevenLabsVoice(string VoiceId, string Name, string? Category, string? Description, IReadOnlyDictionary<string, string> Labels);

/// <summary>
/// The only code that talks to ElevenLabs. The API key arrives per call from the resource that
/// holds it and goes into one request header; it is never logged and never returned.
/// </summary>
public sealed class ElevenLabsClient(HttpClient http)
{
    public const string DefaultBaseUrl = "https://api.elevenlabs.io/";

    /// <summary>"Rachel", one of ElevenLabs' premade voices — available to every account.</summary>
    public const string DefaultVoiceId = "21m00Tcm4TlvDq8ikWAM";

    public const string DefaultModelId = "eleven_multilingual_v2";
    public const string OutputFormat = "mp3_44100_128";

    /// <summary>ElevenLabs' per-request limit for the multilingual model; longer scripts go in several calls.</summary>
    public const int MaxCharacters = 5_000;

    public static readonly string[] ModelIds = ["eleven_multilingual_v2", "eleven_turbo_v2_5", "eleven_flash_v2_5", "eleven_v3"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<byte[]> SynthesizeAsync(string apiKey, string voiceId, string modelId, string text, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format={OutputFormat}");
        request.Headers.Add("xi-api-key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        request.Content = JsonContent.Create(new { text, model_id = modelId }, options: Json);

        using var response = await SendAsync(request, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ElevenLabsVoice>> ListVoicesAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/voices");
        request.Headers.Add("xi-api-key", apiKey);

        using var response = await SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<VoicesPayload>(Json, cancellationToken);
        return (payload?.Voices ?? [])
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId))
            .Select(v => new ElevenLabsVoice(v.VoiceId!, v.Name ?? "", v.Category, v.Description,
                v.Labels ?? new Dictionary<string, string>()))
            .ToList();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ElevenLabsException($"ElevenLabs could not be reached: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ElevenLabsException("ElevenLabs did not answer within the time limit — try a shorter text.");
        }

        if (response.IsSuccessStatusCode) return response;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        response.Dispose();
        throw new ElevenLabsException($"ElevenLabs refused the request ({status}): {Describe(body)}");
    }

    /// <summary>ElevenLabs errors look like {"detail":{"status":"…","message":"…"}} or {"detail":"…"}; pull the message out.</summary>
    internal static string Describe(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no details.";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String) return detail.GetString()!;
                if (detail.ValueKind == JsonValueKind.Object)
                {
                    var message = detail.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    var status = detail.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    if (message is not null) return status is not null ? $"{message} ({status})" : message;
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON: the raw body is the best we have.
        }
        return body.Length > 300 ? body[..300] + "…" : body;
    }

    private sealed record VoicesPayload(List<VoicePayload>? Voices);

    private sealed record VoicePayload(
        [property: JsonPropertyName("voice_id")] string? VoiceId,
        string? Name,
        string? Category,
        string? Description,
        Dictionary<string, string>? Labels);
}
