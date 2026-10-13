using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using VoiceType.Core.Speech;
using VoiceType.Providers.Engines;

namespace VoiceType.Providers.Resilience;

/// <summary>مفتاح واحد من مفاتيح الخدمة مع المحرك الذي يستخدمه.</summary>
public sealed record KeyedEngine(string KeyId, string Label, ISpeechEngine Engine);

/// <summary>حالة مفتاح: هل هو متوقف مؤقتًا ولماذا.</summary>
public sealed record KeyStatus(DateTimeOffset? PausedUntil, SpeechErrorKind? LastError, DateTimeOffset? LastSuccess);

/// <summary>
/// يتذكر حالة كل مفتاح طوال تشغيل البرنامج: المفتاح الذي تجاوز حده أو رُفض يُتخطى مباشرة
/// في المرات التالية بدل الانتظار عليه، والمفتاح الذي نجح آخر مرة يُجرب أولًا.
/// </summary>
public sealed class KeyHealth
{
    private readonly ConcurrentDictionary<string, KeyStatus> _status = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public KeyHealth(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public static KeyHealth Shared { get; } = new();

    public DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>بصمة قصيرة للمفتاح (لا يمكن استرجاع المفتاح منها).</summary>
    public static string Fingerprint(string providerId, string? apiKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey ?? string.Empty));
        return $"{providerId}:{Convert.ToHexString(hash, 0, 6)}";
    }

    public KeyStatus Get(string keyId) => _status.TryGetValue(keyId, out var status) ? status : new KeyStatus(null, null, null);

    public bool IsPaused(string keyId) => Get(keyId).PausedUntil is { } until && until > Now;

    public void Success(string keyId) => _status[keyId] = new KeyStatus(null, null, Now);

    public void Pause(string keyId, TimeSpan duration, SpeechErrorKind reason) =>
        _status[keyId] = Get(keyId) with { PausedUntil = Now + duration, LastError = reason };

    public void Failure(string keyId, SpeechErrorKind reason) =>
        _status[keyId] = Get(keyId) with { LastError = reason };

    /// <summary>يعيد المفتاح للخدمة (بعد اختباره بنجاح أو تعديله).</summary>
    public void Reset(string keyId) => _status.TryRemove(keyId, out _);
}

/// <summary>
/// خدمة واحدة بعدة مفاتيح (مثل عدة حسابات Gemini مجانية):
/// يجرب المفاتيح بالترتيب، وإذا تجاوز مفتاح حده أو رُفض أو طلب الدفع ينتقل للتالي فورًا.
/// إذا فشلت كلها يرمي الخطأ، فتنتقل سلسلة البدائل للنموذج المحلي.
/// </summary>
public sealed class KeyRotatingEngine : ISpeechEngine
{
    private readonly IReadOnlyList<KeyedEngine> _keys;
    private readonly KeyHealth _health;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public KeyRotatingEngine(
        IReadOnlyList<KeyedEngine> keys,
        KeyHealth? health = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (keys is null || keys.Count == 0)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }

        _keys = keys;
        _health = health ?? KeyHealth.Shared;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    public string Id => _keys[0].Engine.Id;

    public string DisplayName => _keys[0].Engine.DisplayName;

    public SpeechCapabilities Capabilities => _keys[0].Engine.Capabilities;

    public IReadOnlyList<KeyedEngine> Keys => _keys;

    /// <summary>أقصى انتظار مقبول لحد الطلبات في الدقيقة قبل تخطي المفتاح.</summary>
    public static TimeSpan MaxGateWait { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>مدة إيقاف المفتاح مؤقتًا حسب نوع الخطأ.</summary>
    public static TimeSpan PauseFor(SpeechException ex) => ex.Kind switch
    {
        SpeechErrorKind.RateLimited when ex.RetryAfter is { } after && after > TimeSpan.Zero =>
            after < TimeSpan.FromHours(6) ? after : TimeSpan.FromHours(6),
        SpeechErrorKind.RateLimited when LooksDaily(ex.TechnicalDetails) => TimeSpan.FromHours(1),
        SpeechErrorKind.RateLimited => TimeSpan.FromSeconds(65),
        SpeechErrorKind.PaymentRequired => TimeSpan.FromHours(1),
        SpeechErrorKind.Unauthorized or SpeechErrorKind.Forbidden => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromSeconds(30),
    };

    private static bool LooksDaily(string? details) =>
        details is not null &&
        (details.Contains("per day", StringComparison.OrdinalIgnoreCase) ||
         details.Contains("PerDay", StringComparison.OrdinalIgnoreCase) ||
         details.Contains("daily", StringComparison.OrdinalIgnoreCase));

    /// <summary>أخطاء تخص المفتاح نفسه: ننتقل للمفتاح التالي.</summary>
    private static bool IsKeyProblem(SpeechErrorKind kind) => kind is SpeechErrorKind.Unauthorized
        or SpeechErrorKind.Forbidden
        or SpeechErrorKind.PaymentRequired
        or SpeechErrorKind.RateLimited;

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioPayload audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<EngineFailure>();
        SpeechException? last = null;

        var ready = Order().Where(k => !_health.IsPaused(k.KeyId)).ToList();
        if (ready.Count == 0)
        {
            // كل المفاتيح متوقفة: نجرب المفتاح الأقرب لانتهاء حده (طلب مرفوض لا يكلف إلا جزءًا من الثانية)،
            // حتى لا يبقى المزود معطلًا لفترة طويلة بسبب تقدير خاطئ لمدة الإيقاف
            ready = Order()
                .Where(k => _health.Get(k.KeyId).LastError == SpeechErrorKind.RateLimited)
                .OrderBy(k => _health.Get(k.KeyId).PausedUntil ?? DateTimeOffset.MinValue)
                .Take(1)
                .ToList();
        }

        foreach (var key in ready)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // وصل هذا المفتاح لحد الطلبات في الدقيقة: لا ننتظره، ننتقل لمفتاح آخر أو للبديل
            if (key.Engine is DeclarativeSpeechEngine gated && gated.EstimatedGateWait > MaxGateWait)
            {
                last = new SpeechException(SpeechErrorKind.RateLimited, key.Engine.Id,
                    $"Per-minute limit reached; next request allowed in {gated.EstimatedGateWait.TotalSeconds:0}s.");
                failures.Add(new EngineFailure($"{key.Engine.Id} ({key.Label})", last.Kind, last.UserMessage, last.TechnicalDetails));
                continue;
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await key.Engine.TranscribeAsync(audio, options, cancellationToken).ConfigureAwait(false);
                    _health.Success(key.KeyId);
                    return failures.Count == 0 ? result : result with { PreviousFailures = [.. failures, .. result.PreviousFailures] };
                }
                catch (SpeechException ex)
                {
                    last = ex;
                    failures.Add(new EngineFailure($"{key.Engine.Id} ({key.Label})", ex.Kind, ex.UserMessage, ex.TechnicalDetails));

                    if (IsKeyProblem(ex.Kind))
                    {
                        _health.Pause(key.KeyId, PauseFor(ex), ex.Kind);
                        break; // المفتاح التالي فورًا بدون انتظار
                    }

                    _health.Failure(key.KeyId, ex.Kind);

                    // ضغط مؤقت على الخدمة: محاولة سريعة واحدة بنفس المفتاح، ثم المفتاح التالي
                    if (ex.Kind == SpeechErrorKind.ServerError && attempt == 1)
                    {
                        await _delay(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    if (ex.Kind is SpeechErrorKind.ServerError)
                    {
                        break;
                    }

                    // لا إنترنت، أو الخدمة لا ترد (Timeout)، أو مشكلة في الطلب نفسه: لن تختلف مع مفتاح آخر،
                    // فننتقل للبديل فورًا بدل انتظار كل مفتاح
                    throw WithFailures(ex, failures);
                }
            }
        }

        if (last is null)
        {
            // كل المفاتيح متوقفة مؤقتًا: لا ننتظر، ننتقل للبديل مباشرة
            var soonest = _keys.Select(k => _health.Get(k.KeyId).PausedUntil).Where(t => t is not null).Min();
            var minutes = soonest is { } t ? Math.Max(1, (int)Math.Ceiling((t - _health.Now).TotalMinutes)) : 1;
            throw new SpeechException(SpeechErrorKind.RateLimited, Id,
                $"All {_keys.Count} key(s) are paused (limit reached or rejected). Next try in ~{minutes} min.");
        }

        throw WithFailures(last, failures);
    }

    /// <summary>المفاتيح الجاهزة فورًا أولًا (بدون انتظار حد الطلبات في الدقيقة)، ثم الذي نجح آخر مرة، ثم البقية بالترتيب.</summary>
    private IEnumerable<KeyedEngine> Order() =>
        _keys
            .Select((key, index) => (key, index))
            .OrderBy(x => x.key.Engine is DeclarativeSpeechEngine d && d.EstimatedGateWait > TimeSpan.FromSeconds(1) ? 1 : 0)
            .ThenByDescending(x => _health.Get(x.key.KeyId).LastSuccess ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.index)
            .Select(x => x.key);

    private SpeechException WithFailures(SpeechException ex, List<EngineFailure> failures)
    {
        if (failures.Count <= 1)
        {
            return ex;
        }

        var details = string.Join(" | ", failures.Select(f => $"{f.EngineId}: {f.Kind} {f.Details}"));
        return new SpeechException(ex.Kind, Id, details, ex.StatusCode, ex.RetryAfter, ex);
    }
}
