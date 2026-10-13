using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;
using VoiceType.Providers.Templating;

namespace VoiceType.Providers.Engines;

/// <summary>
/// يحوّل رد HTTP الفاشل إلى خطأ مفهوم، ويستخرج رسالة المزود للتفاصيل التقنية.
/// </summary>
internal static class ErrorMapper
{
    private const int MaxDetailsLength = 300;

    private static readonly string[] CommonErrorPaths =
    [
        "error.message", "error", "message", "detail.message", "detail", "err_msg", "errors[0].message",
    ];

    public static SpeechException FromResponse(string engineId, HttpResponseMessage response, string body, string? errorPath)
    {
        var status = response.StatusCode;
        var kind = MapStatus(status);
        var details = ExtractProviderMessage(body, errorPath);

        // بعض المزودات تعيد 400 عند رفض صيغة الصوت
        if (kind == SpeechErrorKind.BadRequest && details is not null &&
            (details.Contains("format", StringComparison.OrdinalIgnoreCase) ||
             details.Contains("codec", StringComparison.OrdinalIgnoreCase)))
        {
            kind = SpeechErrorKind.UnsupportedFormat;
        }

        kind = RefineByMessage(kind, details);

        return new SpeechException(
            kind,
            engineId,
            $"HTTP {(int)status}" + (details is null ? string.Empty : $": {details}"),
            status,
            GetRetryAfter(response) ?? RetryDelayFromBody(body));
    }

    /// <summary>
    /// بعض المزودات (مثل Gemini) تعيد 400 لمفتاح غير صالح، و429/403 لنفاد الرصيد،
    /// فنصحح نوع الخطأ من نص الرسالة حتى يُجرب المفتاح التالي مباشرة.
    /// </summary>
    internal static SpeechErrorKind RefineByMessage(SpeechErrorKind kind, string? details)
    {
        if (details is null)
        {
            return kind;
        }

        bool Has(string text) => details.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (kind is SpeechErrorKind.BadRequest or SpeechErrorKind.Forbidden or SpeechErrorKind.Unknown &&
            (Has("api key not valid") || Has("api_key_invalid") || Has("invalid api key") || Has("api key expired") ||
             Has("incorrect api key") || Has("invalid_api_key")))
        {
            return SpeechErrorKind.Unauthorized;
        }

        // ملاحظة: رسالة 429 العادية في Gemini تذكر "billing" رغم أنها مجرد حد مؤقت، لذلك لا نغيّر 429
        if (kind is SpeechErrorKind.Forbidden or SpeechErrorKind.BadRequest or SpeechErrorKind.RateLimited &&
            (Has("insufficient credit") || Has("insufficient_quota") || Has("insufficient balance") ||
             Has("payment required") || Has("add credits") || Has("credit balance")))
        {
            return SpeechErrorKind.PaymentRequired;
        }

        if (kind is SpeechErrorKind.Forbidden or SpeechErrorKind.BadRequest &&
            (Has("billing") || Has("insufficient credit") || Has("insufficient_quota") || Has("insufficient balance") ||
             Has("payment required") || Has("add credits") || Has("credit balance")))
        {
            return SpeechErrorKind.PaymentRequired;
        }

        if (kind is SpeechErrorKind.Forbidden or SpeechErrorKind.BadRequest &&
            (Has("resource_exhausted") || Has("quota exceeded") || Has("rate limit")))
        {
            return SpeechErrorKind.RateLimited;
        }

        return kind;
    }

    internal static SpeechErrorKind MapStatus(HttpStatusCode status) => (int)status switch
    {
        400 or 422 => SpeechErrorKind.BadRequest,
        401 => SpeechErrorKind.Unauthorized,
        402 => SpeechErrorKind.PaymentRequired,
        403 => SpeechErrorKind.Forbidden,
        404 => SpeechErrorKind.NotFound,
        408 => SpeechErrorKind.Timeout,
        413 => SpeechErrorKind.PayloadTooLarge,
        415 => SpeechErrorKind.UnsupportedFormat,
        429 => SpeechErrorKind.RateLimited,
        >= 500 => SpeechErrorKind.ServerError,
        _ => SpeechErrorKind.Unknown,
    };

    /// <summary>Gemini يضع مدة الانتظار داخل الرد: "retryDelay": "41s".</summary>
    internal static TimeSpan? RetryDelayFromBody(string body)
    {
        var match = System.Text.RegularExpressions.Regex.Match(body ?? string.Empty, "\"retryDelay\"\\s*:\\s*\"(\\d+(?:\\.\\d+)?)s\"");
        return match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static string? ExtractProviderMessage(string body, string? errorPath)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        JsonNode? root = null;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            // ليس JSON: نأخذ بداية النص فقط
        }

        if (root is null)
        {
            return Truncate(body);
        }

        var paths = errorPath is { Length: > 0 } ? CommonErrorPaths.Prepend(errorPath) : CommonErrorPaths;
        foreach (var path in paths)
        {
            var node = JsonPathLite.Select(root, path);
            if (node is JsonValue value && value.TryGetValue<string>(out var message) && !string.IsNullOrWhiteSpace(message))
            {
                return Truncate(message);
            }
        }

        return Truncate(body);
    }

    private static string Truncate(string text)
    {
        var singleLine = text.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= MaxDetailsLength ? singleLine : singleLine[..MaxDetailsLength] + "…";
    }
}
