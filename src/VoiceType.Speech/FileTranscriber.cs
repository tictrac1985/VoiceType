using System.Diagnostics;
using NAudio.Utils;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceType.Core.Speech;
using VoiceType.Providers.Resilience;
using VoiceType.Text;

namespace VoiceType.Speech;

public sealed record FileTranscript(
    string FilePath,
    string Text,
    IReadOnlyList<TranscriptSegment> Segments,
    string EngineId,
    TimeSpan AudioDuration,
    TimeSpan Elapsed)
{
    /// <summary>هل اتعرف مين اللي قال كل جملة؟</summary>
    public bool HasSpeakers => Segments.Any(s => s.Speaker is >= 0);

    /// <summary>عدد المتكلمين اللي اتعرفوا.</summary>
    public int SpeakerCount => Segments.Where(s => s.Speaker is >= 0).Select(s => s.Speaker).Distinct().Count();
}

public sealed record FileTranscriptionProgress(double Fraction, string Stage);

/// <summary>
/// تفريغ ملف صوتي كامل (أي طول، أي صيغة شائعة) إلى نص منسق في فقرات.
/// النموذج المحلي يعالج الملف كاملًا مع التوقيتات. خدمات الإنترنت تُرسل لها أجزاء متتالية.
/// </summary>
public sealed class FileTranscriber
{
    /// <summary>طول الجزء المرسل لخدمات الإنترنت.</summary>
    public TimeSpan ChunkLength { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// «مين اللي بيتكلم» (اختياري): بيدّي كل جملة رقم متكلم. بيشتغل بس مع النماذج اللي على الجهاز،
    /// لأنها بترجّع وقت كل جملة. خدمات الإنترنت بترجّع أجزاء طويلة، فمش بنستخدمه معاها.
    /// </summary>
    public Func<float[], IReadOnlyList<TranscriptSegment>, CancellationToken, Task<IReadOnlyList<TranscriptSegment>>>? SpeakerLabeler { get; init; }

    public async Task<FileTranscript> TranscribeAsync(
        string path,
        ISpeechEngine engine,
        TranscriptionOptions options,
        IProgress<FileTranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new FileTranscriptionProgress(0, "جارٍ قراءة الملف الصوتي…"));
        var samples = await Task.Run(() => AudioDecoder.DecodeToMono16k(path), cancellationToken).ConfigureAwait(false);
        var duration = TimeSpan.FromSeconds(samples.Length / (double)AudioConverter.SampleRate);

        IReadOnlyList<TranscriptSegment> segments;
        var segmented = engine as ISegmentedSpeechEngine ?? (engine as ResilientSpeechEngine)?.Inner as ISegmentedSpeechEngine;
        if (segmented is not null)
        {
            progress?.Report(new FileTranscriptionProgress(0.02, "جارٍ التفريغ على جهازك…"));
            segments = await segmented.TranscribeSegmentsAsync(samples, options,
                new Progress<double>(f => progress?.Report(new FileTranscriptionProgress(SpeakerLabeler is null ? f : f * 0.9, "جارٍ التفريغ على جهازك…"))),
                cancellationToken).ConfigureAwait(false);
            if (SpeakerLabeler is not null && segments.Count > 1)
            {
                progress?.Report(new FileTranscriptionProgress(0.92, "جارٍ معرفة مين اللي بيتكلم…"));
                segments = await SpeakerLabeler(samples, segments, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            segments = await TranscribeInChunksAsync(samples, engine, options, progress, cancellationToken).ConfigureAwait(false);
        }

        stopwatch.Stop();
        progress?.Report(new FileTranscriptionProgress(1, "تم."));
        return new FileTranscript(path, TranscriptFormatter.ToParagraphs(segments), segments, engine.Id, duration, stopwatch.Elapsed);
    }

    private async Task<IReadOnlyList<TranscriptSegment>> TranscribeInChunksAsync(
        float[] samples,
        ISpeechEngine engine,
        TranscriptionOptions options,
        IProgress<FileTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var bounds = AudioDecoder.SplitAtQuietPoints(samples, (int)(ChunkLength.TotalSeconds * AudioConverter.SampleRate));
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < bounds.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (start, length) = bounds[i];
            progress?.Report(new FileTranscriptionProgress((double)i / bounds.Count, $"جارٍ التفريغ… الجزء {i + 1} من {bounds.Count}"));

            var wav = AudioDecoder.ToWav16k(samples, start, length);
            var result = await engine.TranscribeAsync(new AudioPayload(wav, "wav", $"part-{i + 1}.wav"), options, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                segments.Add(new TranscriptSegment(
                    TimeSpan.FromSeconds(start / (double)AudioConverter.SampleRate),
                    TimeSpan.FromSeconds((start + length) / (double)AudioConverter.SampleRate),
                    result.Text.Trim()));
            }
        }

        return segments;
    }
}

/// <summary>قراءة الملفات الصوتية وتحويلها إلى 16kHz mono.</summary>
public static class AudioDecoder
{
    public static readonly IReadOnlyList<string> SupportedExtensions =
        [".wav", ".mp3", ".m4a", ".aac", ".wma", ".mp4", ".flac", ".ogg", ".opus", ".webm", ".amr", ".3gp"];

    public static float[] DecodeToMono16k(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("الملف غير موجود.", path);
        }

        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return AudioConverter.ToMono16kSamples(File.ReadAllBytes(path));
            }
            catch (FormatException)
            {
                // ملفات WAV ذات ترميز غير شائع: نجرب قارئ Windows
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new SpeechException(SpeechErrorKind.UnsupportedFormat, "file", "Only WAV files can be read on this platform.");
        }

        try
        {
            using var reader = new MediaFoundationReader(path);
            return ReadAll(reader.ToSampleProvider());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            throw new SpeechException(SpeechErrorKind.UnsupportedFormat, "file",
                "تعذّرت قراءة الملف. جرّب تحويله إلى MP3 أو WAV.  " + ex.Message, innerException: ex);
        }
    }

    internal static float[] ReadAll(ISampleProvider provider)
    {
        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            provider = new DownmixToMonoProvider(provider); // 5.1/7.1: الكلام غالبًا في القناة اللي في النص، فناخد كل القنوات
        }

        if (provider.WaveFormat.SampleRate != AudioConverter.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, AudioConverter.SampleRate);
        }

        var samples = new List<float>(AudioConverter.SampleRate * 60);
        var buffer = new float[AudioConverter.SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }

        return samples.ToArray();
    }

    /// <summary>ملف WAV بـ16-bit PCM من جزء من العينات.</summary>
    public static byte[] ToWav16k(float[] samples, int offset = 0, int? count = null)
    {
        var length = count ?? samples.Length - offset;
        using var stream = new MemoryStream(44 + (length * 2));
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(stream), new WaveFormat(AudioConverter.SampleRate, 16, 1)))
        {
            var pcm = new byte[length * 2];
            for (var i = 0; i < length; i++)
            {
                var value = (short)Math.Clamp(samples[offset + i] * 32767f, short.MinValue, short.MaxValue);
                pcm[i * 2] = (byte)value;
                pcm[(i * 2) + 1] = (byte)(value >> 8);
            }

            writer.Write(pcm, 0, pcm.Length);
        }

        return stream.ToArray();
    }

    /// <summary>يقسم الصوت إلى أجزاء بطول تقريبي، ويقطع عند أهدأ نقطة قريبة حتى لا تنقسم كلمة.</summary>
    public static IReadOnlyList<(int Start, int Length)> SplitAtQuietPoints(float[] samples, int targetLength)
    {
        var parts = new List<(int, int)>();
        var window = AudioConverter.SampleRate / 10; // 100ms
        var search = AudioConverter.SampleRate * 5;  // ±5 ثوانٍ
        var start = 0;
        while (start < samples.Length)
        {
            var end = start + targetLength;
            if (end >= samples.Length - (AudioConverter.SampleRate * 2))
            {
                parts.Add((start, samples.Length - start));
                break;
            }

            var best = end;
            var bestEnergy = double.MaxValue;
            for (var candidate = Math.Max(start + window, end - search); candidate + window < Math.Min(samples.Length, end + search); candidate += window)
            {
                double energy = 0;
                for (var i = candidate; i < candidate + window; i++)
                {
                    energy += samples[i] * samples[i];
                }

                if (energy < bestEnergy)
                {
                    bestEnergy = energy;
                    best = candidate + (window / 2);
                }
            }

            parts.Add((start, best - start));
            start = best;
        }

        return parts;
    }
}

/// <summary>يجمع كل قنوات الصوت (5.1، 7.1…) في قناة واحدة بالمتوسط.</summary>
public sealed class DownmixToMonoProvider(ISampleProvider source) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private float[] _buffer = [];

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);

    public int Read(float[] buffer, int offset, int count)
    {
        var needed = count * _channels;
        if (_buffer.Length < needed)
        {
            _buffer = new float[needed];
        }

        var read = source.Read(_buffer, 0, needed);
        var frames = read / _channels;
        for (var f = 0; f < frames; f++)
        {
            float sum = 0;
            for (var c = 0; c < _channels; c++)
            {
                sum += _buffer[(f * _channels) + c];
            }

            buffer[offset + f] = sum / _channels;
        }

        return frames;
    }
}
