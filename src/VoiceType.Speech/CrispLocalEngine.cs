using System.Diagnostics;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;

namespace VoiceType.Speech;

/// <summary>
/// تفريغ محلي بنماذج CrispASR (مثل Cohere Transcribe Arabic، 2 مليار، Apache 2.0).
/// النموذج بيشتغل في برنامج مساعد منفصل (VoiceType.CrispHost) مخفي:
/// - مكتبته فيها ملفات بنفس أسماء ملفات Whisper، فلو اتحمّلت جوه صدى هتتعارض.
/// - لو وقعت أو علّقت، صدى يفضل شغال، والمساعد يتقفل ويتفتح تاني في المرة الجاية.
/// النموذج بيتحمّل مرة واحدة ويفضل في الذاكرة لحد ما صدى يفرّغه بعد الخمول.
/// النموذج بيرجّع وقت كل كلمة، فالأجزاء بتطلع جمل قصيرة (مناسبة للترجمة ومعرفة المتكلمين).
/// </summary>
public sealed class CrispLocalEngine : ILocalEngine
{
    private readonly string _modelPath;
    private readonly CrispHostClient _host;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CrispLocalEngine(string modelPath, string? id = null, string? displayName = null, string? hostPath = null, string? libraryDirectory = null)
    {
        _modelPath = modelPath;
        var fileName = Path.GetFileName(modelPath);
        Id = id ?? $"{LocalModels.ProviderId}:{fileName}";
        DisplayName = displayName ?? $"على جهازك · {LocalModels.DisplayNameOf(fileName)}";
        _host = new CrispHostClient(Id, hostPath ?? CrispRuntime.HostPath, libraryDirectory ?? CrispRuntime.Directory, modelPath);
    }

    /// <summary>عرض البحث (beam). 0 = الافتراضي بتاع النموذج. من الإعدادات المتقدمة.</summary>
    public static int BeamSize { get; set; }

    public string Id { get; }

    public string DisplayName { get; }

    public SpeechCapabilities Capabilities { get; } = new()
    {
        Formats = ["wav"],
        RequiresNetwork = false,
        SupportsPrompt = false,
    };

    /// <summary>اسم المحرك اللي اشتغل (cohere ...)، بعد أول تحميل.</summary>
    public string? LoadedBackend => _host.Backend;

    public async Task<TranscriptionResult> TranscribeAsync(AudioPayload audio, TranscriptionOptions options, CancellationToken cancellationToken = default)
    {
        if (!Capabilities.AcceptsFormat(audio.Format))
        {
            throw new SpeechException(SpeechErrorKind.UnsupportedFormat, Id, "Local engine accepts WAV audio only.");
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

        var stopwatch = Stopwatch.StartNew();
        var (text, _) = await RunAsync(samples, options, null, cancellationToken).ConfigureAwait(false);
        return new TranscriptionResult
        {
            Text = text,
            EngineId = Id,
            Model = Path.GetFileName(_modelPath),
            Elapsed = stopwatch.Elapsed,
            Cost = 0,
        };
    }

    public async Task<IReadOnlyList<TranscriptSegment>> TranscribeSegmentsAsync(
        float[] samples16kMono,
        TranscriptionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        (await RunAsync(samples16kMono, options, progress, cancellationToken).ConfigureAwait(false)).Segments;

    public async Task PreloadAsync()
    {
        if (!File.Exists(_modelPath))
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _host.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (SpeechException)
        {
            // التحميل المسبق اختياري: الخطأ هيظهر عند التفريغ نفسه
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool TryUnload()
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        try
        {
            _host.Stop();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            _host.Stop();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(string Text, IReadOnlyList<TranscriptSegment> Segments)> RunAsync(
        float[] samples, TranscriptionOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(_modelPath))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id, $"Model file not found: {_modelPath}");
        }

        // النموذج بيدعم العربي والإنجليزي بس
        var language = options.Language?.Trim().ToLowerInvariant() is "en" ? "en" : "ar";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temp = Path.Combine(Path.GetTempPath(), $"voicetype-crisp-{Guid.NewGuid():N}.f32");
        try
        {
            await _host.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            var texts = new List<string>();
            var result = new List<TranscriptSegment>();
            var chunks = AudioChunker.Split(samples);
            for (var c = 0; c < chunks.Count; c++)
            {
                var chunk = chunks[c];
                if (chunk.Length < AudioConverter.SampleRate / 5)
                {
                    continue; // أقل من 0.2 ثانية: مفيهوش كلام
                }

                var bytes = new byte[chunk.Length * sizeof(float)];
                Buffer.BlockCopy(samples, chunk.Start * sizeof(float), bytes, 0, bytes.Length);
                await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);

                var seconds = chunk.Length / (double)AudioConverter.SampleRate;
                var request = new JsonObject { ["pcm"] = temp, ["lang"] = language };
                if (BeamSize > 0)
                {
                    request["beam"] = BeamSize;
                }

                var reply = await _host.RequestAsync(request, TimeSpan.FromSeconds(Math.Max(60, seconds * 8)), cancellationToken).ConfigureAwait(false);
                var offset = TimeSpan.FromSeconds(chunk.Start / (double)AudioConverter.SampleRate);
                var chunkEnd = offset + TimeSpan.FromSeconds(seconds);
                foreach (var node in reply["segments"]?.AsArray() ?? [])
                {
                    var text = ((string?)node?["text"])?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    texts.Add(text);
                    var words = (node!["words"]?.AsArray() ?? [])
                        .Select(w => new WordGrouping.Word(
                            offset + Centiseconds((long?)w?["t0"]),
                            offset + Centiseconds((long?)w?["t1"]),
                            ((string?)w?["text"])?.Trim() ?? string.Empty))
                        .Where(w => w.Text.Length > 0)
                        .ToList();
                    if (words.Count > 0)
                    {
                        result.AddRange(WordGrouping.Group(WordGrouping.WithPunctuation(words, text)));
                        continue;
                    }

                    var t0 = (long?)node["t0"] ?? -1;
                    var t1 = (long?)node["t1"] ?? -1;
                    var start = t0 >= 0 ? offset + Centiseconds(t0) : offset;
                    var end = t1 >= t0 && t1 >= 0 ? offset + Centiseconds(t1) : chunkEnd;
                    result.Add(new TranscriptSegment(start, end > chunkEnd ? chunkEnd : end, text));
                }

                progress?.Report((c + 1) / (double)chunks.Count);
            }

            return (string.Join(" ", texts).Trim(), result);
        }
        finally
        {
            TryDelete(temp);
            _gate.Release();
        }
    }

    private static TimeSpan Centiseconds(long? value) => TimeSpan.FromMilliseconds(Math.Max(0, value ?? 0) * 10);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
