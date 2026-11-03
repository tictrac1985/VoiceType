using System.Diagnostics;
using System.Text;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using VoiceType.Core.Speech;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoiceType.Speech;

/// <summary>
/// تفريغ محلي على جهاز المستخدم باستخدام Whisper (whisper.cpp عبر Whisper.net).
/// مجاني، بدون إنترنت، والصوت لا يخرج من الجهاز.
/// يستخدم كرت الشاشة (Vulkan) إن كان التسريع مثبتًا، وإلا المعالج.
/// </summary>
public sealed class WhisperLocalEngine : ILocalEngine
{
    private static readonly object RuntimeGate = new();
    private static bool _runtimeConfigured;

    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WhisperFactory? _factory;

    public WhisperLocalEngine(string modelPath, string? id = null, string? displayName = null)
    {
        _modelPath = modelPath;
        var fileName = Path.GetFileName(modelPath);
        Id = id ?? $"{LocalModels.ProviderId}:{fileName}";
        DisplayName = displayName ?? $"على جهازك · {LocalModels.DisplayNameOf(fileName)}";
    }

    public string Id { get; }

    public string DisplayName { get; }

    public SpeechCapabilities Capabilities { get; } = new()
    {
        Formats = ["wav"],
        RequiresNetwork = false,
        SupportsPrompt = true,
    };

    /// <summary>عدد الاحتمالات التي يجربها النموذج لكل جملة في وضع الدقة (نفس إعداد Vibe الافتراضي).</summary>
    public const int AccurateBeamSize = 5;

    /// <summary>هل يعمل التفريغ على كرت الشاشة (Vulkan / CUDA)؟</summary>
    public static bool IsGpuRuntime => RuntimeOptions.LoadedLibrary is RuntimeLibrary.Vulkan or RuntimeLibrary.Cuda or RuntimeLibrary.Cuda12;

    /// <summary>
    /// البحث الأدق (beam search): يجرب عدة احتمالات للجملة ويختار الأرجح، فيقل الخلط في العامية المصرية والكلمات الإنجليزية.
    /// أبطأ من الطريقة العادية، لذلك يُستخدم للملفات دائمًا وللإملاء على كرت الشاشة فقط.
    /// </summary>
    internal static WhisperProcessorBuilder WithDecoding(WhisperProcessorBuilder builder, bool accurate) =>
        accurate ? builder.WithBeamSearchSamplingStrategy(beam => beam.WithBeamSize(AccurateBeamSize)) : builder;

    /// <summary>طبقة التشغيل التي حُمّلت فعلًا (Vulkan / Cpu ...)، بعد أول تفريغ.</summary>
    public static string? LoadedRuntime => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>ترتيب طبقات التشغيل: كرت الشاشة أولًا ثم المعالج. يُستدعى مرة واحدة قبل أول تحميل.</summary>
    public static void ConfigureRuntime()
    {
        lock (RuntimeGate)
        {
            if (_runtimeConfigured)
            {
                return;
            }

            RuntimeOptions.RuntimeLibraryOrder =
            [
                RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx,
            ];
            _runtimeConfigured = true;
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioPayload audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!Capabilities.AcceptsFormat(audio.Format))
        {
            throw new SpeechException(SpeechErrorKind.UnsupportedFormat, Id, "Local engine accepts WAV audio only.");
        }

        if (!File.Exists(_modelPath))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id, $"Model file not found: {_modelPath}");
        }

        float[] samples;
        try
        {
            samples = AudioConverter.ToMono16kSamples(audio.Data);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException or EndOfStreamException)
        {
            throw new SpeechException(SpeechErrorKind.UnsupportedFormat, Id, "Could not read WAV audio: " + ex.Message, innerException: ex);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var factory = await Task.Run(EnsureFactory, cancellationToken).ConfigureAwait(false);

            var builder = factory.CreateBuilder()
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 1, 16));
            // على كرت الشاشة نستخدم البحث الأدق (beam search) مثل برنامج Vibe؛ على المعالج نبقى على الأسرع
            builder = WithDecoding(builder, accurate: IsGpuRuntime);
            builder = string.IsNullOrWhiteSpace(options.Language)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(options.Language.Trim());
            if (!string.IsNullOrWhiteSpace(options.Prompt))
            {
                builder = builder.WithPrompt(options.Prompt);
            }

            var text = new StringBuilder();
            await using (var processor = builder.Build())
            {
                await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
                {
                    text.Append(segment.Text);
                }
            }

            stopwatch.Stop();
            return new TranscriptionResult
            {
                Text = text.ToString().Trim(),
                EngineId = Id,
                Model = Path.GetFileName(_modelPath),
                Elapsed = stopwatch.Elapsed,
                Cost = 0,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SpeechException)
        {
            throw new SpeechException(SpeechErrorKind.Unknown, Id, "Local transcription failed: " + ex.Message, innerException: ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>تفريغ صوت طويل مع التوقيتات (للملفات الصوتية وترجمات SRT).</summary>
    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeSegmentsAsync(
        float[] samples16kMono,
        TranscriptionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_modelPath))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id, $"Model file not found: {_modelPath}");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var factory = await Task.Run(EnsureFactory, cancellationToken).ConfigureAwait(false);
            var builder = factory.CreateBuilder()
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 2, 1, 16))
                .WithProgressHandler(percent => progress?.Report(percent / 100.0));
            // تفريغ الملفات: الدقة أهم من السرعة، فنستخدم دائمًا البحث الأدق
            builder = WithDecoding(builder, accurate: true);
            builder = string.IsNullOrWhiteSpace(options.Language)
                ? builder.WithLanguageDetection()
                : builder.WithLanguage(options.Language.Trim());
            if (!string.IsNullOrWhiteSpace(options.Prompt))
            {
                builder = builder.WithPrompt(options.Prompt);
            }

            var segments = new List<TranscriptSegment>();
            await using (var processor = builder.Build())
            {
                await foreach (var segment in processor.ProcessAsync(samples16kMono, cancellationToken).ConfigureAwait(false))
                {
                    if (!string.IsNullOrWhiteSpace(segment.Text))
                    {
                        segments.Add(new TranscriptSegment(segment.Start, segment.End, segment.Text.Trim()));
                    }
                }
            }

            progress?.Report(1);
            return segments;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not SpeechException)
        {
            throw new SpeechException(SpeechErrorKind.Unknown, Id, "Local transcription failed: " + ex.Message, innerException: ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>يحمّل النموذج في الذاكرة مسبقًا (مثلًا أثناء كلام المستخدم) حتى يبدأ التفريغ فورًا.</summary>
    public async Task PreloadAsync()
    {
        if (_factory is not null || !File.Exists(_modelPath))
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(EnsureFactory).ConfigureAwait(false);
        }
        catch (SpeechException)
        {
            // سيظهر الخطأ عند التفريغ نفسه
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>يفرغ النموذج من الذاكرة إن لم يكن يعمل الآن. يرجع false إن كان مشغولًا.</summary>
    public bool TryUnload()
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        try
        {
            _factory?.Dispose();
            _factory = null;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>ينتظر انتهاء أي تفريغ جارٍ ثم يفرغ النموذج (لا يُحرَّر النموذج أثناء استخدامه أبدًا).</summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private WhisperFactory EnsureFactory()
    {
        if (_factory is not null)
        {
            return _factory;
        }

        ConfigureRuntime();
        try
        {
            _factory = WhisperFactory.FromPath(_modelPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id,
                "Could not load the local model or the Whisper runtime files: " + ex.Message, innerException: ex);
        }

        return _factory;
    }
}

/// <summary>تحويل الصوت إلى الصيغة التي يحتاجها Whisper: 16kHz، قناة واحدة، أرقام عشرية.</summary>
public static class AudioConverter
{
    public const int SampleRate = 16000;

    public static float[] ToMono16kSamples(byte[] wavBytes)
    {
        using var stream = new MemoryStream(wavBytes);
        using var reader = new WaveFileReader(stream);
        ISampleProvider provider = reader.ToSampleProvider();

        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider) { LeftVolume = 0.5f, RightVolume = 0.5f };
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            throw new FormatException("Audio with more than two channels is not supported.");
        }

        if (provider.WaveFormat.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }

        var samples = new List<float>(capacity: (int)(reader.TotalTime.TotalSeconds * SampleRate) + SampleRate);
        var buffer = new float[SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }

        return samples.ToArray();
    }
}
