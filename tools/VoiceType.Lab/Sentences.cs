namespace VoiceType.Lab;

/// <summary>جمل الاختبار المقترحة: مصري، فصحى، عربي مختلط بالإنجليزية، أرقام، جمل طويلة.</summary>
internal static class Sentences
{
    public static readonly IReadOnlyList<string> Defaults =
    [
        "السلام عليكم يا محمد، أتمنى تكون بخير. هل ممكن تبعتلي الملف النهارده؟",
        "أنا هتأخر شوية النهارده عشان الطريق زحمة جدًا، ابدأوا من غيري.",
        "معلش مش هقدر أحضر الاجتماع بكرة، ممكن نأجله ليوم الخميس؟",
        "الأكل كان حلو أوي بس الخدمة كانت بطيئة شوية.",
        "إيه رأيك نخرج نتغدى برا بعد الشغل؟",
        "نود إبلاغكم بأن موعد تسليم المشروع قد تأجل إلى الأسبوع القادم.",
        "يرجى مراجعة المستندات المرفقة وإرسال ملاحظاتكم في أقرب وقت ممكن.",
        "افتح ChatGPT واكتب له Python code عشان يعمل API.",
        "محتاج أعمل deploy للـ backend على Docker قبل الـ meeting.",
        "الـ bug ده في function اسمها getUserData في ملف الـ JavaScript.",
        "ابعتلي الـ link بتاع الـ GitHub repository على WhatsApp.",
        "الـ model ده بيشتغل على GPU وبياخد تقريبًا 12 GB من الـ VRAM.",
        "جرب تستخدم OpenRouter بدل OpenAI عشان الـ pricing أرخص.",
        "عايزين نعمل integration بين Supabase و React في الـ frontend.",
        "رقم الطلب هو 45872 والمبلغ 1250 جنيه.",
        "الاجتماع يوم 15 أكتوبر الساعة 3 ونص العصر.",
        "التليفون 01012345678 وممكن تكلمني بعد الساعة 9 بالليل.",
        "بص يا سيدي، المشكلة إن العميل عايز التعديلات دي تخلص قبل آخر الشهر، وإحنا لسه ما خلصناش الـ testing، فلازم نقسم الشغل بينا من النهارده.",
        "لو سمحت اكتب رسالة للعميل تقول له إن المشروع هيتأخر يومين بسبب مشكلة في السيرفر، وإننا بنشتغل عليها دلوقتي.",
        "أنا جربت البرنامج على اللابتوب بتاعي وكان شغال تمام، بس على جهاز المكتب بيقفل لوحده بعد دقيقة.",
    ];
}

internal sealed class SentenceItem
{
    public required int Index { get; init; }

    public string Text { get; set; } = string.Empty;

    public bool Recorded { get; set; }

    public string FileBase => Index.ToString("00", System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString()
    {
        var preview = string.IsNullOrWhiteSpace(Text) ? "(جملة جديدة)" : Text.Length > 38 ? Text[..38] + "…" : Text;
        return $"{(Recorded ? "✓" : "○")}  {Index}. {preview}";
    }
}
