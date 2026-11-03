using System.Text;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;
using VoiceType.Evaluation;
using VoiceType.Speech;
using Xunit;
using Xunit.Abstractions;

namespace VoiceType.Tests.Speech;

/// <summary>v2.17: نموذج Cohere Arabic عن طريق محرك CrispASR في برنامج مساعد منفصل.</summary>
public class CrispEngineTests(ITestOutputHelper output)
{
    [Fact]
    public void Short_audio_is_one_chunk()
    {
        var chunks = AudioChunker.Split(new float[16000 * 20]);
        Assert.Equal([new AudioChunker.Chunk(0, 16000 * 20)], chunks);
    }

    [Fact]
    public void Long_audio_is_cut_at_the_quietest_point()
    {
        // 50 ثانية كلام (صوت عالي) فيها وقفة هادية عند الثانية 20
        var samples = new float[16000 * 50];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)Math.Sin(i * 0.05) * 0.5f;
        }

        Array.Clear(samples, 16000 * 20, 16000 / 2);

        var chunks = AudioChunker.Split(samples);

        Assert.True(chunks.Count >= 2);
        var cut = chunks[0].Length / 16000.0;
        Assert.InRange(cut, 20, 20.5);
        Assert.Equal(samples.Length, chunks.Sum(c => c.Length));
        Assert.All(chunks, c => Assert.InRange(c.Length, 1, 16000 * 28));
    }

    [Theory]
    [InlineData("cohere-transcribe-arabic-q4_k-imatrix.gguf", true)]
    [InlineData("cohere-transcribe-arabic-q8_0.gguf", true)]
    [InlineData("diar_streaming_sortformer_4spk-v2.q8_0.gguf", false)]
    [InlineData("ggml-large-v3-q5_0.bin", false)]
    public void Recognizes_crisp_models(string file, bool expected)
    {
        Assert.Equal(expected, LocalModels.IsCrispModel(file));
    }

    [Fact]
    public void Cohere_is_offered_but_never_first()
    {
        var cohere = Assert.Single(LocalModels.Suggested, m => m.FileName == LocalModels.CohereArabicFile);
        Assert.False(cohere.Recommended);
        Assert.StartsWith("https://huggingface.co/cstr/cohere-transcribe-arabic-07-2026-GGUF/resolve/main/", cohere.DownloadUrl);
        Assert.Contains("Q4", cohere.DisplayName);
    }

    [Fact]
    public async Task Missing_runtime_gives_a_clear_error()
    {
        var model = Path.GetTempFileName();
        try
        {
            using var engine = new CrispLocalEngine(model, hostPath: Path.Combine(Path.GetTempPath(), "missing-host"), libraryDirectory: Path.GetTempPath());
            var ex = await Assert.ThrowsAsync<SpeechException>(() =>
                engine.TranscribeSegmentsAsync(new float[16000], new TranscriptionOptions()));
            Assert.Equal(SpeechErrorKind.Configuration, ex.Kind);
            Assert.Contains("CrispASR", ex.Message);
        }
        finally
        {
            File.Delete(model);
        }
    }

    /// <summary>
    /// مقارنة حقيقية (بتشتغل بس لو الملفات موجودة):
    /// VT_CRISP_DIR = مجلد المساعد + مكتبة CrispASR، VT_COHERE_MODEL، VT_WHISPER_MODEL (اختياري)،
    /// VT_AUDIO_DIR = ملفات wav + refs.json (اسم الملف ← النص الصحيح).
    /// </summary>
    [Fact]
    public async Task Compare_cohere_and_whisper_on_real_audio()
    {
        var crispDir = Environment.GetEnvironmentVariable("VT_CRISP_DIR");
        var cohereModel = Environment.GetEnvironmentVariable("VT_COHERE_MODEL");
        var audioDir = Environment.GetEnvironmentVariable("VT_AUDIO_DIR");
        if (crispDir is null || cohereModel is null || audioDir is null)
        {
            output.WriteLine("skipped: set VT_CRISP_DIR, VT_COHERE_MODEL, VT_AUDIO_DIR");
            return;
        }

        var host = Path.Combine(crispDir, OperatingSystem.IsWindows() ? "VoiceType.CrispHost.exe" : "VoiceType.CrispHost");
        var engines = new List<ISpeechEngine> { new CrispLocalEngine(cohereModel, hostPath: host, libraryDirectory: crispDir) };
        if (Environment.GetEnvironmentVariable("VT_WHISPER_MODEL") is { } whisperModel)
        {
            WhisperLocalEngine.ConfigureRuntime();
            engines.Add(new WhisperLocalEngine(whisperModel));
        }

        var refs = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(audioDir, "refs.json")))!.AsObject();
        var report = new StringBuilder();
        foreach (var engine in engines)
        {
            int words = 0, errors = 0;
            var elapsed = TimeSpan.Zero;
            double audioSeconds = 0;
            foreach (var (name, reference) in refs.OrderBy(p => p.Key))
            {
                var wav = await File.ReadAllBytesAsync(Path.Combine(audioDir, name));
                audioSeconds += AudioConverter.ToMono16kSamples(wav).Length / 16000.0;
                var started = DateTime.UtcNow;
                var result = await engine.TranscribeAsync(new AudioPayload(wav, "wav", name), new TranscriptionOptions { Language = "ar" });
                elapsed += DateTime.UtcNow - started;
                var score = TranscriptScorer.Score((string)reference!, result.Text);
                words += score.ReferenceWords;
                errors += score.WordErrors;
                output.WriteLine($"[{engine.Id}] {name} WER={score.Wer:P0}: {result.Text}");
            }

            report.AppendLine($"{engine.Id}: WER={(double)errors / words:P1}  time={elapsed.TotalSeconds:0.0}s for {audioSeconds:0.0}s audio");
            Assert.True((double)errors / words < 0.5, $"{engine.Id} is far off");
            (engine as IDisposable)?.Dispose();
        }

        output.WriteLine(report.ToString());
    }
}
