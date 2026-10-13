using System.Collections.Concurrent;
using VoiceType.Core.Speech;

namespace VoiceType.Providers.Resilience;

/// <summary>
/// يتذكر الخدمات المعطلة حاليًا (مثل نموذج يرد بخطأ 400 من جهة Google، أو خدمة لا ترد):
/// تُتخطى لبضع دقائق في الإملاء بدل أن يضيع وقت المستخدم عليها في كل مرة، ثم تُجرب من جديد تلقائيًا.
/// </summary>
public sealed class EngineHealth
{
    private readonly ConcurrentDictionary<string, (DateTimeOffset Until, int Strikes)> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time;

    public EngineHealth(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public static EngineHealth Shared { get; } = new();

    private DateTimeOffset Now => _time.GetUtcNow();

    public bool IsSkipped(string engineId) => _open.TryGetValue(engineId, out var state) && state.Until > Now;

    public DateTimeOffset? SkippedUntil(string engineId) =>
        _open.TryGetValue(engineId, out var state) && state.Until > Now ? state.Until : null;

    public void Success(string engineId) => _open.TryRemove(engineId, out _);

    /// <summary>يعيد كل نماذج الخدمة للعمل (بعد اختبار مفتاحها بنجاح مثلًا).</summary>
    public void ResetProvider(string providerId)
    {
        foreach (var id in _open.Keys.Where(k => k.StartsWith(providerId + ":", StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(k, providerId, StringComparison.OrdinalIgnoreCase)))
        {
            _open.TryRemove(id, out _);
        }
    }

    public void Failure(string engineId, SpeechErrorKind kind)
    {
        var baseDuration = PauseFor(kind);
        if (baseDuration is null)
        {
            return;
        }

        var strikes = _open.TryGetValue(engineId, out var previous) ? previous.Strikes + 1 : 1;
        // كل فشل متكرر يضاعف مدة التخطي (بحد أقصى 4 أضعاف)
        var duration = baseDuration.Value * Math.Min(4, 1 << Math.Min(2, strikes - 1));
        _open[engineId] = (Now + duration, strikes);
    }

    /// <summary>
    /// أخطاء لا تُحل بإعادة المحاولة فورًا. أخطاء المفاتيح (الحد المجاني، المفتاح المرفوض) لها نظامها الخاص لكل مفتاح.
    /// </summary>
    private static TimeSpan? PauseFor(SpeechErrorKind kind) => kind switch
    {
        SpeechErrorKind.BadRequest or SpeechErrorKind.NotFound => TimeSpan.FromMinutes(10),
        SpeechErrorKind.Timeout => TimeSpan.FromMinutes(5),
        // ردّ فارغ (مقطع قصير أو صامت) لا يعني أن الخدمة معطلة، فلا نتخطاها في المرات التالية
        SpeechErrorKind.ServerError => TimeSpan.FromMinutes(2),
        _ => null,
    };
}
