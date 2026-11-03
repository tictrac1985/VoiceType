using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;

namespace VoiceType.Speech;

/// <summary>
/// تفريغ محلي بنماذج CrispASR (مثل Cohere Transcribe Arabic، 2 مليار، Apache 2.0).
/// النموذج بيشتغل في برنامج مساعد منفصل (VoiceType.CrispHost) مخفي:
/// - مكتبته فيها ملفات بنفس أسماء ملفات Whisper، فلو اتحمّلت جوه صدى هتتعارض.
/// - لو وقعت أو علّقت، صدى يفضل شغال، والمساعد يتقفل ويتفتح تاني في المرة الجاية.
/// النموذج بيتحمّل مرة واحدة ويفضل في الذاكرة لحد ما صدى يفرّغه بعد الخمول.
/// </summary>
public sealed class CrispLocalEngine : ILocalEngine
{
    private const string Marker = "@@VT ";

    private readonly string _modelPath;
    private readonly string _hostPath;
    private readonly string _libraryDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly Queue<string> _stderrTail = new();
    private Process? _host;
    private TaskCompletionSource<JsonObject>? _ready;
    private int _nextId;

    public CrispLocalEngine(string modelPath, string? id = null, string? displayName = null, string? hostPath = null, string? libraryDirectory = null)
    {
        _modelPath = modelPath;
        _hostPath = hostPath ?? CrispRuntime.HostPath;
        _libraryDirectory = libraryDirectory ?? CrispRuntime.Directory;
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
        SupportsPrompt = false,
    };

    /// <summary>اسم المحرك اللي اشتغل (cohere ...) ووقت تحميل النموذج، بعد أول تحميل.</summary>
    public string? LoadedBackend { get; private set; }

    /// <summary>أطول مدة نستناها لتحميل النموذج أول مرة (القرص البطيء + 1.5 جيجا).</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(3);

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
        var segments = await RunAsync(samples, options, null, cancellationToken).ConfigureAwait(false);
        return new TranscriptionResult
        {
            Text = string.Join(" ", segments.Select(s => s.Text)).Trim(),
            EngineId = Id,
            Model = Path.GetFileName(_modelPath),
            Elapsed = stopwatch.Elapsed,
            Cost = 0,
        };
    }

    public Task<IReadOnlyList<TranscriptSegment>> TranscribeSegmentsAsync(
        float[] samples16kMono,
        TranscriptionOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(samples16kMono, options, progress, cancellationToken);

    public async Task PreloadAsync()
    {
        if (!File.Exists(_modelPath))
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureHostAsync(CancellationToken.None).ConfigureAwait(false);
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
            StopHost();
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
            StopHost();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<TranscriptSegment>> RunAsync(float[] samples, TranscriptionOptions options, IProgress<double>? progress, CancellationToken cancellationToken)
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
            await EnsureHostAsync(cancellationToken).ConfigureAwait(false);
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
                var reply = await RequestAsync(new JsonObject { ["pcm"] = temp, ["lang"] = language },
                    TimeSpan.FromSeconds(Math.Max(60, seconds * 8)), cancellationToken).ConfigureAwait(false);
                var offset = TimeSpan.FromSeconds(chunk.Start / (double)AudioConverter.SampleRate);
                var chunkEnd = offset + TimeSpan.FromSeconds(seconds);
                foreach (var node in reply["segments"]?.AsArray() ?? [])
                {
                    var text = ((string?)node?["text"])?.Trim();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    var t0 = (long?)node!["t0"] ?? -1;
                    var t1 = (long?)node["t1"] ?? -1;
                    var start = t0 >= 0 ? offset + TimeSpan.FromMilliseconds(t0 * 10) : offset;
                    var end = t1 >= t0 && t1 >= 0 ? offset + TimeSpan.FromMilliseconds(t1 * 10) : chunkEnd;
                    result.Add(new TranscriptSegment(start, end > chunkEnd ? chunkEnd : end, text));
                }

                progress?.Report((c + 1) / (double)chunks.Count);
            }

            return result;
        }
        finally
        {
            TryDelete(temp);
            _gate.Release();
        }
    }

    private async Task<JsonObject> RequestAsync(JsonObject request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var host = _host ?? throw new SpeechException(SpeechErrorKind.Unknown, Id, "CrispASR host is not running.");
        var id = Interlocked.Increment(ref _nextId);
        request["id"] = id;
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await host.StandardInput.WriteLineAsync(request.ToJsonString().AsMemory(), cancellationToken).ConfigureAwait(false);
            await host.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var answer = await reply.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if ((string?)answer["error"] is { } error)
            {
                throw new SpeechException(SpeechErrorKind.Unknown, Id, "Local transcription failed: " + error);
            }

            return answer;
        }
        catch (TimeoutException ex)
        {
            StopHost(); // مفيش طريقة نوقف تفريغ جوه المكتبة غير إننا نقفل المساعد
            throw new SpeechException(SpeechErrorKind.Timeout, Id, "Local transcription took too long.", innerException: ex);
        }
        catch (OperationCanceledException)
        {
            StopHost();
            throw;
        }
        catch (IOException ex)
        {
            StopHost();
            throw new SpeechException(SpeechErrorKind.Unknown, Id, "CrispASR host stopped: " + LastError(), innerException: ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task EnsureHostAsync(CancellationToken cancellationToken)
    {
        if (_host is { HasExited: false } && _ready is { Task.IsCompletedSuccessfully: true })
        {
            return;
        }

        StopHost();
        if (!File.Exists(_hostPath) || !File.Exists(Path.Combine(_libraryDirectory, OperatingSystem.IsWindows() ? "crispasr.dll" : "libcrispasr.so")))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id, "محرك CrispASR مش متسطّب. نزّل النموذج تاني من الإعدادات وهو هينزل معاه.");
        }

        var start = new ProcessStartInfo(_hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true, // من غير أي نافذة سودا
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = _libraryDirectory,
        };
        start.ArgumentList.Add("--lib");
        start.ArgumentList.Add(_libraryDirectory);
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(_modelPath);

        var ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process host;
        try
        {
            host = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SpeechException(SpeechErrorKind.Configuration, Id, "Could not start CrispASR host: " + ex.Message, innerException: ex);
        }

        try
        {
            host.PriorityClass = ProcessPriorityClass.AboveNormal; // الإملاء محتاج يرد بسرعة
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }

        _host = host;
        _ready = ready;
        lock (_stderrTail)
        {
            _stderrTail.Clear();
        }

        _ = Task.Run(() => ReadOutputAsync(host, ready));
        _ = Task.Run(() => ReadErrorsAsync(host));

        try
        {
            var message = await ready.Task.WaitAsync(LoadTimeout, cancellationToken).ConfigureAwait(false);
            LoadedBackend = (string?)message["backend"];
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SpeechException)
        {
            StopHost();
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw ex as SpeechException ?? new SpeechException(SpeechErrorKind.Timeout, Id, "Loading the model took too long.", innerException: ex);
        }
    }

    private async Task ReadOutputAsync(Process host, TaskCompletionSource<JsonObject> ready)
    {
        try
        {
            string? line;
            while ((line = await host.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                var index = line.IndexOf(Marker, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue; // كلام طبعته المكتبة نفسها
                }

                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line[(index + Marker.Length)..]) as JsonObject;
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                if (message["fatal"] is not null)
                {
                    ready.TrySetException(new SpeechException(SpeechErrorKind.Configuration, Id, (string?)message["fatal"]));
                }
                else if (message["ready"] is not null)
                {
                    ready.TrySetResult(message);
                }
                else if (message["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id) && _pending.TryGetValue(id, out var pending))
                {
                    pending.TrySetResult(message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        // المساعد قفل: أي حد مستني ياخد خطأ واضح
        var failure = new SpeechException(SpeechErrorKind.Unknown, Id, "CrispASR host exited: " + LastError());
        ready.TrySetException(failure);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(failure);
        }
    }

    private async Task ReadErrorsAsync(Process host)
    {
        try
        {
            string? line;
            while ((line = await host.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > 15)
                    {
                        _stderrTail.Dequeue();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private string LastError()
    {
        lock (_stderrTail)
        {
            return _stderrTail.Count == 0 ? "no details" : string.Join(" | ", _stderrTail.TakeLast(3));
        }
    }

    private void StopHost()
    {
        var host = _host;
        _host = null;
        _ready = null;
        if (host is null)
        {
            return;
        }

        try
        {
            if (!host.HasExited)
            {
                host.StandardInput.Close(); // المساعد بيقفل نفسه بهدوء لما stdin يتقفل
                if (!host.WaitForExit(3000))
                {
                    host.Kill(entireProcessTree: true);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
        }
        finally
        {
            host.Dispose();
        }
    }

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
