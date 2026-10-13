using System.Net;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;
using VoiceType.Tests.Support;

namespace VoiceType.Tests.Providers;

public class BuiltInProvidersTests
{
    private static readonly ProviderCatalog Catalog = ProviderCatalog.CreateDefault();

    private static DeclarativeSpeechEngine Engine(string definition, FakeHttpHandler handler, string? model = null, string? key = "test-key") =>
        new(Catalog.Get(definition), new ProviderInstance { Definition = definition, Model = model, ApiKey = key }, handler.CreateClient());

    [Fact]
    public void Catalog_contains_all_built_in_providers()
    {
        var ids = Catalog.All.Select(d => d.Id).ToList();
        Assert.Contains("openai", ids);
        Assert.Contains("groq", ids);
        Assert.Contains("openai-compatible", ids);
        Assert.Contains("cohere", ids);
        Assert.Contains("openrouter", ids);
        Assert.Contains("huggingface", ids);
        Assert.Contains("elevenlabs", ids);
        Assert.Contains("deepgram", ids);
        Assert.All(Catalog.All, d => Assert.Empty(ProviderDefinitionValidator.Validate(d)));
    }

    [Fact]
    public async Task OpenAI_sends_multipart_with_model_language_and_file()
    {
        var handler = FakeHttpHandler.Json("""{"text":"مرحبا"}""");
        var result = await Engine("openai", handler).TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions { Language = "ar" });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", request.Uri.ToString());
        Assert.Equal("Bearer test-key", request.Headers["Authorization"]);
        Assert.StartsWith("multipart/form-data", request.ContentType);
        Assert.Contains("name=model", request.BodyText);
        Assert.Contains("gpt-4o-transcribe", request.BodyText);
        Assert.Contains("name=language", request.BodyText);
        Assert.Contains("filename=sample.wav", request.BodyText);
        Assert.DoesNotContain("name=prompt", request.BodyText);
        Assert.DoesNotContain("name=temperature", request.BodyText);
        Assert.Equal("مرحبا", result.Text);
        Assert.Equal("openai:gpt-4o-transcribe", result.EngineId);
    }

    [Fact]
    public async Task Groq_uses_openai_compatible_endpoint_and_selected_model()
    {
        var handler = FakeHttpHandler.Json("""{"text":"اهلا"}""");
        await Engine("groq", handler, model: "whisper-large-v3").TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.groq.com/openai/v1/audio/transcriptions", request.Uri.ToString());
        Assert.Contains("whisper-large-v3", request.BodyText);
    }

    [Fact]
    public async Task Cohere_requires_language()
    {
        var handler = FakeHttpHandler.Json("""{"text":"x"}""");
        var ex = await Assert.ThrowsAsync<SpeechException>(() =>
            Engine("cohere", handler).TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions { Language = null }));

        Assert.Equal(SpeechErrorKind.Configuration, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cohere_rejects_unsupported_format_before_sending()
    {
        var handler = FakeHttpHandler.Json("""{"text":"x"}""");
        var ex = await Assert.ThrowsAsync<SpeechException>(() =>
            Engine("cohere", handler).TranscribeAsync(TestAudio.Of("m4a"), new TranscriptionOptions()));

        Assert.Equal(SpeechErrorKind.UnsupportedFormat, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cohere_sends_arabic_model_by_default()
    {
        var handler = FakeHttpHandler.Json("""{"text":"النص"}""");
        await Engine("cohere", handler).TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions { Language = "ar" });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.cohere.com/v2/audio/transcriptions", request.Uri.ToString());
        Assert.Contains("cohere-transcribe-arabic-07-2026", request.BodyText);
    }

    [Fact]
    public async Task OpenRouter_sends_json_with_base64_audio_and_reads_cost()
    {
        var handler = FakeHttpHandler.Json("""{"text":"تمام","usage":{"cost":0.00042,"seconds":3}}""");
        var audio = TestAudio.Wav();
        var result = await Engine("openrouter", handler, model: "openai/whisper-large-v3")
            .TranscribeAsync(audio, new TranscriptionOptions { Language = "ar", Temperature = 0.2 });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://openrouter.ai/api/v1/audio/transcriptions", request.Uri.ToString());
        Assert.StartsWith("application/json", request.ContentType);
        Assert.Equal("VoiceType", request.Headers["X-Title"]);

        var json = JsonNode.Parse(request.BodyText)!;
        Assert.Equal("openai/whisper-large-v3", (string?)json["model"]);
        Assert.Equal("ar", (string?)json["language"]);
        Assert.Equal(0.2, (double)json["temperature"]!, 3);
        Assert.Equal(Convert.ToBase64String(audio.Data), (string?)json["input_audio"]!["data"]);
        Assert.Equal("wav", (string?)json["input_audio"]!["format"]);
        Assert.Equal("تمام", result.Text);
        Assert.Equal(0.00042, result.Cost!.Value, 6);
    }

    [Fact]
    public async Task OpenRouter_omits_empty_language_and_temperature()
    {
        var handler = FakeHttpHandler.Json("""{"text":"x"}""");
        await Engine("openrouter", handler).TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions { Language = null });

        var json = JsonNode.Parse(Assert.Single(handler.Requests).BodyText)!.AsObject();
        Assert.False(json.ContainsKey("language"));
        Assert.False(json.ContainsKey("temperature"));
    }

    [Fact]
    public async Task HuggingFace_sends_raw_audio_to_model_url()
    {
        var handler = FakeHttpHandler.Json("""{"text":"hello"}""");
        var audio = TestAudio.Wav();
        await Engine("huggingface", handler).TranscribeAsync(audio, new TranscriptionOptions());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://router.huggingface.co/hf-inference/models/openai/whisper-large-v3", request.Uri.ToString());
        Assert.Equal("audio/wav", request.ContentType);
        Assert.Equal(audio.Data, request.Body);
        Assert.Equal("Bearer test-key", request.Headers["Authorization"]);
    }

    [Fact]
    public async Task ElevenLabs_uses_custom_header_auth()
    {
        var handler = FakeHttpHandler.Json("""{"text":"x"}""");
        await Engine("elevenlabs", handler).TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions { Language = "ar" });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("test-key", request.Headers["xi-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.Contains("name=model_id", request.BodyText);
        Assert.Contains("name=language_code", request.BodyText);
    }

    [Fact]
    public async Task Deepgram_uses_token_prefix_query_parameters_and_nested_text_path()
    {
        var handler = FakeHttpHandler.Json("""{"results":{"channels":[{"alternatives":[{"transcript":"نص عميق"}]}]}}""");
        var result = await Engine("deepgram", handler).TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions { Language = "ar" });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Token test-key", request.Headers["Authorization"]);
        Assert.Contains("model=nova-3", request.Uri.Query);
        Assert.Contains("language=ar", request.Uri.Query);
        Assert.Contains("punctuate=true", request.Uri.Query);
        Assert.Equal("نص عميق", result.Text);
    }

    [Fact]
    public async Task Missing_key_is_a_configuration_error()
    {
        var handler = FakeHttpHandler.Json("""{"text":"x"}""");
        var ex = await Assert.ThrowsAsync<SpeechException>(() =>
            Engine("openai", handler, key: null).TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions()));

        Assert.Equal(SpeechErrorKind.Configuration, ex.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OpenAI_compatible_local_server_works_without_key()
    {
        var handler = FakeHttpHandler.Json("""{"text":"محلي"}""");
        var engine = new DeclarativeSpeechEngine(
            Catalog.Get("openai-compatible"),
            new ProviderInstance
            {
                Definition = "openai-compatible",
                Model = "whisper-turbo",
                Variables = { ["base_url"] = "http://127.0.0.1:9000/v1" },
            },
            handler.CreateClient());

        var result = await engine.TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:9000/v1/audio/transcriptions", request.Uri.ToString());
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.Equal("محلي", result.Text);
    }

    [Fact]
    public void Preview_never_shows_the_real_key()
    {
        var engine = Engine("elevenlabs", FakeHttpHandler.Json("{}"), key: "super-secret");
        var preview = engine.Preview(TestAudio.Wav(), new TranscriptionOptions()).ToString();

        Assert.DoesNotContain("super-secret", preview);
        Assert.Contains("xi-api-key: ***", preview);
    }
}
