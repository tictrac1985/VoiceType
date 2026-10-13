using VoiceType.Core.Speech;

namespace VoiceType.Providers.Resilience;

/// <summary>
/// سلسلة بدائل: يجرب المحركات بالترتيب حتى ينجح أحدها.
/// المستخدم هو من يحدد السلسلة، ولا يُضاف إليها مزود سحابي بدون موافقته.
/// </summary>
public sealed class FallbackSpeechEngine : ISpeechEngine
{
    private readonly IReadOnlyList<ISpeechEngine> _engines;
    private readonly EngineHealth? _health;

    /// <param name="health">
    /// اختياري: يتذكر الخدمات المعطلة ويتخطاها لدقائق. يُستخدم فقط عندما يوجد بديل مضمون بعد السلسلة (النموذج المحلي).
    /// </param>
    public FallbackSpeechEngine(IEnumerable<ISpeechEngine> engines, string id = "fallback", EngineHealth? health = null)
    {
        _health = health;
        _engines = engines?.ToList() ?? throw new ArgumentNullException(nameof(engines));
        if (_engines.Count == 0)
        {
            throw new ArgumentException("At least one engine is required.", nameof(engines));
        }

        Id = id;
    }

    public string Id { get; }

    public string DisplayName => string.Join(" ← ", _engines.Select(e => e.DisplayName));

    public IReadOnlyList<ISpeechEngine> Engines => _engines;

    private readonly object _failuresLock = new();
    private List<EngineFailure> _current = [];

    /// <summary>المحرك الذي يعمل الآن في التشغيل الجاري.</summary>
    public string? CurrentEngineId { get; private set; }

    /// <summary>الأخطاء حتى الآن في التشغيل الجاري (لتظهر للمستخدم حتى لو أُلغيت السلسلة لأن المحلي سبقها).</summary>
    public IReadOnlyList<EngineFailure> FailuresSoFar
    {
        get
        {
            lock (_failuresLock)
            {
                return _current.ToList();
            }
        }
    }

    /// <summary>القدرات الأوسع: يكفي أن يقبل محرك واحد الصيغة.</summary>
    public SpeechCapabilities Capabilities => new()
    {
        Formats = _engines.Any(e => e.Capabilities.Formats is null or { Count: 0 })
            ? null
            : _engines.SelectMany(e => e.Capabilities.Formats!).Distinct().ToList(),
        LanguageRequired = _engines.All(e => e.Capabilities.LanguageRequired),
        SupportsPrompt = _engines.Any(e => e.Capabilities.SupportsPrompt),
        RequiresNetwork = _engines.All(e => e.Capabilities.RequiresNetwork),
    };

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioPayload audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        var failures = new List<EngineFailure>();
        lock (_failuresLock)
        {
            _current = failures;
        }

        foreach (var engine in _engines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!engine.Capabilities.AcceptsFormat(audio.Format))
            {
                Record(failures, new EngineFailure(engine.Id, SpeechErrorKind.UnsupportedFormat,
                    SpeechException.GetUserMessage(SpeechErrorKind.UnsupportedFormat)));
                continue;
            }

            if (_health?.SkippedUntil(engine.Id) is { } until)
            {
                Record(failures, new EngineFailure(engine.Id, SpeechErrorKind.CircuitOpen,
                    "تعطلت هذه الخدمة قبل قليل، فتم تخطيها مؤقتًا.",
                    $"Skipped until {until.ToLocalTime():HH:mm} after a recent failure."));
                continue;
            }

            CurrentEngineId = engine.Id;
            try
            {
                var result = await engine.TranscribeAsync(audio, options, cancellationToken).ConfigureAwait(false);
                _health?.Success(engine.Id);
                return result with { PreviousFailures = [.. failures, .. result.PreviousFailures] };
            }
            catch (SpeechException ex)
            {
                if (ex is AllEnginesFailedException nested)
                {
                    RecordAll(failures, nested.Failures);
                }
                else
                {
                    Record(failures, new EngineFailure(engine.Id, ex.Kind, ex.UserMessage, ex.TechnicalDetails));
                    _health?.Failure(engine.Id, ex.Kind);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // أي خطأ غير متوقع في محرك لا يمنع تجربة البديل
                Record(failures, new EngineFailure(engine.Id, SpeechErrorKind.Unknown,
                    SpeechException.GetUserMessage(SpeechErrorKind.Unknown), ex.GetType().Name + ": " + ex.Message));
            }
        }

        throw new AllEnginesFailedException(failures);
    }

    private void Record(List<EngineFailure> failures, EngineFailure failure)
    {
        lock (_failuresLock)
        {
            failures.Add(failure);
        }
    }

    private void RecordAll(List<EngineFailure> failures, IEnumerable<EngineFailure> items)
    {
        lock (_failuresLock)
        {
            failures.AddRange(items);
        }
    }
}
