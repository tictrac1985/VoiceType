using System.Text.Json.Nodes;

namespace VoiceType.Providers.Definitions;

/// <summary>
/// تعريف مزود تفريغ. كل المزودات (المدمجة والمخصصة) تستخدم نفس هذا التعريف،
/// ويُقرأ من ملف JSON. انظر docs/custom-provider.md.
/// </summary>
public sealed class ProviderDefinition
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Website { get; set; }

    /// <summary>رابط صفحة إنشاء مفتاح API لدى المزود.</summary>
    public string? KeyUrl { get; set; }

    /// <summary>free / free-tier / trial / paid — يُعرض للمستخدم حتى يعرف هل سيدفع أم لا.</summary>
    public string? Pricing { get; set; }

    /// <summary>شرح قصير للتكلفة أو الحدود المجانية.</summary>
    public string? PricingNote { get; set; }

    public bool IsPaid => string.Equals(Pricing, "paid", StringComparison.OrdinalIgnoreCase);

    /// <summary>نص قصير للعرض: مجاني / مجاني بحدود / تجربة مجانية / مدفوع.</summary>
    public string PricingLabel => Pricing?.ToLowerInvariant() switch
    {
        "free" => "مجاني",
        "free-tier" => "مجاني بحدود",
        "trial" => "تجربة مجانية",
        "paid" => "مدفوع",
        _ => "غير محدد",
    };

    /// <summary>متغيرات بقيم افتراضية، يمكن للمستخدم تغييرها (مثل base_url).</summary>
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ProviderModel> Models { get; set; } = [];

    public string? DefaultModel { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>أقصى عدد طلبات في الدقيقة (مثل حدود الخطط المجانية). البرنامج يتمهل تلقائيًا ليلتزم به.</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>كيف نجلب قائمة النماذج المتاحة من المزود (اختياري).</summary>
    /// <summary>يستخدم مفتاح خدمة أخرى (مثل openrouter-free ← openrouter) فلا يلصق المستخدم المفتاح مرتين.</summary>
    public string? KeyFrom { get; set; }

    public ModelsListDefinition? ModelsList { get; set; }

    public RequestDefinition Request { get; set; } = new();

    public ResponseDefinition Response { get; set; } = new();

    public CapabilitiesDefinition Capabilities { get; set; } = new();
}

public sealed class ModelsListDefinition
{
    /// <summary>عنوان قائمة النماذج، مثل {base_url}/models (طلب GET بنفس المصادقة).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>مسار مصفوفة النماذج في الرد، مثل data أو models.</summary>
    public string ItemsPath { get; set; } = "data";

    /// <summary>مسار المعرّف داخل كل عنصر، مثل id أو name.</summary>
    public string IdPath { get; set; } = "id";

    /// <summary>بادئة تُحذف من المعرّف، مثل models/</summary>
    public string? StripPrefix { get; set; }

    /// <summary>يُعرض فقط ما يحتوي على إحدى هذه الكلمات (مثل transcribe أو whisper).</summary>
    public List<string>? Contains { get; set; }
}

public sealed class ProviderModel
{
    public string Id { get; set; } = string.Empty;

    public string? Name { get; set; }

    public string? Notes { get; set; }
}

public sealed class RequestDefinition
{
    /// <summary>العنوان، ويقبل متغيرات مثل {base_url} و{model}.</summary>
    public string Url { get; set; } = string.Empty;

    public string Method { get; set; } = "POST";

    public AuthDefinition Auth { get; set; } = new();

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>معاملات Query. القيم الفارغة بعد التعويض تُحذف.</summary>
    public Dictionary<string, string> Query { get; set; } = new(StringComparer.Ordinal);

    public BodyDefinition Body { get; set; } = new();
}

public sealed class AuthDefinition
{
    public const string Bearer = "bearer";
    public const string Header = "header";
    public const string QueryParameter = "query";
    public const string None = "none";

    /// <summary>bearer / header / query / none</summary>
    public string Type { get; set; } = Bearer;

    /// <summary>اسم الـHeader أو معامل الـQuery (مثل xi-api-key).</summary>
    public string? Name { get; set; }

    /// <summary>بادئة قبل المفتاح (مثل Token).</summary>
    public string? Prefix { get; set; }

    /// <summary>عند true يُرسل الطلب بدون مصادقة إذا لم يُدخل مفتاح (للخوادم المحلية).</summary>
    public bool Optional { get; set; }
}

public sealed class BodyDefinition
{
    public const string Multipart = "multipart";
    public const string Json = "json";
    public const string Raw = "raw";

    /// <summary>multipart / json / raw</summary>
    public string Type { get; set; } = Multipart;

    /// <summary>multipart: اسم حقل الملف.</summary>
    public string FileField { get; set; } = "file";

    /// <summary>multipart: الحقول النصية. القيم الفارغة بعد التعويض تُحذف.</summary>
    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);

    /// <summary>json: قالب JSON كامل.</summary>
    public JsonNode? Template { get; set; }

    /// <summary>raw: نوع المحتوى. الافتراضي {audio_mime}.</summary>
    public string? ContentType { get; set; }
}

public sealed class ResponseDefinition
{
    /// <summary>مسار النص في الرد، مثل text أو results.channels[0].alternatives[0].transcript. القيمة $ تعني الرد كاملًا كنص.</summary>
    public string TextPath { get; set; } = "text";

    public string? ErrorPath { get; set; }

    public string? CostPath { get; set; }

    /// <summary>
    /// للنماذج اللغوية (Chat) التي تُستخدم في التفريغ: تحذف التفكير المكتوب، وعلامات الكود،
    /// وعبارات مثل "Transcription:" وعلامات التنصيص المحيطة بالنص.
    /// </summary>
    public bool CleanupChatText { get; set; }

    /// <summary>
    /// لخدمات قد ترد بأن العمل ما زال جاريًا (مثل status = in_progress):
    /// يعيد البرنامج السؤال على عنوان النتيجة حتى تكتمل.
    /// </summary>
    public PollingDefinition? Polling { get; set; }
}

public sealed class PollingDefinition
{
    /// <summary>مسار الحالة في الرد، مثل status.</summary>
    public string StatusPath { get; set; } = "status";

    /// <summary>قيم الحالة التي تعني أن النتيجة لم تجهز بعد.</summary>
    public List<string> Pending { get; set; } = ["in_progress", "queued", "pending", "processing"];

    /// <summary>مسار معرّف العملية في الرد، مثل id.</summary>
    public string IdPath { get; set; } = "id";

    /// <summary>عنوان السؤال عن النتيجة (طلب GET بنفس المصادقة)، مثل {base_url}/{id}.</summary>
    public string Url { get; set; } = string.Empty;

    public int IntervalMs { get; set; } = 1000;
}

public sealed class CapabilitiesDefinition
{
    public List<string>? Formats { get; set; }

    public double? MaxFileMb { get; set; }

    public bool LanguageRequired { get; set; }

    public bool SupportsPrompt { get; set; }
}
