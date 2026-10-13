using VoiceType.Core.Speech;

namespace VoiceType.Providers.Resilience;

public sealed record ResilienceOptions
{
    /// <summary>عدد المحاولات الكلي (1 = بدون إعادة).</summary>
    public int MaxAttempts { get; init; } = 3;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>أقصى انتظار نقبله من Retry-After. إذا طلب المزود أكثر ننتقل للبديل.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>الانتظار عند تجاوز حد الطلبات إذا لم يحدد المزود المدة (يتضاعف مع كل محاولة).</summary>
    public TimeSpan RateLimitDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>عدد الأخطاء المتتالية قبل إيقاف المزود مؤقتًا.</summary>
    public int CircuitBreakerThreshold { get; init; } = 3;

    public TimeSpan CircuitBreakDuration { get; init; } = TimeSpan.FromMinutes(1);

    public static ResilienceOptions NoRetry { get; } = new() { MaxAttempts = 1 };
}

/// <summary>
/// يغلف أي محرك بإعادة المحاولة للأخطاء المؤقتة وبـCircuit breaker.
/// لا يعيد المحاولة عند أخطاء المفتاح أو الطلب، لأن تكرارها لن يغير النتيجة.
/// </summary>
public sealed class ResilientSpeechEngine : ISpeechEngine
{
    private readonly ISpeechEngine _inner;
    private readonly ResilienceOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;

    public ResilientSpeechEngine(
        ISpeechEngine inner,
        ResilienceOptions? options = null,
        TimeProvider? time = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? new ResilienceOptions();
        _time = time ?? TimeProvider.System;
        _delay = delay ?? ((span, ct) => Task.Delay(span, _time, ct));
    }

    public string Id => _inner.Id;

    public string DisplayName => _inner.DisplayName;

    public SpeechCapabilities Capabilities => _inner.Capabilities;

    public ISpeechEngine Inner => _inner;

    public bool IsCircuitOpen
    {
        get
        {
            lock (_gate)
            {
                return _openUntil is { } until && _time.GetUtcNow() < until;
            }
        }
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioPayload audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (IsCircuitOpen)
        {
            throw new SpeechException(SpeechErrorKind.CircuitOpen, Id, "Circuit is open.");
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await _inner.TranscribeAsync(audio, options, cancellationToken).ConfigureAwait(false);
                RecordSuccess();
                return result;
            }
            catch (SpeechException ex) when (ex.IsTransient)
            {
                RecordFailure();

                var wait = GetDelay(attempt, ex);
                if (attempt >= _options.MaxAttempts || wait is null || IsCircuitOpen)
                {
                    throw;
                }

                await _delay(wait.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan? GetDelay(int attempt, SpeechException ex)
    {
        if (ex.RetryAfter is { } requested)
        {
            return requested <= _options.MaxRetryAfter ? requested : null;
        }

        if (ex.Kind == SpeechErrorKind.RateLimited)
        {
            var wait = TimeSpan.FromMilliseconds(_options.RateLimitDelay.TotalMilliseconds * attempt);
            return wait <= _options.MaxRetryAfter ? wait : _options.MaxRetryAfter;
        }

        var exponential = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var jitter = Random.Shared.NextDouble() * _options.BaseDelay.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Min(exponential + jitter, _options.MaxDelay.TotalMilliseconds));
    }

    private void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openUntil = null;
        }
    }

    private void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.CircuitBreakerThreshold)
            {
                _openUntil = _time.GetUtcNow() + _options.CircuitBreakDuration;
                _consecutiveFailures = 0;
            }
        }
    }
}
