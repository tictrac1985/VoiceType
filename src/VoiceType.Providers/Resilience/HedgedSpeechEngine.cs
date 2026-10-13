using VoiceType.Core.Speech;

namespace VoiceType.Providers.Resilience;

/// <summary>
/// سباق بين خدمة الإنترنت والنموذج المحلي حتى لا ينتظر المستخدم طويلًا:
/// - تبدأ خدمة الإنترنت أولًا (أدق عادة).
/// - إذا فشلت ← يبدأ المحلي فورًا.
/// - إذا تأخرت أكثر من <c>hedgeAfter</c> ← يبدأ المحلي بالتوازي، وأيهما ينتهي أولًا يُكتب نصه ويُلغى الآخر.
/// بهذا لا يتجاوز الانتظار تقريبًا (مدة التأخير + زمن النموذج المحلي) مهما تعطلت الخدمة.
/// </summary>
public sealed class HedgedSpeechEngine : ISpeechEngine
{
    private readonly ISpeechEngine _preferred;
    private readonly ISpeechEngine _backup;
    private readonly TimeSpan _hedgeAfter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public HedgedSpeechEngine(
        ISpeechEngine preferred,
        ISpeechEngine backup,
        TimeSpan hedgeAfter,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _preferred = preferred ?? throw new ArgumentNullException(nameof(preferred));
        _backup = backup ?? throw new ArgumentNullException(nameof(backup));
        _hedgeAfter = hedgeAfter < TimeSpan.Zero ? TimeSpan.Zero : hedgeAfter;
        _delay = delay ?? Task.Delay;
    }

    public string Id => "hedged";

    public string DisplayName => $"{_preferred.DisplayName} ⇄ {_backup.DisplayName}";

    public SpeechCapabilities Capabilities => _backup.Capabilities;

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioPayload audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default)
    {
        using var preferredCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var backupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var preferredTask = _preferred.Capabilities.AcceptsFormat(audio.Format)
            ? Run(_preferred, audio, options, preferredCts.Token)
            : Task.FromResult(Outcome.Failed([new EngineFailure(_preferred.Id, SpeechErrorKind.UnsupportedFormat,
                SpeechException.GetUserMessage(SpeechErrorKind.UnsupportedFormat))]));

        var timer = _delay(_hedgeAfter, timerCts.Token);
        var first = await Task.WhenAny(preferredTask, timer).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (first == preferredTask && preferredTask.Result.Result is { } quick)
        {
            timerCts.Cancel();
            return quick;
        }

        // فشلت الخدمة بسرعة، أو تأخرت: نشغّل المحلي (والخدمة ما زالت تعمل إن لم تفشل)
        timerCts.Cancel();
        var backupTask = Run(_backup, audio, options, backupCts.Token);
        var pending = new List<Task<Outcome>> { preferredTask, backupTask };
        var failures = new List<EngineFailure>();

        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await done.ConfigureAwait(false);
            if (outcome.Result is { } result)
            {
                if (done == backupTask && pending.Count > 0)
                {
                    // المحلي سبق خدمة الإنترنت: نلغيها ونسجل السبب (مع ما فشل فيها قبل الإلغاء)
                    preferredCts.Cancel();
                    var slowId = _preferred.Id;
                    if (_preferred is FallbackSpeechEngine chain)
                    {
                        failures.InsertRange(0, chain.FailuresSoFar);
                        slowId = chain.CurrentEngineId ?? slowId;
                    }

                    failures.Add(new EngineFailure(slowId, SpeechErrorKind.Timeout,
                        "تأخرت خدمة الإنترنت، فكُتب النص بالنموذج المحلي الأسرع.",
                        $"No answer within {_hedgeAfter.TotalSeconds:0.#}s plus local transcription time."));
                }
                else if (pending.Count > 0)
                {
                    backupCts.Cancel();
                }

                // ترتيب الأخطاء: أخطاء خدمة الإنترنت أولًا
                var merged = done == preferredTask
                    ? failures.Concat(result.PreviousFailures)
                    : (preferredTask.IsCompletedSuccessfully ? preferredTask.Result.Failures : []).Concat(failures).Concat(result.PreviousFailures);
                return result with { PreviousFailures = merged.Distinct().ToList() };
            }

            if (done == preferredTask)
            {
                failures.InsertRange(0, outcome.Failures);
            }
            else
            {
                failures.AddRange(outcome.Failures);
            }
        }

        throw new AllEnginesFailedException(failures);
    }

    private static async Task<Outcome> Run(ISpeechEngine engine, AudioPayload audio, TranscriptionOptions options, CancellationToken token)
    {
        try
        {
            return Outcome.Succeeded(await engine.TranscribeAsync(audio, options, token).ConfigureAwait(false));
        }
        catch (AllEnginesFailedException ex)
        {
            return Outcome.Failed(ex.Failures);
        }
        catch (SpeechException ex)
        {
            return Outcome.Failed([new EngineFailure(engine.Id, ex.Kind, ex.UserMessage, ex.TechnicalDetails)]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Outcome.Failed([]);
        }
        catch (Exception ex)
        {
            return Outcome.Failed([new EngineFailure(engine.Id, SpeechErrorKind.Unknown,
                SpeechException.GetUserMessage(SpeechErrorKind.Unknown), ex.GetType().Name + ": " + ex.Message)]);
        }
    }

    private sealed record Outcome(TranscriptionResult? Result, IReadOnlyList<EngineFailure> Failures)
    {
        public static Outcome Succeeded(TranscriptionResult result) => new(result, []);

        public static Outcome Failed(IReadOnlyList<EngineFailure> failures) => new(null, failures);
    }
}
