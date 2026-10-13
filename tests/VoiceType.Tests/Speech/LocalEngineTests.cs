using VoiceType.Core.Speech;
using VoiceType.Speech;

namespace VoiceType.Tests.Speech;

public class LocalEngineTests
{
    // اختبار حقيقي للتفريغ المحلي: يعمل فقط إذا وُجد نموذج وملف صوتي للاختبار
    // VOICETYPE_TEST_MODEL=path/to/ggml-tiny.bin  VOICETYPE_TEST_WAV=path/to/hello.wav
    private static readonly string? ModelPath = Environment.GetEnvironmentVariable("VOICETYPE_TEST_MODEL");
    private static readonly string? WavPath = Environment.GetEnvironmentVariable("VOICETYPE_TEST_WAV");

    [Fact]
    public async Task Local_whisper_transcribes_speech_when_model_is_available()
    {
        if (ModelPath is null || WavPath is null || !File.Exists(ModelPath) || !File.Exists(WavPath))
        {
            return; // لا يوجد نموذج اختبار في هذه البيئة
        }

        using var engine = new WhisperLocalEngine(ModelPath);
        var result = await engine.TranscribeAsync(AudioPayload.FromFile(WavPath), new TranscriptionOptions { Language = "en" });

        Console.WriteLine($"LOCAL RESULT [{WhisperLocalEngine.LoadedRuntime}] {result.Elapsed.TotalSeconds:0.00}s: {result.Text}");
        Assert.Contains("hello", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(WhisperLocalEngine.LoadedRuntime);
    }

    [Fact]
    public async Task Local_engine_rejects_non_wav_audio()
    {
        using var engine = new WhisperLocalEngine("/nonexistent/model.bin");
        var ex = await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(TestSupport.Mp3(), new TranscriptionOptions()));
        Assert.Equal(SpeechErrorKind.UnsupportedFormat, ex.Kind);
    }

    [Fact]
    public async Task Missing_model_is_a_configuration_error()
    {
        using var engine = new WhisperLocalEngine("/nonexistent/model.bin");
        var ex = await Assert.ThrowsAsync<SpeechException>(() => engine.TranscribeAsync(SampleAudio.Silence(), new TranscriptionOptions()));
        Assert.Equal(SpeechErrorKind.Configuration, ex.Kind);
    }

    [Fact]
    public void Audio_is_converted_to_16k_mono()
    {
        var samples = AudioConverter.ToMono16kSamples(SampleAudio.Silence(2).Data);
        Assert.InRange(samples.Length, 31_000, 33_000);
    }

    [Fact]
    public void Suggested_models_have_valid_download_links()
    {
        Assert.Contains(LocalModels.Suggested, m => m.Recommended);
        Assert.All(LocalModels.Suggested, m =>
        {
            Assert.EndsWith(".bin", m.FileName);
            Assert.StartsWith("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/", m.DownloadUrl);
        });
    }

    private static class TestSupport
    {
        public static AudioPayload Mp3() => new([1, 2, 3], "mp3");
    }
}
