using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceType.Providers.Engines;

namespace VoiceType.Providers.Rewrite;

/// <summary>طريقة الكتابة بعد التفريغ.</summary>
public enum WritingMode
{
    /// <summary>كما قيل (مع التنسيق العادي).</summary>
    Normal,

    /// <summary>صياغة رسمية بالفصحى.</summary>
    Formal,

    /// <summary>رسالة بريد إلكتروني مرتبة.</summary>
    Email,

    /// <summary>ترجمة إلى الإنجليزية.</summary>
    English,
}

/// <summary>
/// إعادة صياغة الكلام بـ Gemini (مجاني بنفس مفتاح التفريغ): رسمي، إيميل، ترجمة، وتلخيص الاجتماعات.
/// يجرب المفاتيح والنماذج بالترتيب، ويرمي خطأ إذا فشلت كلها (فيُكتب النص كما قيل).
/// </summary>
public sealed class TextRewriter
{
    public static readonly IReadOnlyList<string> DefaultModels = ["gemini-flash-latest", "gemini-flash-lite-latest"];

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _keys;
    private readonly IReadOnlyList<string> _models;

    public TextRewriter(HttpClient http, IReadOnlyList<string> apiKeys, IReadOnlyList<string>? models = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keys = apiKeys?.Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? throw new ArgumentNullException(nameof(apiKeys));
        _models = models ?? DefaultModels;
    }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(25);

    public static WritingMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "formal" => WritingMode.Formal,
        "email" => WritingMode.Email,
        "english" => WritingMode.English,
        _ => WritingMode.Normal,
    };

    public static string ModeId(WritingMode mode) => mode switch
    {
        WritingMode.Formal => "formal",
        WritingMode.Email => "email",
        WritingMode.English => "english",
        _ => "normal",
    };

    public static string Instructions(WritingMode mode) => mode switch
    {
        WritingMode.Formal =>
            "أعد صياغة الكلام التالي (قد يكون بالعامية المصرية ومخلوطًا بكلمات إنجليزية) بالعربية الفصحى، بأسلوب واضح ومهذب ومنظم وعلامات ترقيم صحيحة. " +
            "لا تضف أي معلومة غير موجودة ولا تحذف أي معنى. اترك أسماء البرامج والمصطلحات الإنجليزية بحروفها الإنجليزية. " +
            "إذا كان الكلام بالإنجليزية فأعد صياغته بإنجليزية رسمية. أخرج النص النهائي فقط بدون مقدمة أو شرح أو علامات تنصيص.",
        WritingMode.Email =>
            "حوّل الكلام التالي إلى رسالة بريد إلكتروني رسمية ومرتبة بنفس لغة المتكلم (الفصحى إذا كان عربيًا): " +
            "تحية مناسبة في سطر، ثم المحتوى في فقرات قصيرة واضحة، ثم خاتمة مهذبة. لا تكتب سطر الموضوع ولا اسم المرسل إلا إذا ذُكر. " +
            "لا تضف معلومات غير موجودة. اترك المصطلحات الإنجليزية بحروفها. أخرج نص الرسالة فقط بدون أي شرح.",
        WritingMode.English =>
            "Translate the following speech (usually Egyptian Arabic, sometimes mixed with English words) into natural, clear, well-punctuated English. " +
            "Keep the meaning and tone; do not add or remove information. Output only the translation, with no notes or quotes.",
        _ => string.Empty,
    };

    public const string SummaryInstructions =
        "هذا تفريغ نصي لاجتماع أو مكالمة (قد يكون بالعامية المصرية ومخلوطًا بالإنجليزية). اكتب ملخصًا عمليًا بالعربية الفصحى بهذا الشكل بالضبط:\n" +
        "الملخص:\n(من ٣ إلى ٦ جمل تشرح ما دار)\n\n" +
        "أهم النقاط:\n- نقطة\n- نقطة\n\n" +
        "القرارات:\n- قرار (أو: لا توجد)\n\n" +
        "المهام:\n- المهمة — المسؤول — الموعد (اكتب «غير محدد» لما لم يُذكر، أو: لا توجد)\n\n" +
        "لا تخترع أسماء أو مواعيد غير مذكورة. اترك المصطلحات الإنجليزية بحروفها. لا تكتب أي شيء قبل «الملخص:».";

    public Task<string> RewriteAsync(string text, WritingMode mode, CancellationToken cancellationToken = default) =>
        mode == WritingMode.Normal || string.IsNullOrWhiteSpace(text)
            ? Task.FromResult(text)
            : GenerateAsync(Instructions(mode), text, 0.3, cancellationToken);

    public Task<string> SummarizeAsync(string transcript, CancellationToken cancellationToken = default) =>
        GenerateAsync(SummaryInstructions, transcript, 0.2, cancellationToken);

    public static string RequestBody(string instructions, string text, double temperature) => new JsonObject
    {
        ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = instructions }) },
        ["contents"] = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
        }),
        ["generationConfig"] = new JsonObject { ["temperature"] = temperature },
    }.ToJsonString();

    private async Task<string> GenerateAsync(string instructions, string text, double temperature, CancellationToken cancellationToken)
    {
        if (_keys.Count == 0)
        {
            throw new InvalidOperationException("أضف مفتاح Gemini أولًا (مجاني) من صفحة النماذج.");
        }

        var body = RequestBody(instructions, text, temperature);
        var errors = new List<string>();
        foreach (var model in _models)
        {
            foreach (var key in _keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Timeout);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + model + ":generateContent")
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    };
                    request.Headers.Add("x-goog-api-key", key);
                    using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                    var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"{model}: HTTP {(int)response.StatusCode}");
                        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                        {
                            break; // النموذج نفسه غير متاح: نجرب نموذجًا آخر
                        }

                        continue; // حد مجاني أو مفتاح مرفوض: المفتاح التالي
                    }

                    var result = ExtractText(json);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        return ChatTextCleanup.Clean(result).Trim();
                    }

                    errors.Add($"{model}: empty");
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    errors.Add($"{model}: timeout");
                }
                catch (HttpRequestException ex)
                {
                    errors.Add($"{model}: {ex.Message}");
                }
            }
        }

        throw new InvalidOperationException("تعذّرت الصياغة الآن: " + string.Join(" | ", errors.Distinct().Take(4)));
    }

    /// <summary>نص الرد (بدون أجزاء «التفكير» إن وُجدت).</summary>
    public static string ExtractText(string json)
    {
        try
        {
            var parts = JsonNode.Parse(json)?["candidates"]?[0]?["content"]?["parts"] as JsonArray;
            if (parts is null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var part in parts.OfType<JsonObject>())
            {
                if (part["thought"] is JsonValue thought && thought.TryGetValue<bool>(out var isThought) && isThought)
                {
                    continue;
                }

                if (part["text"] is JsonValue value && value.TryGetValue<string>(out var piece))
                {
                    builder.Append(piece);
                }
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
