using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace VoiceType.Providers.Live;

/// <summary>إعدادات جلسة التفريغ المباشر.</summary>
public sealed record LiveOptions
{
    public const string DefaultModel = "gemini-3.5-transcribe-live";

    public string Model { get; init; } = DefaultModel;

    /// <summary>مثل ar-EG. فارغة = اكتشاف تلقائي.</summary>
    public IReadOnlyList<string> LanguageCodes { get; init; } = [];

    /// <summary>كلمات يجب أن تُكتب صحيحة (مثل Python وGmail).</summary>
    public IReadOnlyList<string> CustomVocabulary { get; init; } = [];

    /// <summary>SMART: ترقيم وتنسيق وحذف التلعثم. VERBATIM: حرفيًا.</summary>
    public string? Mode { get; init; } = "SMART";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(6);
}

/// <summary>اتصال WebSocket مبسّط (لتسهيل الاختبار).</summary>
public interface ILiveSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendAsync(string text, CancellationToken cancellationToken);

    /// <summary>الرسالة التالية كاملة، أو null عند إغلاق الاتصال.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);

    /// <summary>سبب الإغلاق من الخادم إن وُجد (مثل مفتاح غير صالح).</summary>
    string? CloseReason { get; }

    Task CloseAsync();
}

/// <summary>
/// التفريغ المباشر أثناء الكلام عبر Gemini Live (gemini-3.5-transcribe-live):
/// يرسل الصوت أولًا بأول، ويستقبل نصًا مؤقتًا يتغير أثناء الكلام (Interim)،
/// ونصًا نهائيًا عند كل وقفة (Final) يُكتب فورًا في البرنامج.
/// </summary>
public sealed class LiveTranscriber : IAsyncDisposable
{
    private const int ChunkBytes = 3200; // 100 ملّي ثانية من 16kHz/16bit/mono

    private readonly IReadOnlyList<string> _keys;
    private LiveOptions _options;
    private bool _strippedRetry;
    private readonly Func<ILiveSocket> _socketFactory;
    private readonly Channel<byte[]> _audio = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource _setupComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ILiveSocket? _socket;
    private Task? _sendLoop;
    private Task? _receiveLoop;
    private long _bytesSent;
    private long _lastMessageTicks;
    private volatile string _interim = string.Empty;
    private int _finals;
    private volatile bool _terminated;
    private volatile bool _endSent;
    private volatile bool _disposed;

    public LiveTranscriber(IReadOnlyList<string> apiKeys, LiveOptions options, Func<ILiveSocket>? socketFactory = null)
    {
        _keys = apiKeys?.Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? throw new ArgumentNullException(nameof(apiKeys));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _socketFactory = socketFactory ?? (() => new WebSocketLiveSocket());
    }

    /// <summary>نص مؤقت للمقطع الحالي (يتغير أثناء الكلام).</summary>
    public event Action<string>? Interim;

    /// <summary>نص نهائي لمقطع انتهى (عند وقفة في الكلام).</summary>
    public event Action<string>? Final;

    /// <summary>انقطعت الجلسة أثناء الكلام.</summary>
    public event Action<string>? Failed;

    public bool IsReady { get; private set; }

    /// <summary>انقطعت الجلسة بعد أن بدأت (أخطاء الاتصال الأولى لا تُحسب، يُجرَّب مفتاح آخر).</summary>
    public bool HasFailed => _terminated;

    public string? Error { get; private set; }

    /// <summary>كم بايت صوت أُرسل حتى الآن.</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>كم بايت صوت كان قد أُرسل لحظة وصول آخر نص نهائي (لمعرفة الجزء الذي لم يُفرّغ بعد).</summary>
    public long BytesAtLastFinal { get; private set; }

    public int FinalCount => _finals;

    public static Uri EndpointFor(string apiKey) => new(
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent?key="
        + Uri.EscapeDataString(apiKey));

    public static string SetupMessage(LiveOptions options)
    {
        var transcription = new JsonObject
        {
            ["languageCodes"] = new JsonArray(options.LanguageCodes.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        };
        if (options.CustomVocabulary.Count > 0)
        {
            transcription["customVocabulary"] = new JsonArray(options.CustomVocabulary.Take(100).Select(c => (JsonNode?)JsonValue.Create(c)).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(options.Mode))
        {
            transcription["mode"] = options.Mode;
        }

        var setup = new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = "models/" + options.Model,
                ["generationConfig"] = new JsonObject { ["responseModalities"] = new JsonArray("TEXT") },
                ["inputAudioTranscription"] = transcription,
            },
        };
        return setup.ToJsonString();
    }

    /// <summary>يتصل ويرسل الإعداد. يجرب المفاتيح بالترتيب. يرمي خطأ إذا لم ينجح أي مفتاح.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_keys.Count == 0)
        {
            throw new InvalidOperationException("No Gemini key.");
        }

        var errors = new List<string>();
        foreach (var key in _keys)
        {
            if (_disposed)
            {
                throw new OperationCanceledException();
            }

            // حالة جديدة لكل محاولة
            _setupComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Error = null;
            var socket = _socketFactory();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
                timeout.CancelAfter(_options.ConnectTimeout);
                await socket.ConnectAsync(EndpointFor(key), timeout.Token).ConfigureAwait(false);
                await socket.SendAsync(SetupMessage(_options), timeout.Token).ConfigureAwait(false);
                _socket = socket;
                if (_disposed)
                {
                    // أُغلقت الجلسة أثناء الاتصال
                    _socket = null;
                    await SafeCloseAsync(socket).ConfigureAwait(false);
                    throw new OperationCanceledException();
                }

                var closed = _closed;
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket, closed));

                // جاهز عند وصول setupComplete، أو بعد لحظة إذا بقي الاتصال مفتوحًا
                // (الخادم يغلق الاتصال فورًا إذا رُفض المفتاح أو الإعداد)
                await Task.WhenAny(_setupComplete.Task, _closed.Task, Task.Delay(1500, timeout.Token)).ConfigureAwait(false);
                if (!_closed.Task.IsCompleted)
                {
                    IsReady = true;
                    Error = null;
                    _sendLoop = Task.Run(SendLoopAsync);
                    return;
                }

                errors.Add(Error ?? socket.CloseReason ?? "closed");
            }
            catch (Exception ex) when (!_disposed && ex is WebSocketException or OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                errors.Add(ex.Message);
            }

            // هذا المفتاح لم ينجح: نغلق ونجرب التالي
            _socket = null;
            await SafeCloseAsync(socket).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // إذا رفض الخادم حقلًا اختياريًا (مثل mode أو customVocabulary) نجرب مرة بالإعداد الأساسي فقط
        if (!_strippedRetry && (_options.Mode is not null || _options.CustomVocabulary.Count > 0) &&
            errors.Any(e => e.Contains("nvalid", StringComparison.Ordinal) || e.Contains("Unknown name", StringComparison.Ordinal)))
        {
            _strippedRetry = true;
            _options = _options with { Mode = null, CustomVocabulary = [] };
            await StartAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        Error = string.Join(" | ", errors.Distinct());
        throw new InvalidOperationException("Live transcription unavailable: " + Error);
    }

    /// <summary>يضيف صوت PCM (16kHz/16bit/mono). آمن من أي خيط، ويُخزَّن إلى أن يجهز الاتصال.</summary>
    public void SendAudio(byte[] buffer, int count)
    {
        if (count <= 0 || _terminated || _disposed)
        {
            return;
        }

        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        _audio.Writer.TryWrite(copy);
    }

    /// <summary>
    /// نهاية الكلام: يرسل ما تبقى من الصوت، ثم ينتظر آخر نص نهائي (بحد أقصى <paramref name="maxWait"/>).
    /// يعيد false إذا انقطعت الجلسة.
    /// </summary>
    public async Task<bool> FinishAsync(TimeSpan maxWait)
    {
        _audio.Writer.TryComplete();
        if (!IsReady || _sendLoop is null || _terminated)
        {
            return false;
        }

        var deadline = DateTime.UtcNow + maxWait;
        await Task.WhenAny(_sendLoop, Task.Delay(maxWait)).ConfigureAwait(false);
        if (!_sendLoop.IsCompleted)
        {
            return false; // الشبكة متعطلة: آخر الكلام لم يُرسل
        }

        var endSent = DateTime.UtcNow;
        var finalsAtEnd = _finals;

        // ننتظر النص النهائي للمقطع الأخير: لا نص مؤقت معلّق، ومرت لحظة هدوء بعد آخر رسالة
        while (!_closed.Task.IsCompleted && !_terminated)
        {
            var now = DateTime.UtcNow;
            if (now >= deadline)
            {
                return false;
            }

            var sinceLast = now - new DateTime(Interlocked.Read(ref _lastMessageTicks), DateTimeKind.Utc);
            var sinceEnd = now - endSent;
            var gotFinalAfterEnd = _finals > finalsAtEnd;
            if (_interim.Length == 0 && sinceLast > TimeSpan.FromMilliseconds(gotFinalAfterEnd ? 350 : 600) && sinceEnd > TimeSpan.FromMilliseconds(gotFinalAfterEnd ? 350 : 1000))
            {
                break;
            }

            await Task.Delay(60).ConfigureAwait(false);
        }

        return !_terminated;
    }

    /// <summary>النص المؤقت المعلّق لحظة الإيقاف (إن لم يصل نص نهائي له).</summary>
    public string PendingInterim => _interim;

    private async Task SendLoopAsync()
    {
        var socket = _socket;
        var pending = new MemoryStream();
        try
        {
            await foreach (var chunk in _audio.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                pending.Write(chunk);
                if (pending.Length >= ChunkBytes && socket is not null)
                {
                    await SendChunkAsync(socket, pending).ConfigureAwait(false);
                }
            }

            if (socket is not null)
            {
                if (pending.Length > 0)
                {
                    await SendChunkAsync(socket, pending).ConfigureAwait(false);
                }

                _endSent = true;
                await socket.SendAsync("{\"realtimeInput\":{\"audioStreamEnd\":true}}", _cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            Fail("send: " + ex.Message);
        }
    }

    private async Task SendChunkAsync(ILiveSocket socket, MemoryStream pending)
    {
        var data = Convert.ToBase64String(pending.GetBuffer(), 0, (int)pending.Length);
        var message = "{\"realtimeInput\":{\"audio\":{\"data\":\"" + data + "\",\"mimeType\":\"audio/pcm;rate=16000\"}}}";
        await socket.SendAsync(message, _cts.Token).ConfigureAwait(false);
        Interlocked.Add(ref _bytesSent, pending.Length);
        pending.SetLength(0);
    }

    private async Task ReceiveLoopAsync(ILiveSocket socket, TaskCompletionSource closed)
    {
        try
        {
            while (true)
            {
                var message = await socket.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                if (message is null)
                {
                    // إغلاق طبيعي بعد نهاية الكلام ليس عطلًا
                    if (!_cts.IsCancellationRequested && !_endSent && ReferenceEquals(socket, _socket))
                    {
                        Fail(socket.CloseReason ?? "connection closed");
                    }

                    break;
                }

                Interlocked.Exchange(ref _lastMessageTicks, DateTime.UtcNow.Ticks);
                Handle(message);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or InvalidOperationException or ObjectDisposedException)
        {
            if (!_cts.IsCancellationRequested && !_endSent && ReferenceEquals(socket, _socket))
            {
                Fail("receive: " + ex.Message);
            }
        }
        finally
        {
            closed.TrySetResult();
        }
    }

    internal void Handle(string message)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(message);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is not JsonObject root)
        {
            return;
        }

        if (root.ContainsKey("setupComplete"))
        {
            _setupComplete.TrySetResult();
        }

        if (root["serverContent"] is not JsonObject content)
        {
            return;
        }

        if ((content["interimInputTranscription"] as JsonObject)?["text"] is JsonValue interimValue && interimValue.TryGetValue<string>(out var interim))
        {
            _interim = interim ?? string.Empty;
            Interim?.Invoke(_interim);
        }

        if ((content["inputTranscription"] as JsonObject)?["text"] is JsonValue finalValue && finalValue.TryGetValue<string>(out var final))
        {
            _interim = string.Empty;
            BytesAtLastFinal = BytesSent;
            Interlocked.Increment(ref _finals);
            if (!string.IsNullOrWhiteSpace(final))
            {
                Final?.Invoke(final);
            }
        }
    }

    private void Fail(string reason)
    {
        if (_terminated)
        {
            return;
        }

        Error ??= reason;
        _closed.TrySetResult();
        if (IsReady)
        {
            // بعد بدء الجلسة: انقطاع حقيقي (قبلها يُجرَّب المفتاح التالي)
            _terminated = true;
            Failed?.Invoke(reason);
        }
    }

    private static async Task SafeCloseAsync(ILiveSocket socket)
    {
        try
        {
            await socket.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or InvalidOperationException)
        {
        }

        await socket.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _audio.Writer.TryComplete();
        _cts.Cancel();
        if (_socket is { } socket)
        {
            _socket = null;
            await SafeCloseAsync(socket).ConfigureAwait(false);
        }
    }
}

/// <summary>اتصال WebSocket حقيقي بخوادم Google.</summary>
public sealed class WebSocketLiveSocket : ILiveSocket
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string? CloseReason => _socket.CloseStatusDescription is { Length: > 0 } d
        ? $"{(int?)_socket.CloseStatus} {d}"
        : _socket.CloseStatus is { } status ? status.ToString() : null;

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        return _socket.ConnectAsync(uri, cancellationToken);
    }

    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                // Gemini يرسل JSON أحيانًا كرسائل ثنائية: نقرأ الاثنين كنص UTF-8
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    public async Task CloseAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
