using System.Net;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Looper.Api.Domain;
using Looper.Api.Features.Audio;
using Looper.Api.Infrastructure;
using Looper.Api.Infrastructure.Audio;
using Looper.Api.Infrastructure.Mcp;
using Looper.Api.Modules;
using Looper.Api.Modules.BuiltIn;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Looper.Api.Tests;

// ============================================================================
// ElevenLabs as a tool: the resource holds the key, the run gets generate_speech and
// list_voices, Looper makes the call and writes the file. The key reaches exactly one
// place — the request header — and never the prompt, the environment, the log or the model.
// ============================================================================

/// <summary>A scripted ElevenLabs: records what was sent, answers what the test says.</summary>
internal sealed class FakeElevenLabs : HttpMessageHandler
{
    public List<(HttpMethod Method, Uri Url, string? ApiKey, string? Accept, string Body)> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => Audio();

    public static HttpResponseMessage Audio(int bytes = 64) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(Enumerable.Repeat((byte)0xAB, bytes).ToArray())
        {
            Headers = { ContentType = new("audio/mpeg") }
        }
    };

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var key = request.Headers.TryGetValues("xi-api-key", out var values) ? values.FirstOrDefault() : null;
        Requests.Add((request.Method, request.RequestUri!, key, request.Headers.Accept.ToString(), body));
        return Respond(request);
    }

    public ElevenLabsClient Client() => new(new HttpClient(this) { BaseAddress = new Uri(ElevenLabsClient.DefaultBaseUrl) });
}

public sealed class ElevenLabsClientTests
{
    [Fact]
    public async Task The_key_goes_in_a_header_and_the_text_and_model_in_the_body()
    {
        var fake = new FakeElevenLabs();

        var audio = await fake.Client().SynthesizeAsync("sk-test-key", "voice-1", "eleven_turbo_v2_5", "Hello there.", default);

        Assert.Equal(64, audio.Length);
        var sent = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/voice-1?output_format=mp3_44100_128", sent.Url.ToString());
        Assert.Equal("sk-test-key", sent.ApiKey);
        Assert.Contains("audio/mpeg", sent.Accept);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("Hello there.", body.RootElement.GetProperty("text").GetString());
        Assert.Equal("eleven_turbo_v2_5", body.RootElement.GetProperty("model_id").GetString());
    }

    [Fact]
    public async Task A_refusal_carries_elevenlabs_own_message_and_never_the_key()
    {
        var fake = new FakeElevenLabs
        {
            Respond = _ => FakeElevenLabs.Json(HttpStatusCode.Unauthorized,
                """{"detail":{"status":"invalid_api_key","message":"Invalid API key"}}""")
        };

        var ex = await Assert.ThrowsAsync<ElevenLabsException>(() =>
            fake.Client().SynthesizeAsync("sk-test-key", "v", "eleven_multilingual_v2", "x", default));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Invalid API key (invalid_api_key)", ex.Message);
        Assert.DoesNotContain("sk-test-key", ex.Message);

        Assert.Equal("Quota exceeded", ElevenLabsClient.Describe("""{"detail":"Quota exceeded"}"""));
        Assert.Equal("<html>gateway</html>", ElevenLabsClient.Describe("<html>gateway</html>"));
        Assert.Equal("no details.", ElevenLabsClient.Describe(""));
    }

    [Fact]
    public async Task Voices_are_parsed_from_the_account_and_broken_entries_are_skipped()
    {
        var fake = new FakeElevenLabs
        {
            Respond = _ => FakeElevenLabs.Json(HttpStatusCode.OK,
                """{"voices":[{"voice_id":"v1","name":"Daniel","category":"premade","labels":{"accent":"british","gender":"male"}},{"voice_id":"","name":"broken"}]}""")
        };

        var voices = await fake.Client().ListVoicesAsync("sk-test-key", default);

        var daniel = Assert.Single(voices);
        Assert.Equal(("v1", "Daniel", "premade"), (daniel.VoiceId, daniel.Name, daniel.Category));
        Assert.Equal("british", daniel.Labels["accent"]);
        Assert.Equal("v1/voices", fake.Requests.Single().Url.AbsolutePath.TrimStart('/'));
        Assert.Equal("sk-test-key", fake.Requests.Single().ApiKey);
    }
}

public sealed class ElevenLabsModuleTests
{
    private static ResourceModuleContext Context(string json, string name = "Brand voice") =>
        new(json, Guid.NewGuid(), "Agent", Guid.NewGuid(), name, "");

    [Fact]
    public void The_resource_names_its_tools_and_keeps_the_key_out_of_the_prompt_and_the_environment()
    {
        var module = new ElevenLabsModule();
        var contribution = module.Contribute(Context(
            """{"apiKey":"sk-secret","voiceId":"v-daniel","modelId":"eleven_v3","outputFolder":"C:\\media\\vo","instructions":"One file per line."}"""));

        var section = Assert.Single(contribution.PromptSections);
        Assert.Contains("\"Brand voice\"", section);
        Assert.Contains("generate_speech", section);
        Assert.Contains("list_voices", section);
        Assert.Contains("Default voice v-daniel, model eleven_v3", section);
        Assert.Contains("One file per line.", section);
        Assert.DoesNotContain("sk-secret", section);
        Assert.Empty(contribution.EnvironmentVariables);          // the key is not handed to the run
        Assert.Contains(@"C:\media\vo", contribution.AdditionalDirectories);

        // Defaults fill in what the form left empty.
        var defaults = ElevenLabsResources.Parse(Context("""{"apiKey":"k"}"""));
        Assert.Equal(ElevenLabsClient.DefaultVoiceId, defaults.VoiceId);
        Assert.Equal(ElevenLabsClient.DefaultModelId, defaults.ModelId);
        Assert.Null(defaults.OutputFolder);
    }

    [Fact]
    public void Without_a_key_the_run_is_refused_before_it_starts_and_nothing_is_contributed()
    {
        var module = new ElevenLabsModule();
        var ex = Assert.Throws<InvalidOperationException>(() => module.PrepareRun(Context("""{"voiceId":"v"}""")));
        Assert.Contains("No ElevenLabs API key", ex.Message);
        Assert.Empty(module.Contribute(Context("{}")).PromptSections);
    }

    [Fact]
    public void Attaching_the_resource_gives_the_run_generate_speech_and_list_voices()
    {
        Resource Voice(string name) => new()
        {
            Name = name, Type = ResourceType.Custom, CustomTypeKey = "ElevenLabs",
            ConfigJson = """{"apiKey":"sk-secret","voiceId":"v-1","outputFolder":"/media/vo"}"""
        };

        var one = LooperTools.ForRun([Voice("Brand voice")]);
        Assert.Contains(one, t => t.Name == "generate_speech");
        Assert.Contains(one, t => t.Name == "list_voices");
        var speak = one.Single(t => t.Name == "generate_speech");
        Assert.Equal(["text"], speak.InputSchema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("\"Brand voice\": voice v-1", speak.Description);
        Assert.Contains("/media/vo", speak.Description);
        Assert.DoesNotContain("sk-secret", speak.Description);

        var two = LooperTools.ForRun([Voice("Narrator"), Voice("Brand voice")]).Single(t => t.Name == "generate_speech");
        Assert.Equal(["Brand voice", "Narrator"],
            two.InputSchema["properties"]!["resource"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
    }
}

public sealed class GenerateSpeechHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<LooperDbContext> _options;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"looper-voice-{Guid.NewGuid():N}");

    public GenerateSpeechHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<LooperDbContext>().UseSqlite(_connection).Options;
        using var db = new LooperDbContext(_options);
        db.Database.EnsureCreated();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<(Guid AgentId, Guid RunId)> Seed(params Resource[] resources)
    {
        await using var db = new LooperDbContext(_options);
        var agent = new LoopAgent { Name = "Voice agent", Prompt = "Speak.", Model = "claude-opus-5", WorkingDirectory = _dir };
        agent.Resources.AddRange(resources);
        db.Agents.Add(agent);
        var run = new AgentRun { AgentId = agent.Id, Trigger = RunTrigger.Manual, Model = agent.Model };
        db.Runs.Add(run);
        await db.SaveChangesAsync();
        return (agent.Id, run.Id);
    }

    private static Resource Voice(string name, string config) => new()
    {
        Name = name, Type = ResourceType.Custom, CustomTypeKey = "ElevenLabs", ConfigJson = config
    };

    [Fact]
    public async Task Writes_the_audio_next_to_the_work_returns_the_path_and_records_it_in_the_run_log()
    {
        var (agentId, runId) = await Seed(Voice("Brand voice", """{"apiKey":"sk-secret","voiceId":"v-default","modelId":"eleven_turbo_v2_5"}"""));
        var fake = new FakeElevenLabs();
        await using var db = new LooperDbContext(_options);
        var handler = new GenerateSpeechHandler(db, fake.Client());

        var first = await handler.Handle(new GenerateSpeechCommand(runId, agentId, "Every day, the world asks more.", "01_hook"), default);

        Assert.Equal(Path.Combine(_dir, "audio", "01_hook.mp3"), first.Path);
        Assert.Equal(64, new FileInfo(first.Path).Length);
        Assert.Equal(("01_hook.mp3", 64L, "v-default", "eleven_turbo_v2_5"), (first.FileName, first.Bytes, first.VoiceId, first.ModelId));
        Assert.Equal("sk-secret", fake.Requests.Single().ApiKey);
        Assert.Contains("/v1/text-to-speech/v-default", fake.Requests.Single().Url.ToString());

        var log = Assert.Single(await db.RunLogs.AsNoTracking().Where(l => l.RunId == runId).ToListAsync());
        Assert.Contains("generate_speech: wrote", log.Message);
        Assert.Contains("01_hook.mp3", log.Message);
        Assert.DoesNotContain("sk-secret", log.Message);

        // The same name again is a new take, not an overwrite; a voice override reaches the request.
        var second = await handler.Handle(new GenerateSpeechCommand(runId, agentId, "Take two.", "01_hook", VoiceId: "v-other"), default);
        Assert.Equal(Path.Combine(_dir, "audio", "01_hook-2.mp3"), second.Path);
        Assert.Contains("/v1/text-to-speech/v-other", fake.Requests.Last().Url.ToString());
    }

    [Fact]
    public async Task A_configured_output_folder_wins_and_a_nameless_call_is_named_from_its_text()
    {
        var folder = Path.Combine(_dir, "vo");
        var (agentId, runId) = await Seed(Voice("Brand voice", $$"""{"apiKey":"k","outputFolder":"{{folder.Replace("\\", "\\\\")}}"}"""));
        await using var db = new LooperDbContext(_options);

        var made = await new GenerateSpeechHandler(db, new FakeElevenLabs().Client())
            .Handle(new GenerateSpeechCommand(runId, agentId, "Transform the future of food!"), default);

        Assert.Equal(Path.Combine(folder, "transform-the-future-of-food.mp3"), made.Path);
        Assert.True(File.Exists(made.Path));
    }

    [Fact]
    public async Task Ambiguous_unknown_and_keyless_resources_are_refused_readably()
    {
        var (agentId, runId) = await Seed(
            Voice("Narrator", """{"apiKey":"k1"}"""),
            Voice("Brand voice", """{"voiceId":"v"}"""));
        await using var db = new LooperDbContext(_options);
        var handler = new GenerateSpeechHandler(db, new FakeElevenLabs().Client());

        var ambiguous = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new GenerateSpeechCommand(runId, agentId, "x"), default));
        Assert.Contains("Say which ElevenLabs resource", ambiguous.Message);
        Assert.Contains("\"Brand voice\"", ambiguous.Message);

        var unknown = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new GenerateSpeechCommand(runId, agentId, "x", Resource: "Podcast"), default));
        Assert.Contains("No ElevenLabs resource named \"Podcast\"", unknown.Message);

        var keyless = await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(new GenerateSpeechCommand(runId, agentId, "x", Resource: "Brand voice"), default));
        Assert.Contains("has no API key stored", keyless.Message);

        var upstream = new FakeElevenLabs { Respond = _ => FakeElevenLabs.Json(HttpStatusCode.PaymentRequired, """{"detail":{"status":"quota_exceeded","message":"You have 12 characters left"}}""") };
        var refused = await Assert.ThrowsAsync<ElevenLabsException>(() =>
            new GenerateSpeechHandler(db, upstream.Client()).Handle(new GenerateSpeechCommand(runId, agentId, "x", Resource: "Narrator"), default));
        Assert.Contains("12 characters left", refused.Message);
    }

    [Theory]
    [InlineData("01_hook", "ignored", "01_hook.mp3")]
    [InlineData("../evil/take.mp3", "ignored", "take.mp3")]
    [InlineData("  Scene 2: The Reveal  ", "ignored", "scene-2-the-reveal.mp3")]
    [InlineData(null, "Every day, the world asks more of the food industry.", "every-day-the-world-asks-more-of-the-food-industry.mp3")]
    [InlineData("###", "###", "speech.mp3")]
    public void File_names_are_safe_and_never_empty(string? requested, string text, string expected)
    {
        Assert.Equal(expected, GenerateSpeechHandler.AudioFileName(requested, text));
    }

    [Fact]
    public void The_validator_bounds_the_text()
    {
        var validator = new GenerateSpeechValidator();
        Assert.False(validator.Validate(new GenerateSpeechCommand(Guid.NewGuid(), Guid.NewGuid(), "")).IsValid);
        Assert.False(validator.Validate(new GenerateSpeechCommand(Guid.NewGuid(), Guid.NewGuid(), new string('x', ElevenLabsClient.MaxCharacters + 1))).IsValid);
        Assert.True(validator.Validate(new GenerateSpeechCommand(Guid.NewGuid(), Guid.NewGuid(), new string('x', ElevenLabsClient.MaxCharacters))).IsValid);
    }
}
