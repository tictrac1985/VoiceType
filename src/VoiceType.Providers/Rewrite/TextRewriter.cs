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
/// إعادة صياغة الكلام: رسمي، إيميل، ترجمة، وتلخيص الاجتماعات.
/// بيجرّب الموديل اللي المستخدم اختاره الأول (Ollama أو LM Studio، على الجهاز أو السحابة)،
/// ولو فشل يكمّل بـ Gemini (مجاني بنفس مفتاح التفريغ). لو كله فشل يرمي خطأ (فيُكتب النص كما قيل).
/// </summary>
public sealed class TextRewriter
{
    public static readonly IReadOnlyList<string> DefaultModels = ["gemini-flash-latest", "gemini-flash-lite-latest"];

    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models/";

    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _keys;
    private readonly IReadOnlyList<string> _models;
    private readonly IReadOnlyList<ITextEngine> _preferred;

    public TextRewriter(HttpClient http, IReadOnlyList<string> apiKeys, IReadOnlyList<string>? models = null, IReadOnlyList<ITextEngine>? preferred = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _keys = apiKeys?.Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? throw new ArgumentNullException(nameof(apiKeys));
        _models = models ?? DefaultModels;
        _preferred = preferred ?? [];
    }

    /// <summary>اسم آخر موديل كتب الرد (للعرض: «اتعمل بـ …»).</summary>
    public string? LastEngine { get; private set; }

    /// <summary>أخطاء الموديلات اللي فشلت قبل اللي نجح (لو فيه).</summary>
    public IReadOnlyList<string> LastFailures { get; private set; } = [];

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// أقصى انتظار للموديل المختار (Ollama / LM Studio) قبل ما نكمّل بـ Gemini. null = مهلة الموديل نفسه.
    /// في الإملاء بنحطها قصيرة، عشان موديل بطيء على الجهاز ميعلّقش الميكروفون دقايق.
    /// </summary>
    public TimeSpan? PreferredTimeout { get; init; }

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

    /// <summary>ترجمة نص طويل للإنجليزي: على أجزاء (عند الفقرات) عشان الرد ميتقطعش.</summary>
    public async Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default, int maxChunkChars = 3500)
    {
        var parts = new List<string>();
        foreach (var chunk in ChunkByParagraphs(text, maxChunkChars))
        {
            parts.Add(await GenerateAsync(Instructions(WritingMode.English) +
                " Keep paragraph breaks and speaker labels (write «المتكلم 1:» as «Speaker 1:»).", chunk, 0.2, cancellationToken).ConfigureAwait(false));
        }

        return string.Join("\n\n", parts);
    }

    public static IReadOnlyList<string> ChunkByParagraphs(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var paragraph in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.Length > 0 && current.Length + paragraph.Length + 2 > maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(paragraph);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString());
        }

        return chunks;
    }

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
        var errors = new List<string>();
        foreach (var engine in _preferred)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (PreferredTimeout is { } preferredTimeout)
            {
                limit.CancelAfter(preferredTimeout);
            }

            try
            {
                var answer = await engine.GenerateAsync(instructions, text, temperature, limit.Token).ConfigureAwait(false);
                LastEngine = engine.Name;
                LastFailures = errors;
                return answer;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{engine.Name}: اتأخر، فكمّلنا بالبديل.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                errors.Add(ex.Message); // أي فشل (خادم مقفول، رد غريب…): نكمّل بالبديل (Gemini)
            }
        }

        if (_keys.Count == 0)
        {
            throw new InvalidOperationException(errors.Count > 0
                ? "تعذّرت الصياغة: " + string.Join(" | ", errors.Take(3))
                : "اختار موديل للذكاء الاصطناعي من الإعدادات، أو أضف مفتاح Gemini (مجاني) من صفحة النماذج.");
        }

        var body = RequestBody(instructions, text, temperature);
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
                        LastEngine = "Gemini · " + model;
                        LastFailures = errors;
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
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException or InvalidOperationException or FormatException)
        {
            return string.Empty;
        }
    }
}
