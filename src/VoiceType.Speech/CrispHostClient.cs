using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;

namespace VoiceType.Speech;

/// <summary>
/// تشغيل البرنامج المساعد VoiceType.CrispHost (مخفي) والكلام معاه بسطور JSON.
/// بيستخدمه نموذج Cohere (تفريغ) ومعرفة المتكلمين.
/// لو المساعد وقع أو علّق: الطلب ياخد خطأ واضح، والمساعد يتقفل، ويتفتح تاني في الطلب الجاي.
/// </summary>
internal sealed class CrispHostClient : IDisposable
{
    private const string Marker = "@@VT ";

    private readonly string _engineId;
    private readonly string _hostPath;
    private readonly string _libraryDirectory;
    private readonly string? _modelPath;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly Queue<string> _stderrTail = new();
    private Process? _host;
    private TaskCompletionSource<JsonObject>? _ready;
    private int _nextId;

    public CrispHostClient(string engineId, string hostPath, string libraryDirectory, string? modelPath)
    {
        _engineId = engineId;
        _hostPath = hostPath;
        _libraryDirectory = libraryDirectory;
        _modelPath = modelPath;
    }

    /// <summary>أطول مدة نستناها لتحميل النموذج أول مرة (القرص البطيء + 1.5 جيجا).</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public string? Backend { get; private set; }

    public bool IsRunning => _host is { HasExited: false } && _ready is { Task.IsCompletedSuccessfully: true };

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        Stop();
        if (!File.Exists(_hostPath) || !File.Exists(Path.Combine(_libraryDirectory, OperatingSystem.IsWindows() ? "crispasr.dll" : "libcrispasr.so")))
        {
            throw new SpeechException(SpeechErrorKind.Configuration, _engineId, "محرك CrispASR مش متسطّب. نزّل النموذج تاني من الإعدادات وهو هينزل معاه.");
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
        // اللاب فيه Intel مدمج (0) و NVIDIA (1): من غير ده CrispASR بياخد الكرت الضعيف (أبطأ 4 مرات)
        if (VulkanDevices.PreferredIndex is { } gpu && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GGML_VK_VISIBLE_DEVICES")))
        {
            start.Environment["GGML_VK_VISIBLE_DEVICES"] = gpu.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        start.ArgumentList.Add("--lib");
        start.ArgumentList.Add(_libraryDirectory);
        if (_modelPath is not null)
        {
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(_modelPath);
        }

        var ready = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process host;
        try
        {
            host = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new SpeechException(SpeechErrorKind.Configuration, _engineId, "Could not start CrispASR host: " + ex.Message, innerException: ex);
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
            Backend = (string?)message["backend"];
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SpeechException)
        {
            Stop();
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw ex as SpeechException ?? new SpeechException(SpeechErrorKind.Timeout, _engineId, "Loading the model took too long.", innerException: ex);
        }
    }

    public async Task<JsonObject> RequestAsync(JsonObject request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var host = _host ?? throw new SpeechException(SpeechErrorKind.Unknown, _engineId, "CrispASR host is not running.");
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
                throw new SpeechException(SpeechErrorKind.Unknown, _engineId, "CrispASR: " + error);
            }

            return answer;
        }
        catch (TimeoutException ex)
        {
            Stop(); // مفيش طريقة نوقف شغل جوه المكتبة غير إننا نقفل المساعد
            throw new SpeechException(SpeechErrorKind.Timeout, _engineId, "Local processing took too long.", innerException: ex);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        catch (IOException ex)
        {
            Stop();
            throw new SpeechException(SpeechErrorKind.Unknown, _engineId, "CrispASR host stopped: " + LastError(), innerException: ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Stop()
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

    public void Dispose() => Stop();

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
                    ready.TrySetException(new SpeechException(SpeechErrorKind.Configuration, _engineId, (string?)message["fatal"]));
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
        var failure = new SpeechException(SpeechErrorKind.Unknown, _engineId, "CrispASR host exited: " + LastError());
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
}
