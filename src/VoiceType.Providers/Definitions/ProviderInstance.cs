namespace VoiceType.Providers.Definitions;

/// <summary>
/// نسخة مزود كما أعدّها المستخدم: أي تعريف + أي نموذج + المفتاح + تعديل المتغيرات.
/// يمكن إنشاء أكثر من نسخة من نفس المزود (مثل نموذجين مختلفين أو مفتاحين).
/// </summary>
public sealed class ProviderInstance
{
    /// <summary>معرّف النسخة. إن تُرك فارغًا يُستخدم definition:model.</summary>
    public string? Id { get; set; }

    /// <summary>معرّف تعريف المزود في الكتالوج.</summary>
    public string Definition { get; set; } = string.Empty;

    public string? Label { get; set; }

    public string? Model { get; set; }

    /// <summary>المفتاح نفسه. في التطبيق يُحفظ مشفرًا بـDPAPI ولا يُكتب في ملفات الإعدادات.</summary>
    public string? ApiKey { get; set; }

    /// <summary>تعديل متغيرات التعريف (مثل base_url لخادم خاص).</summary>
    public Dictionary<string, string> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int? TimeoutSeconds { get; set; }

    public bool Enabled { get; set; } = true;
}
