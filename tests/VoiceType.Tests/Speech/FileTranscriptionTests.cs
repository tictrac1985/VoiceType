using VoiceType.Core.Speech;
using VoiceType.Speech;
using VoiceType.Text;

namespace VoiceType.Tests.Speech;

public class FileTranscriptionTests
{
    private sealed class EchoEngine : ISpeechEngine
    {
        public int Calls { get; private set; }

        public string Id => "echo";

        public string DisplayName => "echo";

        public SpeechCapabilities Capabilities { get; } = new();

        public Task<TranscriptionResult> TranscribeAsync(AudioPayload audio, TranscriptionOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new TranscriptionResult { Text = $"جزء رقم {Calls}", EngineId = Id });
        }
    }

    private static string WriteSilenceWav(double seconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"vt-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(path, SampleAudio.Silence(seconds).Data);
        return path;
    }

    [Fact]
    public async Task Long_files_are_sent_to_online_engines_in_chunks()
    {
        var path = WriteSilenceWav(250);
        try
        {
            var engine = new EchoEngine();
            var transcript = await new FileTranscriber { ChunkLength = TimeSpan.FromMinutes(1) }
                .TranscribeAsync(path, engine, new TranscriptionOptions());

            Assert.InRange(engine.Calls, 4, 5);
            Assert.Equal(engine.Calls, transcript.Segments.Count);
            Assert.InRange(transcript.AudioDuration.TotalSeconds, 249, 251);
            Assert.Equal(TimeSpan.Zero, transcript.Segments[0].Start);
            Assert.Contains("جزء رقم 1", transcript.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Chunks_cover_the_whole_audio_without_gaps()
    {
        var samples = new float[16000 * 130];
        var parts = AudioDecoder.SplitAtQuietPoints(samples, 16000 * 60);
        Assert.Equal(0, parts[0].Start);
        for (var i = 1; i < parts.Count; i++)
        {
            Assert.Equal(parts[i - 1].Start + parts[i - 1].Length, parts[i].Start);
        }

        Assert.Equal(samples.Length, parts[^1].Start + parts[^1].Length);
    }

    [Fact]
    public void Segments_become_punctuated_paragraphs_at_long_pauses()
    {
        var segments = new List<TranscriptSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "السلام عليكم يا جماعة"),
            new(TimeSpan.FromSeconds(3.2), TimeSpan.FromSeconds(6), "النهارده هنتكلم عن المشروع"),
            new(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(12), "هل في أسئلة"),
        };

        var text = TranscriptFormatter.ToParagraphs(segments);

        Assert.Equal("السلام عليكم يا جماعة النهارده هنتكلم عن المشروع.\n\nهل في أسئلة؟", text);
    }

    [Fact]
    public void Exports_txt_srt_and_word()
    {
        var folder = Directory.CreateTempSubdirectory();
        try
        {
            var segments = new List<TranscriptSegment> { new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), "مرحبا") };
            TranscriptExporter.SaveText(Path.Combine(folder.FullName, "a.txt"), "سطر أول\n\nسطر ثاني");
            TranscriptExporter.SaveSrt(Path.Combine(folder.FullName, "a.srt"), segments);
            TranscriptExporter.SaveDocx(Path.Combine(folder.FullName, "a.docx"), "عنوان", "فقرة أولى\n\nفقرة ثانية");

            Assert.Contains("00:00:01,500 --> 00:00:04,000", File.ReadAllText(Path.Combine(folder.FullName, "a.srt")));
            Assert.Contains("سطر ثاني", File.ReadAllText(Path.Combine(folder.FullName, "a.txt")));
            Assert.True(new FileInfo(Path.Combine(folder.FullName, "a.docx")).Length > 1000);
        }
        finally
        {
            folder.Delete(true);
        }
    }

    [Fact]
    public async Task Local_model_transcribes_a_file_with_timestamps_when_available()
    {
        var model = Environment.GetEnvironmentVariable("VOICETYPE_TEST_MODEL");
        var wav = Environment.GetEnvironmentVariable("VOICETYPE_TEST_LONG_WAV");
        if (model is null || wav is null || !File.Exists(model) || !File.Exists(wav))
        {
            return;
        }

        using var engine = new WhisperLocalEngine(model);
        var transcript = await new FileTranscriber().TranscribeAsync(wav, engine, new TranscriptionOptions { Language = "en" });

        Console.WriteLine("FILE RESULT: " + transcript.Text.Replace("\n", " | "));
        Assert.NotEmpty(transcript.Segments);
        Assert.Contains("project", transcript.Text, StringComparison.OrdinalIgnoreCase);
    }
}
