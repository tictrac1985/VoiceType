using VoiceType.Core.Speech;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;

namespace VoiceType.Providers;

public enum ConnectionStatus
{
    /// <summary>المفتاح يعمل.</summary>
    Ok,

    /// <summary>المفتاح مقبول لكن هناك ملاحظة (رصيد، حد استخدام، نموذج).</summary>
    Warning,

    /// <summary>المفتاح لا يعمل أو لا يوجد اتصال.</summary>
    Failed,
}

public sealed record ConnectionTestResult(ConnectionStatus Status, string Message, string? Details = null);

/// <summary>
/// اختبار مفتاح مزود بإرسال ثانية صامتة. الهدف معرفة هل المفتاح صحيح، وليس جودة التفريغ.
/// </summary>
public static class ConnectionTester
{
    public static async Task<ConnectionTestResult> TestAsync(
        ProviderDefinition definition,
        string? apiKey,
        HttpClient http,
        string? model = null,
        CancellationToken cancellationToken = default)
    {
        var engine = new DeclarativeSpeechEngine(
            definition,
            new ProviderInstance { Definition = definition.Id, ApiKey = apiKey, Model = model, TimeoutSeconds = 30 },
            http);

        try
        {
            await engine.TranscribeAsync(SampleAudio.Silence(), new TranscriptionOptions { Language = "ar" }, cancellationToken)
                .ConfigureAwait(false);
            return new ConnectionTestResult(ConnectionStatus.Ok, "✓ المفتاح يعمل");
        }
        catch (SpeechException ex)
        {
            return Interpret(ex);
        }
    }

    private static bool LooksLikeSilenceRejection(string details) =>
        details.Contains("audio", StringComparison.OrdinalIgnoreCase) &&
        (details.Contains("short", StringComparison.OrdinalIgnoreCase) || details.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
         details.Contains("silence", StringComparison.OrdinalIgnoreCase) || details.Contains("no speech", StringComparison.OrdinalIgnoreCase));

    private static string Short(string details)
    {
        var text = details.StartsWith("HTTP ", StringComparison.Ordinal) && details.IndexOf(':') is > 0 and var colon ? details[(colon + 1)..].Trim() : details;
        return text.Length <= 120 ? text : text[..120] + "…";
    }

    public static ConnectionTestResult Interpret(SpeechException ex) => ex.Kind switch
    {
        SpeechErrorKind.Unauthorized => new(ConnectionStatus.Failed, "✗ المفتاح غير صحيح. تأكد أنك نسخته كاملًا.", ex.TechnicalDetails),
        SpeechErrorKind.Forbidden => new(ConnectionStatus.Failed, "✗ المفتاح لا يملك صلاحية استخدام التفريغ.", ex.TechnicalDetails),
        SpeechErrorKind.Configuration => new(ConnectionStatus.Failed, "✗ لم يُدخل مفتاح.", ex.TechnicalDetails),
        SpeechErrorKind.Network => new(ConnectionStatus.Failed, "✗ لا يوجد اتصال بالإنترنت أو الخدمة محجوبة.", ex.TechnicalDetails),
        SpeechErrorKind.Timeout => new(ConnectionStatus.Failed, "✗ الخدمة لم ترد. جرّب مرة أخرى.", ex.TechnicalDetails),
        SpeechErrorKind.ServerError or SpeechErrorKind.CircuitOpen =>
            new(ConnectionStatus.Warning, "⚠ الخدمة مشغولة الآن. المفتاح محفوظ، جرّب الاختبار لاحقًا.", ex.TechnicalDetails),
        SpeechErrorKind.PaymentRequired => new(ConnectionStatus.Warning, "⚠ المفتاح صحيح، لكن الرصيد غير كافٍ لدى المزود.", ex.TechnicalDetails),
        SpeechErrorKind.RateLimited => new(ConnectionStatus.Warning, "⚠ المفتاح صحيح، لكنك تجاوزت الحد المسموح مؤقتًا.", ex.TechnicalDetails),
        SpeechErrorKind.NotFound => new(ConnectionStatus.Warning, "⚠ تم الاتصال، لكن النموذج الافتراضي غير متاح لحسابك.", ex.TechnicalDetails),

        // المفتاح قُبل، لكن الخدمة رفضت الطلب نفسه: نعرض سببها بدل "مقبول" حتى لا يظن المستخدم أن كل شيء يعمل
        SpeechErrorKind.BadRequest when ex.TechnicalDetails is { Length: > 0 } details && !LooksLikeSilenceRejection(details) =>
            new(ConnectionStatus.Warning, "⚠ المفتاح صحيح، لكن الخدمة ترفض الطلب الآن: " + Short(details), details),

        // الطلب وصل وتجاوز المصادقة، والرفض بسبب المقطع الصامت نفسه
        _ => new(ConnectionStatus.Ok, "✓ المفتاح مقبول", ex.TechnicalDetails),
    };
}
