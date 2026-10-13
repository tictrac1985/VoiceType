using System.Net;
using System.Text;
using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;
using VoiceType.Providers.Resilience;
using VoiceType.Settings;
using VoiceType.Tests.Support;
using Xunit;

namespace VoiceType.Tests.Providers;

public class MultiKeyTests
{
    private static readonly ProviderCatalog Catalog = ProviderCatalog.CreateDefault();
    private static readonly AudioPayload Audio = TestAudio.Wav();

    /// <summary>محرك وهمي: ينجح أو يرمي خطأ محددًا، ويعد مرات الاستدعاء.</summary>
    private sealed class FakeEngine(string id, Func<int, TranscriptionResult> behavior) : ISpeechEngine
    {
        public int Calls { get; private set; }

        public string Id => id;

        public string DisplayName => id;

        public SpeechCapabilities Capabilities { get; } = new();

        public Task<TranscriptionResult> TranscribeAsync(AudioPayload audio, TranscriptionOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(behavior(Calls));
        }
    }

    private static Func<int, TranscriptionResult> Fails(SpeechErrorKind kind) =>
        _ => throw new SpeechException(kind, "gemini:m", "test");

    private static Func<int, TranscriptionResult> Ok(string text) =>
        _ => new TranscriptionResult { Text = text, EngineId = "gemini:m" };

    private static KeyRotatingEngine Rotating(KeyHealth health, params FakeEngine[] engines) =>
        new(engines.Select((e, i) => new KeyedEngine($"k{i + 1}-{Guid.NewGuid():N}", $"المفتاح {i + 1}", e)).ToList(),
            health, (_, _) => Task.CompletedTask);

    [Theory]
    [InlineData(SpeechErrorKind.RateLimited)]
    [InlineData(SpeechErrorKind.PaymentRequired)]
    [InlineData(SpeechErrorKind.Unauthorized)]
    [InlineData(SpeechErrorKind.Forbidden)]
    public async Task Key_problem_moves_to_next_key_immediately(SpeechErrorKind kind)
    {
        var first = new FakeEngine("gemini:m", Fails(kind));
        var second = new FakeEngine("gemini:m", Ok("أهلا"));
        var engine = Rotating(new KeyHealth(), first, second);

        var result = await engine.TranscribeAsync(Audio, new TranscriptionOptions());

        Assert.Equal("أهلا", result.Text);
        Assert.Equal(1, first.Calls);
        Assert.Single(result.PreviousFailures);
        Assert.Equal(kind, result.PreviousFailures[0].Kind);
    }

    [Fact]
    public async Task Paused_key_is_skipped_next_time()
    {
        var health = new KeyHealth();
        var first = new FakeEngine("gemini:m", Fails(SpeechErrorKind.RateLimited));
        var second = new FakeEngine("gemini:m", Ok("تمام"));
        var engine = Rotating(health, first, second);

        await engine.TranscribeAsync(Audio, new TranscriptionOptions());
        var again = await engine.TranscribeAsync(Audio, new TranscriptionOptions());

        Assert.Equal("تمام", again.Text);
        Assert.Equal(1, first.Calls); // لم يُجرب مرة ثانية لأنه متوقف مؤقتًا
        Assert.Equal(2, second.Calls);
        Assert.Empty(again.PreviousFailures);
    }

    [Fact]
    public async Task All_keys_failing_throws_so_local_fallback_runs()
    {
        var engine = Rotating(new KeyHealth(),
            new FakeEngine("gemini:m", Fails(SpeechErrorKind.RateLimited)),
            new FakeEngine("gemini:m", Fails(SpeechErrorKind.PaymentRequired)));
        var local = new FakeEngine("local:model.bin", _ => new TranscriptionResult { Text = "محلي", EngineId = "local:model.bin" });

        var result = await new FallbackSpeechEngine([engine, local]).TranscribeAsync(Audio, new TranscriptionOptions());

        Assert.Equal("محلي", result.Text);
        Assert.Single(result.PreviousFailures);
    }

    [Fact]
    public async Task All_keys_paused_probes_only_the_soonest_rate_limited_key()
    {
        var health = new KeyHealth();
        var limited = new FakeEngine("gemini:m", Fails(SpeechErrorKind.RateLimited));
        var rejected = new FakeEngine("gemini:m", Fails(SpeechErrorKind.Unauthorized));
        var engine = Rotating(health, limited, rejected);

        await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(Audio, new TranscriptionOptions()));
        var ex = await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(Audio, new TranscriptionOptions()));

        Assert.Equal(SpeechErrorKind.RateLimited, ex.Kind);
        Assert.Equal(2, limited.Calls);   // جُرّب مرة أخرى لأن حده مؤقت
        Assert.Equal(1, rejected.Calls);  // المرفوض لا يُجرب حتى تنتهي مدة إيقافه
    }

    [Fact]
    public async Task Timeout_goes_to_fallback_without_trying_other_keys()
    {
        var first = new FakeEngine("gemini:m", Fails(SpeechErrorKind.Timeout));
        var second = new FakeEngine("gemini:m", Ok("x"));

        await Assert.ThrowsAsync<SpeechException>(() => Rotating(new KeyHealth(), first, second).TranscribeAsync(Audio, new TranscriptionOptions()));
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public void Gemini_retry_delay_is_read_from_body()
    {
        var body = """{"error":{"code":429,"details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"41s"}]}}""";
        Assert.Equal(TimeSpan.FromSeconds(41), ErrorMapper.RetryDelayFromBody(body));
    }

    [Fact]
    public async Task Network_error_does_not_try_other_keys()
    {
        var first = new FakeEngine("gemini:m", Fails(SpeechErrorKind.Network));
        var second = new FakeEngine("gemini:m", Ok("x"));

        await Assert.ThrowsAsync<SpeechException>(() => Rotating(new KeyHealth(), first, second).TranscribeAsync(Audio, new TranscriptionOptions()));
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task Server_error_retries_once_then_next_key()
    {
        var first = new FakeEngine("gemini:m", Fails(SpeechErrorKind.ServerError));
        var second = new FakeEngine("gemini:m", Ok("ok"));

        var result = await Rotating(new KeyHealth(), first, second).TranscribeAsync(Audio, new TranscriptionOptions());

        Assert.Equal("ok", result.Text);
        Assert.Equal(2, first.Calls);
    }

    [Fact]
    public async Task Unexpected_exception_still_falls_back()
    {
        var broken = new FakeEngine("gemini:m", _ => throw new InvalidOperationException("boom"));
        var local = new FakeEngine("local:x", _ => new TranscriptionResult { Text = "محلي", EngineId = "local:x" });

        var result = await new FallbackSpeechEngine([broken, local]).TranscribeAsync(Audio, new TranscriptionOptions());

        Assert.Equal("محلي", result.Text);
    }

    [Theory]
    [InlineData(400, "API key not valid. Please pass a valid API key.", SpeechErrorKind.Unauthorized)]
    [InlineData(429, "You exceeded your current quota, please check your plan and billing details.", SpeechErrorKind.RateLimited)]
    [InlineData(429, "You exceeded your current quota (insufficient_quota).", SpeechErrorKind.PaymentRequired)]
    [InlineData(403, "Quota exceeded for quota metric", SpeechErrorKind.RateLimited)]
    [InlineData(429, "Resource has been exhausted (e.g. check quota).", SpeechErrorKind.RateLimited)]
    [InlineData(400, "Invalid audio format", SpeechErrorKind.UnsupportedFormat)]
    public async Task Provider_messages_refine_error_kind(int status, string message, SpeechErrorKind expected)
    {
        var body = "{\"error\":{\"code\":" + status + ",\"message\":\"" + message + "\"}}";
        var handler = FakeHttpHandler.Json(body, (HttpStatusCode)status);
        var engine = new DeclarativeSpeechEngine(Catalog.Get("gemini"), new ProviderInstance { Definition = "gemini", ApiKey = "k" }, handler.CreateClient());

        var ex = await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(Audio, new TranscriptionOptions()));

        Assert.Equal(expected, ex.Kind);
    }

    [Fact]
    public async Task Gemini_pending_result_is_polled_until_completed()
    {
        var handler = FakeHttpHandler.Sequence(
            () => Json("""{"id":"interactions/abc","status":"in_progress"}"""),
            () => Json("""{"id":"interactions/abc","status":"in_progress"}"""),
            () => Json("""{"id":"interactions/abc","status":"completed","output_text":"أهلا بيك"}"""));
        var engine = new DeclarativeSpeechEngine(Catalog.Get("gemini"), new ProviderInstance { Definition = "gemini", ApiKey = "secret" }, handler.CreateClient());

        var result = await engine.TranscribeAsync(Audio, new TranscriptionOptions());

        Assert.Equal("أهلا بيك", result.Text);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("GET", handler.Requests[1].Method);
        Assert.EndsWith("/v1beta/interactions/abc", handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal("secret", handler.Requests[1].Headers["x-goog-api-key"]);
    }

    [Fact]
    public async Task Gemini_request_includes_custom_vocabulary()
    {
        var handler = FakeHttpHandler.Json("""{"status":"completed","output_text":"ok"}""");
        var engine = new DeclarativeSpeechEngine(Catalog.Get("gemini"), new ProviderInstance { Definition = "gemini", ApiKey = "k" }, handler.CreateClient());

        await engine.TranscribeAsync(Audio, new TranscriptionOptions());

        Assert.Contains("\"custom_vocabulary\"", handler.Requests[0].BodyText);
        Assert.Contains("Python", handler.Requests[0].BodyText);
    }

    [Fact]
    public async Task Gemini_flash_sends_inline_audio_and_reads_last_text_part()
    {
        var handler = FakeHttpHandler.Json("""{"candidates":[{"content":{"parts":[{"text":"Transcription: أهلا بيك في الـ Workspace"}]}}]}""");
        var definition = Catalog.Get("gemini-flash");
        var engine = new DeclarativeSpeechEngine(definition, new ProviderInstance { Definition = definition.Id, ApiKey = "k", Model = "gemini-flash-latest" }, handler.CreateClient());

        var result = await engine.TranscribeAsync(Audio, new TranscriptionOptions());

        Assert.Equal("أهلا بيك في الـ Workspace", result.Text);
        Assert.EndsWith("/v1beta/models/gemini-flash-latest:generateContent", handler.Requests[0].Uri.AbsolutePath);
        Assert.Contains("\"inline_data\"", handler.Requests[0].BodyText);
        Assert.Equal("gemini", definition.KeyFrom);
    }

    [Fact]
    public void Key_store_keeps_several_keys_per_provider()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vt-keys-{Guid.NewGuid():N}.dat");
        try
        {
            var store = new SecureKeyStore(path, s => "x" + s, s => s.StartsWith('x') ? s[1..] : null);
            store.ShareKey("openrouter-free", "openrouter");

            var first = store.Add("gemini", "key-one");
            var second = store.Add("gemini", " key-two ");
            var duplicate = store.Add("gemini", "key-one");
            store.Add("openrouter-free", "or-key");

            Assert.Equal("gemini", first);
            Assert.Equal("gemini#2", second);
            Assert.Equal(first, duplicate);
            Assert.Equal(["key-one", "key-two"], store.GetAll("gemini"));
            Assert.Equal("or-key", store.Get("openrouter"));

            var reloaded = new SecureKeyStore(path, s => "x" + s, s => s.StartsWith('x') ? s[1..] : null);
            reloaded.RemoveSlot("gemini");
            Assert.Equal(["key-two"], reloaded.GetAll("gemini"));
            Assert.True(reloaded.Has("gemini"));
            Assert.Equal("key-two", reloaded.Get("gemini"));
            Assert.Equal("gemini", reloaded.Add("gemini", "key-three")); // الخانة الأولى صارت فارغة

            reloaded.Remove("gemini");
            Assert.False(reloaded.Has("gemini"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
