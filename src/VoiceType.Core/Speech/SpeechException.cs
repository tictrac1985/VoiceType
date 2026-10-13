using System.Net;

namespace VoiceType.Core.Speech;

public enum SpeechErrorKind
{
    Unknown,
    Network,
    Timeout,
    Unauthorized,
    Forbidden,
    PaymentRequired,
    RateLimited,
    NotFound,
    BadRequest,
    PayloadTooLarge,
    ServerError,
    UnsupportedFormat,
    InvalidResponse,
    Configuration,
    CircuitOpen,
    AllEnginesFailed,
}

/// <summary>
/// خطأ تفريغ برسالة مفهومة للمستخدم، وتفاصيل تقنية منفصلة للسجلات.
/// لا تحتوي أي رسالة على مفاتيح API أو محتوى صوتي.
/// </summary>
public class SpeechException : Exception
{
    public SpeechException(
        SpeechErrorKind kind,
        string engineId,
        string? technicalDetails = null,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(BuildMessage(kind, engineId, technicalDetails), innerException)
    {
        Kind = kind;
        EngineId = engineId;
        TechnicalDetails = technicalDetails;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public SpeechErrorKind Kind { get; }

    public string EngineId { get; }

    public string? TechnicalDetails { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    /// <summary>أخطاء مؤقتة يُعاد فيها المحاولة تلقائيًا.</summary>
    public bool IsTransient => Kind is SpeechErrorKind.Network
        or SpeechErrorKind.Timeout
        or SpeechErrorKind.RateLimited
        or SpeechErrorKind.ServerError;

    public string UserMessage => GetUserMessage(Kind);

    public static string GetUserMessage(SpeechErrorKind kind) => kind switch
    {
        SpeechErrorKind.Network => "تعذّر الاتصال بالخدمة. تأكد من اتصال الإنترنت.",
        SpeechErrorKind.Timeout => "الخدمة لم ترد في الوقت المحدد.",
        SpeechErrorKind.Unauthorized => "مفتاح API غير صحيح أو منتهي الصلاحية.",
        SpeechErrorKind.Forbidden => "لا تملك صلاحية استخدام هذا النموذج أو الخدمة.",
        SpeechErrorKind.PaymentRequired => "رصيد الحساب لدى المزود غير كافٍ.",
        SpeechErrorKind.RateLimited => "تجاوزت الحد المسموح لدى المزود. حاول بعد قليل.",
        SpeechErrorKind.NotFound => "النموذج أو العنوان غير موجود لدى المزود.",
        SpeechErrorKind.BadRequest => "المزود رفض الطلب. راجع إعدادات النموذج أو اللغة.",
        SpeechErrorKind.PayloadTooLarge => "التسجيل أكبر من الحد المسموح لدى المزود.",
        SpeechErrorKind.ServerError => "الخدمة غير متاحة مؤقتًا.",
        SpeechErrorKind.UnsupportedFormat => "صيغة الصوت غير مدعومة لدى هذا المزود.",
        SpeechErrorKind.InvalidResponse => "رد المزود غير مفهوم.",
        SpeechErrorKind.Configuration => "إعدادات المزود غير مكتملة.",
        SpeechErrorKind.CircuitOpen => "المزود متوقف مؤقتًا بعد عدة أخطاء متتالية.",
        SpeechErrorKind.AllEnginesFailed => "فشلت كل المزودات المتاحة. التسجيل محفوظ ويمكن إعادة المحاولة.",
        _ => "حدث خطأ غير متوقع أثناء التفريغ.",
    };

    private static string BuildMessage(SpeechErrorKind kind, string engineId, string? details) =>
        string.IsNullOrWhiteSpace(details)
            ? $"[{engineId}] {kind}"
            : $"[{engineId}] {kind}: {details}";
}

public sealed class AllEnginesFailedException(IReadOnlyList<EngineFailure> failures)
    : SpeechException(
        SpeechErrorKind.AllEnginesFailed,
        "fallback",
        string.Join(" | ", failures.Select(f => $"{f.EngineId}: {f.Kind}")))
{
    public IReadOnlyList<EngineFailure> Failures { get; } = failures;
}
