using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;
using VoiceType.Providers.Resilience;
using VoiceType.Tests.Support;

namespace VoiceType.Tests.Providers;

public class ErrorsAndResilienceTests
{
    private static readonly ProviderCatalog Catalog = ProviderCatalog.CreateDefault();

    private static DeclarativeSpeechEngine OpenAI(FakeHttpHandler handler, int? timeoutSeconds = null) =>
        new(Catalog.Get("openai"),
            new ProviderInstance { Definition = "openai", ApiKey = "k", TimeoutSeconds = timeoutSeconds },
            handler.CreateClient());

    private static HttpResponseMessage Status(HttpStatusCode status, string body = "{}", TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SpeechErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.PaymentRequired, SpeechErrorKind.PaymentRequired)]
    [InlineData(HttpStatusCode.Forbidden, SpeechErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, SpeechErrorKind.NotFound)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, SpeechErrorKind.PayloadTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, SpeechErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, SpeechErrorKind.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, SpeechErrorKind.ServerError)]
    public async Task Http_errors_map_to_clear_kinds(HttpStatusCode status, SpeechErrorKind expected)
    {
        var handler = FakeHttpHandler.Sequence(() => Status(status, """{"error":{"message":"provider said no"}}"""));
        var ex = await Assert.ThrowsAsync<SpeechException>(() => OpenAI(handler).TranscribeAsync(TestAudio.Wav(), new()));

        Assert.Equal(expected, ex.Kind);
        Assert.Contains("provider said no", ex.TechnicalDetails);
        Assert.False(string.IsNullOrWhiteSpace(ex.UserMessage));
        Assert.DoesNotContain("Bearer", ex.ToString());
    }

    [Fact]
    public async Task Bad_request_about_format_becomes_unsupported_format()
    {
        var handler = FakeHttpHandler.Sequence(() => Status(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid file format."}}"""));
        var ex = await Assert.ThrowsAsync<SpeechException>(() => OpenAI(handler).TranscribeAsync(TestAudio.Wav(), new()));
        Assert.Equal(SpeechErrorKind.UnsupportedFormat, ex.Kind);
    }

    [Fact]
    public async Task Retry_after_header_is_captured()
    {
        var handler = FakeHttpHandler.Sequence(() => Status(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(2)));
        var ex = await Assert.ThrowsAsync<SpeechException>(() => OpenAI(handler).TranscribeAsync(TestAudio.Wav(), new()));
        Assert.Equal(TimeSpan.FromSeconds(2), ex.RetryAfter);
        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Slow_provider_times_out()
    {
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Status(HttpStatusCode.OK);
        });

        var ex = await Assert.ThrowsAsync<SpeechException>(() => OpenAI(handler, timeoutSeconds: 1).TranscribeAsync(TestAudio.Wav(), new()));
        Assert.Equal(SpeechErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task User_cancellation_is_not_reported_as_timeout()
    {
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return Status(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OpenAI(handler).TranscribeAsync(TestAudio.Wav(), new(), cts.Token));
    }

    [Fact]
    public async Task Network_failure_is_reported_as_network()
    {
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException("DNS failure"));
        var ex = await Assert.ThrowsAsync<SpeechException>(() => OpenAI(handler).TranscribeAsync(TestAudio.Wav(), new()));
        Assert.Equal(SpeechErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task Missing_text_in_response_is_invalid_response()
    {
        var handler = FakeHttpHandler.Json("""{"something":"else"}""");
        var ex = await Assert.ThrowsAsync<SpeechException>(() => OpenAI(handler).TranscribeAsync(TestAudio.Wav(), new()));
        Assert.Equal(SpeechErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public async Task Transient_errors_are_retried_then_succeed()
    {
        var handler = FakeHttpHandler.Sequence(
            () => Status(HttpStatusCode.ServiceUnavailable),
            () => Status(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(1)),
            () => Status(HttpStatusCode.OK, """{"text":"نجح"}"""));
        var delays = new List<TimeSpan>();
        var engine = new ResilientSpeechEngine(OpenAI(handler), delay: (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var result = await engine.TranscribeAsync(TestAudio.Wav(), new());

        Assert.Equal("نجح", result.Text);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), delays[1]);
    }

    [Fact]
    public async Task Non_transient_errors_are_not_retried()
    {
        var handler = FakeHttpHandler.Sequence(() => Status(HttpStatusCode.Unauthorized));
        var engine = new ResilientSpeechEngine(OpenAI(handler), delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(TestAudio.Wav(), new()));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Long_retry_after_is_not_awaited()
    {
        var handler = FakeHttpHandler.Sequence(() => Status(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromMinutes(5)));
        var engine = new ResilientSpeechEngine(OpenAI(handler), delay: (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(TestAudio.Wav(), new()));
        Assert.Equal(SpeechErrorKind.RateLimited, ex.Kind);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures_and_skips_calls()
    {
        var handler = FakeHttpHandler.Sequence(() => Status(HttpStatusCode.ServiceUnavailable));
        var engine = new ResilientSpeechEngine(
            OpenAI(handler),
            new ResilienceOptions { MaxAttempts = 1, CircuitBreakerThreshold = 2 },
            delay: (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(TestAudio.Wav(), new()));
        await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(TestAudio.Wav(), new()));
        var ex = await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(TestAudio.Wav(), new()));

        Assert.Equal(SpeechErrorKind.CircuitOpen, ex.Kind);
        Assert.Equal(2, handler.Requests.Count);
        Assert.True(engine.IsCircuitOpen);
    }

    [Fact]
    public async Task Fallback_uses_next_engine_and_reports_previous_failures()
    {
        var failing = OpenAI(FakeHttpHandler.Sequence(() => Status(HttpStatusCode.Unauthorized)));
        var working = new DeclarativeSpeechEngine(
            Catalog.Get("groq"),
            new ProviderInstance { Definition = "groq", ApiKey = "k" },
            FakeHttpHandler.Json("""{"text":"من البديل"}""").CreateClient());

        var result = await new FallbackSpeechEngine([failing, working]).TranscribeAsync(TestAudio.Wav(), new());

        Assert.Equal("من البديل", result.Text);
        Assert.Equal("groq:whisper-large-v3-turbo", result.EngineId);
        var failure = Assert.Single(result.PreviousFailures);
        Assert.Equal(SpeechErrorKind.Unauthorized, failure.Kind);
    }

    [Fact]
    public async Task Fallback_skips_engines_that_do_not_accept_the_format()
    {
        var cohereHandler = FakeHttpHandler.Json("""{"text":"x"}""");
        var cohere = new DeclarativeSpeechEngine(Catalog.Get("cohere"), new ProviderInstance { Definition = "cohere", ApiKey = "k" }, cohereHandler.CreateClient());
        var openai = OpenAI(FakeHttpHandler.Json("""{"text":"m4a ok"}"""));

        var result = await new FallbackSpeechEngine([cohere, openai]).TranscribeAsync(TestAudio.Of("m4a"), new());

        Assert.Equal("m4a ok", result.Text);
        Assert.Empty(cohereHandler.Requests);
        Assert.Equal(SpeechErrorKind.UnsupportedFormat, Assert.Single(result.PreviousFailures).Kind);
    }

    [Fact]
    public async Task Fallback_throws_when_every_engine_fails()
    {
        var a = OpenAI(FakeHttpHandler.Sequence(() => Status(HttpStatusCode.InternalServerError)));
        var b = OpenAI(FakeHttpHandler.Sequence(() => Status(HttpStatusCode.Unauthorized)));

        var ex = await Assert.ThrowsAsync<AllEnginesFailedException>(() => new FallbackSpeechEngine([a, b]).TranscribeAsync(TestAudio.Wav(), new()));

        Assert.Equal(2, ex.Failures.Count);
        Assert.Equal(SpeechErrorKind.AllEnginesFailed, ex.Kind);
    }
}
