using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;
using VoiceType.Providers.Rewrite;
using VoiceType.Speech;
using VoiceType.Tests.Support;
using VoiceType.Text;
using Xunit;
using Xunit.Abstractions;

namespace VoiceType.Tests.Speech;

/// <summary>v2.18: صدى برنامج واحد (مميزات Vibe + Ollama/LM Studio + التحديثات).</summary>
public class SadaOneAppTests(ITestOutputHelper output)
{
    private static TranscriptSegment Seg(double a, double b, string text, int? who = null) =>
        new(TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b), text, who);

    // ===== تجميع الكلمات في جمل =====
    [Fact]
    public void Words_are_grouped_at_pauses_and_sentence_ends()
    {
        var words = new List<WordGrouping.Word>
        {
            new(TimeSpan.FromSeconds(0.0), TimeSpan.FromSeconds(0.4), "أهلا"),
            new(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(0.9), "بيك."),
            new(TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.3), "إزيك"),
            new(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.9), "النهارده"),
        };

        var groups = WordGrouping.Group(words);

        Assert.Equal(["أهلا بيك.", "إزيك", "النهارده"], groups.Select(g => g.Text));
        Assert.Equal(TimeSpan.FromSeconds(0.9), groups[0].End);
    }

    [Fact]
    public void A_stretched_last_word_does_not_swallow_the_silence_after_it()
    {
        var words = new List<WordGrouping.Word>
        {
            new(TimeSpan.FromSeconds(9.0), TimeSpan.FromSeconds(9.5), "لا"),
            new(TimeSpan.FromSeconds(9.6), TimeSpan.FromSeconds(12.5), "البقاء."), // ممدودة لحد كلام المتكلم التاني
        };
        var group = Assert.Single(WordGrouping.Group(words));
        Assert.Equal(TimeSpan.FromSeconds(10.6), group.End);
    }

    [Fact]
    public void Punctuation_comes_from_the_full_text_when_word_counts_match()
    {
        var words = new List<WordGrouping.Word> { new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "مرحبا"), new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "يا صاحبي") };
        Assert.Equal("مرحبا،", WordGrouping.WithPunctuation([words[0], new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "صاحبي")], "مرحبا، صاحبي.")[0].Text);
        Assert.Same(words, WordGrouping.WithPunctuation(words, "نص مختلف تمامًا هنا")); // العدد مختلف: زي ما هي
    }

    // ===== مين اللي بيتكلم =====
    [Fact]
    public void Speakers_are_numbered_by_first_appearance_and_gaps_are_filled()
    {
        var segments = new[] { Seg(0, 1, "a"), Seg(1, 2, "b"), Seg(2, 3, "c"), Seg(3, 4, "d") };
        var labeled = SpeakerDiarizer.Apply(segments, [3, -1, 1, 3]);
        Assert.Equal([0, 0, 1, 0], labeled.Select(s => s.Speaker));
    }

    [Fact]
    public void Transcript_with_speakers_is_written_as_turns()
    {
        var text = TranscriptFormatter.ToParagraphs([Seg(0, 1, "أهلا", 0), Seg(1, 2, "إزيك", 0), Seg(2, 3, "تمام", 1)]);
        Assert.StartsWith("المتكلم 1: أهلا إزيك", text);
        Assert.Contains("\n\nالمتكلم 2: تمام", text);
    }

    // ===== التصدير =====
    [Fact]
    public void Long_captions_are_split_by_words_and_time_is_shared()
    {
        var cues = TranscriptExporter.Cues([Seg(0, 6, "واحد اتنين تلاتة أربعة خمسة ستة سبعة تمانية", 0)], maxChars: 20);
        Assert.True(cues.Count >= 2);
        Assert.All(cues, c => Assert.True(c.Text.Replace("[المتكلم 1] ", "", StringComparison.Ordinal).Length <= 20));
        Assert.StartsWith("[المتكلم 1] ", cues[0].Text);
        Assert.Equal(TimeSpan.Zero, cues[0].Segment.Start);
        Assert.Equal(TimeSpan.FromSeconds(6), cues[^1].Segment.End);
    }

    [Fact]
    public void Vtt_srt_json_and_html_exports_are_valid()
    {
        var segments = new[] { Seg(1.5, 3.25, "أهلا بيك", 0), Seg(4, 5, "Hello", 1) };
        var vtt = TranscriptExporter.ToVtt(segments);
        Assert.StartsWith("WEBVTT", vtt);
        Assert.Contains("00:00:01.500 --> 00:00:03.250", vtt);
        Assert.Contains("00:00:01,500 --> 00:00:03,250", TranscriptExporter.ToSrt(segments));

        var json = JsonNode.Parse(TranscriptExporter.ToJson("اجتماع", "نص", segments, "local:x", TimeSpan.FromSeconds(5)))!;
        Assert.Equal(2, (int)json["speakers"]!);
        Assert.Equal("المتكلم 2", (string)json["segments"]![1]!["speaker"]!);
        Assert.Equal(1.5, (double)json["segments"]![0]!["start"]!);

        var html = TranscriptExporter.ToHtml("اجتماع <1>", "نص", segments);
        Assert.Contains("dir=\"rtl\"", html);
        Assert.Contains("اجتماع &lt;1&gt;", html);
        Assert.Contains("المتكلم 2", html);
    }

    // ===== Ollama / LM Studio =====
    [Fact]
    public async Task Ollama_request_and_answer_without_thinking()
    {
        var handler = FakeHttpHandler.Json("""{"message":{"role":"assistant","content":"<think>بفكر…</think>السلام عليكم."}}""");
        var engine = new TextServerEngine(handler.CreateClient(), TextServerKind.Ollama, "http://127.0.0.1:11434/", "qwen3:8b");

        var text = await engine.GenerateAsync("صيغها", "ازيك", 0.3, CancellationToken.None);

        Assert.Equal("السلام عليكم.", text);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://127.0.0.1:11434/api/chat", request.Uri.ToString());
        var body = JsonNode.Parse(request.BodyText)!;
        Assert.Equal("qwen3:8b", (string)body["model"]!);
        Assert.False((bool)body["stream"]!);
        Assert.False((bool)body["think"]!);
        Assert.Equal("system", (string)body["messages"]![0]!["role"]!);
    }

    [Fact]
    public async Task Lm_studio_uses_openai_chat_completions()
    {
        var handler = FakeHttpHandler.Json("""{"choices":[{"message":{"content":"Hello."}}]}""");
        var engine = new TextServerEngine(handler.CreateClient(), TextServerKind.OpenAiCompatible, "http://127.0.0.1:1234/v1", "gemma");

        Assert.Equal("Hello.", await engine.GenerateAsync("x", "y", 0.2, CancellationToken.None));
        Assert.Equal("http://127.0.0.1:1234/v1/chat/completions", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task Ollama_cloud_sends_the_key_as_bearer()
    {
        var handler = FakeHttpHandler.Json("""{"message":{"content":"ok"}}""");
        var engine = new TextServerEngine(handler.CreateClient(), TextServerKind.OllamaCloud, TextServerEngine.OllamaCloudUrl, "gpt-oss:120b", "secret-key");
        await engine.GenerateAsync("x", "y", 0.2, CancellationToken.None);
        Assert.Equal("Bearer secret-key", handler.Requests[0].Headers["Authorization"]);
        Assert.Equal("https://ollama.com/api/chat", handler.Requests[0].Uri.ToString());
    }

    [Fact]
    public void Models_are_listed_without_embedding_models()
    {
        Assert.Equal(["gemma", "qwen3:8b"], TextServerEngine.ParseModels(TextServerKind.Ollama,
            """{"models":[{"name":"qwen3:8b"},{"name":"nomic-embed-text"},{"name":"gemma"}]}"""));
        Assert.Equal(["huihui-gemma-4"], TextServerEngine.ParseModels(TextServerKind.OpenAiCompatible,
            """{"data":[{"id":"huihui-gemma-4"},{"id":"text-embedding-nomic-embed-text-v1.5"}]}"""));
    }

    [Fact]
    public void Bad_addresses_are_refused()
    {
        Assert.Throws<ArgumentException>(() => TextServerEngine.NormalizeUrl("ftp://x"));
        Assert.Equal("http://192.168.1.5:1234", TextServerEngine.NormalizeUrl(" http://192.168.1.5:1234/v1/ "));
    }

    [Fact]
    public async Task Chosen_engine_failure_falls_back_to_gemini()
    {
        var failing = new FailingEngine();
        var gemini = FakeHttpHandler.Json("""{"candidates":[{"content":{"parts":[{"text":"ملخص"}]}}]}""");
        var rewriter = new TextRewriter(gemini.CreateClient(), ["k"], preferred: [failing]);

        Assert.Equal("ملخص", await rewriter.SummarizeAsync("نص"));
        Assert.StartsWith("Gemini", rewriter.LastEngine);
        Assert.Single(rewriter.LastFailures);
    }

    [Fact]
    public async Task Chosen_engine_works_without_any_gemini_key()
    {
        var handler = FakeHttpHandler.Json("""{"message":{"content":"Translated."}}""");
        var engine = new TextServerEngine(handler.CreateClient(), TextServerKind.Ollama, TextServerEngine.OllamaLocalUrl, "qwen3:8b");
        var rewriter = new TextRewriter(handler.CreateClient(), [], preferred: [engine]);

        Assert.Equal("Translated.\n\nTranslated.", await rewriter.TranslateAsync("فقرة أولى طويلة\n\nفقرة تانية طويلة", maxChunkChars: 10));
        Assert.Equal(2, handler.Requests.Count); // كل فقرة لوحدها
    }

    // ===== تنزيل اللينكات =====
    [Fact]
    public void Yt_dlp_hash_is_read_from_the_official_sums_file()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, LinkDownloader.ExpectedHash($"{new string('b', 64)}  yt-dlp\n{hash}  yt-dlp.exe\n", "yt-dlp.exe"));
        Assert.Null(LinkDownloader.ExpectedHash("abc  yt-dlp.exe", "yt-dlp.exe"));
        Assert.True(LinkDownloader.IsValidLink("https://www.youtube.com/watch?v=x"));
        Assert.False(LinkDownloader.IsValidLink("file:///C:/x"));
        Assert.False(LinkDownloader.IsValidLink("مش لينك"));
    }

    // ===== التحديثات =====
    [Fact]
    public void Update_is_offered_only_when_newer_and_complete()
    {
        const string json = """
            {"tag_name":"v2.19.0","body":"تحسينات","assets":[
              {"name":"Sada-win-x64.zip","browser_download_url":"https://github.com/x/zip"},
              {"name":"Sada-win-x64.zip.sha256","browser_download_url":"https://github.com/x/sha"}]}
            """;
        var newer = UpdateService.Parse(json, new Version(2, 18, 0));
        Assert.True(newer.Available);
        Assert.Equal("2.19.0", newer.Latest);
        Assert.Equal("https://github.com/x/zip", newer.ZipUrl);

        Assert.False(UpdateService.Parse(json, new Version(2, 19, 0)).Available);
        Assert.False(UpdateService.Parse(json.Replace("Sada-win-x64.zip.sha256", "other", StringComparison.Ordinal), new Version(2, 0, 0)).Available);
    }

    [Fact]
    public void Update_replaces_program_files_but_never_user_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "vt-upd-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var staging = Path.Combine(root, "new");
        Directory.CreateDirectory(Path.Combine(app, "data"));
        Directory.CreateDirectory(Path.Combine(staging, "data"));
        Directory.CreateDirectory(Path.Combine(staging, "crispasr"));
        File.WriteAllText(Path.Combine(app, "VoiceType.exe"), "old");
        File.WriteAllText(Path.Combine(app, "data", "settings.json"), "mine");
        File.WriteAllText(Path.Combine(staging, "VoiceType.exe"), "new");
        File.WriteAllText(Path.Combine(staging, "crispasr", "VoiceType.CrispHost.exe"), "host");
        File.WriteAllText(Path.Combine(staging, "data", "settings.json"), "theirs");
        try
        {
            UpdateService.Apply(staging, app);
            Assert.Equal("new", File.ReadAllText(Path.Combine(app, "VoiceType.exe")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(app, "VoiceType.exe.old")));
            Assert.Equal("host", File.ReadAllText(Path.Combine(app, "crispasr", "VoiceType.CrispHost.exe")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "data", "settings.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// تجربة حقيقية للتحديث من GitHub على نسخة وهمية من البرنامج (بتشتغل بس لو VT_REAL_UPDATE=1):
    /// بنعتبر النسخة الحالية 0.1.0، فينزّل آخر إصدار ويتأكد من بصمته ويركّبه، ومجلد data ميتلمسش.
    /// </summary>
    [Fact]
    public async Task Real_update_from_github_installs_into_a_copy()
    {
        if (Environment.GetEnvironmentVariable("VT_REAL_UPDATE") != "1")
        {
            output.WriteLine("skipped: set VT_REAL_UPDATE=1");
            return;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Sada-VoiceType-Updater");
        var json = await http.GetStringAsync($"https://api.github.com/repos/{UpdateService.Repository}/releases/latest");
        var update = UpdateService.Parse(json, new Version(0, 1, 0));
        Assert.True(update.Available);

        var app = Path.Combine(Path.GetTempPath(), "vt-real-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(app, "data"));
        File.WriteAllText(Path.Combine(app, "VoiceType.exe"), "old");
        File.WriteAllText(Path.Combine(app, "data", "settings.json"), "mine");
        try
        {
            await UpdateService.InstallAsync(update, null, CancellationToken.None, app);
            Assert.True(new FileInfo(Path.Combine(app, "VoiceType.exe")).Length > 1_000_000);
            Assert.Equal("old", File.ReadAllText(Path.Combine(app, "VoiceType.exe.old")));
            Assert.True(File.Exists(Path.Combine(app, "crispasr", "VoiceType.CrispHost.exe")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(app, "data", "settings.json")));
            output.WriteLine($"installed {update.Latest} into a copy");
        }
        finally
        {
            Directory.Delete(app, recursive: true);
        }
    }

    [Fact]
    public void Failed_update_puts_the_old_files_back()
    {
        var root = Path.Combine(Path.GetTempPath(), "vt-upd-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "app");
        var staging = Path.Combine(root, "new");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.Combine(staging, "zzz"));
        File.WriteAllText(Path.Combine(app, "VoiceType.exe"), "old");
        File.WriteAllText(Path.Combine(app, "zzz"), "a file where the update needs a folder"); // يخلي النسخ يفشل في النص
        File.WriteAllText(Path.Combine(staging, "VoiceType.exe"), "new");
        File.WriteAllText(Path.Combine(staging, "added.dll"), "new file");
        File.WriteAllText(Path.Combine(staging, "zzz", "inner.dll"), "x");
        try
        {
            var ex = Assert.Throws<IOException>(() => UpdateService.Apply(staging, app));
            Assert.Contains("رجّعنا النسخة القديمة", ex.Message);
            Assert.Equal("old", File.ReadAllText(Path.Combine(app, "VoiceType.exe")));
            Assert.False(File.Exists(Path.Combine(app, "added.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Captions_never_break_on_blank_lines_or_markup()
    {
        var segments = new[] { Seg(0, 2, "سطر أول\n\nسطر تاني <b> & --> كمان") };
        var vtt = TranscriptExporter.ToVtt(segments);
        var srt = TranscriptExporter.ToSrt(segments);
        Assert.Contains("سطر أول سطر تاني &lt;b&gt; &amp; --&gt; كمان", vtt);
        Assert.Single(vtt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Skip(1));
        Assert.Single(srt.Split("\r\n\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Surround_audio_keeps_every_channel()
    {
        // 6 قنوات (5.1): الكلام في قناة النص بس (رقم 3)
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(16000, 6);
        var frames = 1600;
        var bytes = new byte[frames * 6 * 4];
        for (var f = 0; f < frames; f++)
        {
            BitConverter.GetBytes(0.6f).CopyTo(bytes, ((f * 6) + 2) * 4);
        }

        var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(new NAudio.Wave.RawSourceWaveStream(new MemoryStream(bytes), format));
        var mono = new DownmixToMonoProvider(provider);
        var buffer = new float[frames];
        var read = mono.Read(buffer, 0, frames);

        Assert.Equal(frames, read);
        Assert.Equal(1, mono.WaveFormat.Channels);
        Assert.All(buffer, v => Assert.Equal(0.1f, v, 3));
    }

    [Theory]
    [InlineData("{\"running\":true,\"port\":1235}", "http://127.0.0.1:1235")]
    [InlineData("noise\n{\"running\":true,\"port\":1234}\n", "http://127.0.0.1:1234")]
    [InlineData("{\"running\":false}", null)]
    [InlineData("The server is not running.", null)]
    [InlineData(null, null)]
    public void Lm_studio_port_is_read_from_its_status(string? output, string? expected)
    {
        Assert.Equal(expected, LocalTextServers.ParseLmStudioStatus(output));
    }

    /// <summary>
    /// تجربة حقيقية مع Ollama و LM Studio على الجهاز (بتشتغل بس لو اتحدد VT_OLLAMA_MODEL أو VT_LMSTUDIO_MODEL).
    /// </summary>
    [Fact]
    public async Task Real_local_ai_servers_summarize()
    {
        var targets = new List<(TextServerKind Kind, string Url, string Model)>();
        if (Environment.GetEnvironmentVariable("VT_OLLAMA_MODEL") is { Length: > 0 } ollama)
        {
            targets.Add((TextServerKind.Ollama, TextServerEngine.OllamaLocalUrl, ollama));
        }

        if (Environment.GetEnvironmentVariable("VT_LMSTUDIO_MODEL") is { Length: > 0 } lmstudio)
        {
            var url = await LocalTextServers.LmStudioRunningUrlAsync(CancellationToken.None) ?? TextServerEngine.LmStudioLocalUrl;
            targets.Add((TextServerKind.OpenAiCompatible, url, lmstudio));
        }

        if (targets.Count == 0)
        {
            output.WriteLine("skipped: set VT_OLLAMA_MODEL and/or VT_LMSTUDIO_MODEL");
            return;
        }

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        foreach (var (kind, url, model) in targets)
        {
            var models = await TextServerEngine.ListModelsAsync(http, kind, url, null, CancellationToken.None);
            output.WriteLine($"{kind}: {string.Join(", ", models)}");
            Assert.Contains(model, models);

            var rewriter = new TextRewriter(http, [], preferred: [new TextServerEngine(http, kind, url, model)]);
            var started = DateTime.UtcNow;
            var summary = await rewriter.SummarizeAsync(
                "اجتماع النهارده: اتفقنا إن أحمد يبعت التقرير يوم الخميس، ومنى هتكلم العميل بكرة الصبح، وهنأجل إطلاق البرنامج أسبوع.",
                CancellationToken.None);
            output.WriteLine($"[{rewriter.LastEngine}] {(DateTime.UtcNow - started).TotalSeconds:0}s\n{summary}");
            Assert.False(string.IsNullOrWhiteSpace(summary));
            Assert.Contains("أحمد", summary);
            Assert.DoesNotContain("<think>", summary);
        }
    }

    // ===== الاجتماعات =====
    [Fact]
    public void Live_preview_resampling_keeps_duration()
    {
        var samples = new float[48000];
        Assert.Equal(16000, MeetingRecorder.Resample(samples, 48000).Length);
        Assert.Same(samples, MeetingRecorder.Resample(samples, 16000));
    }

    /// <summary>
    /// تجربة حقيقية لمعرفة المتكلمين (بتشتغل بس لو الملفات موجودة):
    /// VT_CRISP_DIR + VT_COHERE_MODEL + VT_EMBEDDER + VT_CONVERSATION (ملف f32 لمحادثة فيها صوتين).
    /// </summary>
    [Fact]
    public async Task Real_conversation_gets_two_speakers()
    {
        var dir = Environment.GetEnvironmentVariable("VT_CRISP_DIR");
        var model = Environment.GetEnvironmentVariable("VT_COHERE_MODEL");
        var embedder = Environment.GetEnvironmentVariable("VT_EMBEDDER");
        var conversation = Environment.GetEnvironmentVariable("VT_CONVERSATION");
        if (dir is null || model is null || embedder is null || conversation is null)
        {
            output.WriteLine("skipped: set VT_CRISP_DIR, VT_COHERE_MODEL, VT_EMBEDDER, VT_CONVERSATION");
            return;
        }

        var host = Path.Combine(dir, OperatingSystem.IsWindows() ? "VoiceType.CrispHost.exe" : "VoiceType.CrispHost");
        var bytes = await File.ReadAllBytesAsync(conversation);
        var samples = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);

        using var engine = new CrispLocalEngine(model, hostPath: host, libraryDirectory: dir);
        var segments = await engine.TranscribeSegmentsAsync(samples, new TranscriptionOptions { Language = "ar" });
        Assert.True(segments.Count > 6, "should give sentence-sized segments from word timings");
        Assert.All(segments, s => Assert.True((s.End - s.Start).TotalSeconds <= 6));

        using var diarizer = new SpeakerDiarizer(embedder, host, dir);
        var labeled = await diarizer.LabelAsync(samples, segments, speakers: 2);
        foreach (var s in labeled)
        {
            output.WriteLine($"[{s.Start:mm\\:ss\\.f}-{s.End:mm\\:ss\\.f}] {TranscriptSegment.SpeakerName(s.Speaker ?? -1)}: {s.Text}");
        }

        Assert.Equal(2, labeled.Select(s => s.Speaker).Distinct().Count());
        // المحادثة: A (0-10.7) ثم B (11.4-21.8) ثم A (22.5-37.3) ثم B (38-48.2)
        var first = labeled.First(s => s.Start.TotalSeconds < 9).Speaker;
        var second = labeled.First(s => s.Start.TotalSeconds is > 12 and < 21).Speaker;
        Assert.NotEqual(first, second);
        output.WriteLine(TranscriptFormatter.ToParagraphs(labeled));
    }

    private sealed class FailingEngine : ITextEngine
    {
        public string Name => "LM Studio · test";

        public Task<string> GenerateAsync(string instructions, string text, double temperature, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("LM Studio مش شغال");
    }
}
