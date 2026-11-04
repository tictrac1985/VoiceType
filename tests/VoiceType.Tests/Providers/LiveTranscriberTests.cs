using System.Text.Json.Nodes;
using System.Threading.Channels;
using VoiceType.Providers.Live;
using Xunit;

namespace VoiceType.Tests.Providers;

/// <summary>الكتابة المباشرة أثناء الكلام (Gemini Live) — باتصال وهمي بدون إنترنت.</summary>
public class LiveTranscriberTests
{
    private sealed class FakeSocket : ILiveSocket
    {
        private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
        public readonly List<string> Sent = [];
        public Uri? Uri;
        public bool AckSetup = true;
        public string? RejectWith;

        public string? CloseReason { get; private set; }

        public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            Uri = uri;
            return Task.CompletedTask;
        }

        public Task SendAsync(string text, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add(text);
            }

            if (text.Contains("\"setup\"", StringComparison.Ordinal))
            {
                if (RejectWith is not null)
                {
                    CloseReason = RejectWith;
                    _incoming.Writer.TryWrite(null);
                }
                else if (AckSetup)
                {
                    _incoming.Writer.TryWrite("{\"setupComplete\":{}}");
                }
            }

            return Task.CompletedTask;
        }

        public void Push(string json) => _incoming.Writer.TryWrite(json);

        public void Drop(string reason)
        {
            CloseReason = reason;
            _incoming.Writer.TryWrite(null);
        }

        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken) => await _incoming.Reader.ReadAsync(cancellationToken);

        public Task CloseAsync() => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public string[] SentSnapshot()
        {
            lock (Sent)
            {
                return [.. Sent];
            }
        }
    }

    private static string Interim(string text) => new JsonObject { ["serverContent"] = new JsonObject { ["interimInputTranscription"] = new JsonObject { ["text"] = text } } }.ToJsonString();

    private static string Final(string text) => new JsonObject { ["serverContent"] = new JsonObject { ["inputTranscription"] = new JsonObject { ["text"] = text } } }.ToJsonString();

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [Fact]
    public void Setup_message_asks_for_egyptian_arabic_and_custom_words()
    {
        var json = JsonNode.Parse(LiveTranscriber.SetupMessage(new LiveOptions { LanguageCodes = ["ar-EG"], CustomVocabulary = ["Python", "Gmail"] }))!;
        var setup = json["setup"]!;
        Assert.Equal("models/gemini-3.5-transcribe-live", (string?)setup["model"]);
        Assert.Equal("TEXT", (string?)setup["generationConfig"]!["responseModalities"]![0]);
        Assert.Equal("ar-EG", (string?)setup["inputAudioTranscription"]!["languageCodes"]![0]);
        Assert.Equal("Gmail", (string?)setup["inputAudioTranscription"]!["customVocabulary"]![1]);
        Assert.Equal("SMART", (string?)setup["inputAudioTranscription"]!["mode"]);
    }

    [Fact]
    public async Task Streams_audio_and_reports_interim_and_final_text()
    {
        var socket = new FakeSocket();
        await using var live = new LiveTranscriber(["key-1"], new LiveOptions(), () => socket);
        var interims = new List<string>();
        var finals = new List<string>();
        live.Interim += interims.Add;
        live.Final += finals.Add;

        live.SendAudio(new byte[4000], 4000); // قبل الاتصال: يُخزّن ثم يُرسل
        await live.StartAsync();
        Assert.True(live.IsReady);
        Assert.Contains("key=key-1", socket.Uri!.Query, StringComparison.Ordinal);

        // الصوت المخزّن بيتبعت في الخلفية: نستنى لما يوصل قبل ما «الخادم» يرد (وإلا الاختبار يفشل لما الجهاز مشغول)
        await WaitFor(() => socket.SentSnapshot().Any(m => m.Contains("audio/pcm;rate=16000", StringComparison.Ordinal)));

        socket.Push(Interim("النهارده"));
        socket.Push(Interim("النهارده هنراجع"));
        socket.Push(Final("النهارده هنراجع الـ API."));
        await WaitFor(() => finals.Count == 1);

        Assert.Equal(["النهارده", "النهارده هنراجع"], interims);
        Assert.Equal("النهارده هنراجع الـ API.", finals[0]);
        Assert.Equal(4000, live.BytesAtLastFinal);

        Assert.True(await live.FinishAsync(TimeSpan.FromSeconds(3)));
        var sent = socket.SentSnapshot();
        Assert.Contains(sent, m => m.Contains("audio/pcm;rate=16000", StringComparison.Ordinal));
        Assert.EndsWith("{\"realtimeInput\":{\"audioStreamEnd\":true}}", sent[^1], StringComparison.Ordinal);
        Assert.Equal(string.Empty, live.PendingInterim);
    }

    [Fact]
    public async Task Tries_the_next_key_when_the_first_is_rejected()
    {
        var sockets = new Queue<FakeSocket>([new FakeSocket { RejectWith = "1007 API key not valid" }, new FakeSocket()]);
        var used = new List<FakeSocket>();
        await using var live = new LiveTranscriber(["bad", "good"], new LiveOptions(), () => { var s = sockets.Dequeue(); used.Add(s); return s; });

        await live.StartAsync();

        Assert.True(live.IsReady);
        Assert.Equal(2, used.Count);
        Assert.Contains("key=good", used[1].Uri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Throws_when_every_key_is_rejected_so_the_normal_path_is_used()
    {
        await using var live = new LiveTranscriber(["bad"], new LiveOptions(), () => new FakeSocket { RejectWith = "1008 model not found" });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => live.StartAsync());

        Assert.Contains("model not found", ex.Message, StringComparison.Ordinal);
        Assert.False(live.IsReady);
    }

    [Fact]
    public async Task Ready_even_without_setup_ack_if_the_connection_stays_open()
    {
        var socket = new FakeSocket { AckSetup = false };
        await using var live = new LiveTranscriber(["k"], new LiveOptions(), () => socket);

        await live.StartAsync();

        Assert.True(live.IsReady);
    }

    [Fact]
    public async Task Reports_failure_when_the_connection_drops_mid_speech()
    {
        var socket = new FakeSocket();
        await using var live = new LiveTranscriber(["k"], new LiveOptions(), () => socket);
        string? failure = null;
        live.Failed += r => failure = r;
        await live.StartAsync();

        socket.Push(Interim("كلام لم يكتمل"));
        socket.Drop("1011 internal error");
        await WaitFor(() => failure is not null);

        Assert.True(live.HasFailed);
        Assert.False(await live.FinishAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("كلام لم يكتمل", live.PendingInterim);
    }

    [Fact]
    public void Segmenter_writes_full_sentences_immediately()
    {
        var segmenter = new LiveSegmenter();
        var now = DateTime.UtcNow;

        Assert.Empty(segmenter.Add("النهارده هنراجع", now));
        var ready = segmenter.Add("الـ API.", now.AddMilliseconds(100));

        Assert.Equal(["النهارده هنراجع الـ API."], ready);
        Assert.False(segmenter.HasPending);
    }

    [Fact]
    public void Segmenter_writes_after_a_short_pause_when_there_is_no_punctuation()
    {
        var segmenter = new LiveSegmenter();
        var now = DateTime.UtcNow;
        segmenter.Add("افتح VS Code", now);

        Assert.Null(segmenter.Tick(now.AddMilliseconds(300)));
        Assert.Equal("افتح VS Code", segmenter.Tick(now.AddMilliseconds(700)));
    }

    [Theory]
    [InlineData(true, "جملة أولى.", "جملة تانية.", "\n")]
    [InlineData(false, "جملة أولى.", "جملة تانية.", " ")]
    [InlineData(true, "", "أول جملة.", "")]
    public void Separator_between_sentences(bool perLine, string previous, string next, string expected)
    {
        Assert.Equal(expected, LiveSegmenter.Separator(perLine, previous, next));
    }

    [Fact]
    public async Task Retries_with_basic_setup_when_an_optional_field_is_rejected()
    {
        var sockets = new List<FakeSocket>();
        await using var live = new LiveTranscriber(["k"], new LiveOptions { CustomVocabulary = ["Python"] }, () =>
        {
            var s = new FakeSocket { RejectWith = sockets.Count == 0 ? "1007 Invalid JSON payload received. Unknown name \"mode\"" : null };
            sockets.Add(s);
            return s;
        });

        await live.StartAsync();

        Assert.True(live.IsReady);
        Assert.Equal(2, sockets.Count);
        var setup = sockets[1].SentSnapshot()[0];
        Assert.DoesNotContain("\"mode\"", setup, StringComparison.Ordinal);
        Assert.DoesNotContain("customVocabulary", setup, StringComparison.Ordinal);
    }
}
