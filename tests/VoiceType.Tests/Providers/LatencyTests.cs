using VoiceType.Core.Speech;
using VoiceType.Providers.Resilience;
using VoiceType.Tests.Support;
using Xunit;

namespace VoiceType.Tests.Providers;

/// <summary>سرعة الإملاء: لا ينتظر المستخدم مهلة الخدمات المعطلة.</summary>
public class LatencyTests
{
    private static readonly AudioPayload Audio = TestAudio.Wav();

    /// <summary>محرك وهمي يتأخر مدة محددة ثم ينجح أو يفشل، ويحترم الإلغاء.</summary>
    private sealed class SlowEngine(string id, TimeSpan delay, SpeechErrorKind? fail = null) : ISpeechEngine
    {
        public int Calls;
        public bool Cancelled;

        public string Id => id;

        public string DisplayName => id;

        public SpeechCapabilities Capabilities { get; } = new();

        public async Task<TranscriptionResult> TranscribeAsync(AudioPayload audio, TranscriptionOptions options, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            if (fail is { } kind)
            {
                throw new SpeechException(kind, id, "test failure");
            }

            return new TranscriptionResult { Text = "from " + id, EngineId = id };
        }
    }

    [Fact]
    public async Task Fast_online_answer_wins_and_local_never_starts()
    {
        var online = new SlowEngine("gemini:m", TimeSpan.FromMilliseconds(20));
        var local = new SlowEngine("local:x.bin", TimeSpan.FromMilliseconds(10));

        var result = await new HedgedSpeechEngine(online, local, TimeSpan.FromSeconds(2)).TranscribeAsync(Audio, new());

        Assert.Equal("gemini:m", result.EngineId);
        Assert.Equal(0, local.Calls);
        Assert.Empty(result.PreviousFailures);
    }

    [Fact]
    public async Task Slow_online_service_is_overtaken_by_local_without_waiting_for_its_timeout()
    {
        var hung = new SlowEngine("gemini-flash:lite", TimeSpan.FromSeconds(30));
        var chain = new FallbackSpeechEngine([hung], "online", new EngineHealth());
        var local = new SlowEngine("local:x.bin", TimeSpan.FromMilliseconds(50));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new HedgedSpeechEngine(chain, local, TimeSpan.FromMilliseconds(100)).TranscribeAsync(Audio, new());

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Equal("local:x.bin", result.EngineId);
        var note = Assert.Single(result.PreviousFailures);
        Assert.Equal("gemini-flash:lite", note.EngineId);
        await Task.Delay(100);
        Assert.True(hung.Cancelled);
    }

    [Fact]
    public async Task Online_failure_starts_local_immediately_and_keeps_the_reason()
    {
        var broken = new SlowEngine("gemini:transcribe", TimeSpan.Zero, SpeechErrorKind.BadRequest);
        var chain = new FallbackSpeechEngine([broken], "online", new EngineHealth());
        var local = new SlowEngine("local:x.bin", TimeSpan.FromMilliseconds(10));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new HedgedSpeechEngine(chain, local, TimeSpan.FromSeconds(10)).TranscribeAsync(Audio, new());

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
        Assert.Equal("local:x.bin", result.EngineId);
        Assert.Equal(SpeechErrorKind.BadRequest, Assert.Single(result.PreviousFailures).Kind);
    }

    [Fact]
    public async Task Broken_service_is_skipped_on_the_next_dictations()
    {
        var health = new EngineHealth();
        var broken = new SlowEngine("gemini:transcribe", TimeSpan.Zero, SpeechErrorKind.BadRequest);
        var companion = new SlowEngine("gemini-flash:lite", TimeSpan.FromMilliseconds(5));

        for (var i = 0; i < 3; i++)
        {
            var result = await new FallbackSpeechEngine([broken, companion], "online", health).TranscribeAsync(Audio, new());
            Assert.Equal("gemini-flash:lite", result.EngineId);
        }

        Assert.Equal(1, broken.Calls); // جُرّب مرة واحدة فقط، ثم تُخطّي
        Assert.True(health.IsSkipped("gemini:transcribe"));

        health.ResetProvider("gemini");
        Assert.False(health.IsSkipped("gemini:transcribe"));
    }

    [Fact]
    public async Task Both_failing_reports_all_reasons()
    {
        var online = new SlowEngine("gemini:m", TimeSpan.Zero, SpeechErrorKind.ServerError);
        var local = new SlowEngine("local:x.bin", TimeSpan.Zero, SpeechErrorKind.InvalidResponse);

        var ex = await Assert.ThrowsAsync<AllEnginesFailedException>(() =>
            new HedgedSpeechEngine(online, local, TimeSpan.FromSeconds(1)).TranscribeAsync(Audio, new()));

        Assert.Equal(["gemini:m", "local:x.bin"], ex.Failures.Select(f => f.EngineId));
    }

    [Fact]
    public async Task User_cancel_stops_everything()
    {
        var online = new SlowEngine("gemini:m", TimeSpan.FromSeconds(30));
        var local = new SlowEngine("local:x.bin", TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HedgedSpeechEngine(online, local, TimeSpan.FromMilliseconds(50)).TranscribeAsync(Audio, new(), cts.Token));
    }
}
