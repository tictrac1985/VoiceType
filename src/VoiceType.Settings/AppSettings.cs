using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace VoiceType.Settings;

/// <summary>اختيار نموذج: "local" + اسم ملف النموذج، أو معرّف خدمة + اسم النموذج.</summary>
public sealed class EngineChoice
{
    public string Provider { get; set; } = string.Empty;

    public string? Model { get; set; }

    public string Key => $"{Provider}|{Model}";

    public static EngineChoice? Parse(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var parts = key.Split('|', 2);
        return new EngineChoice { Provider = parts[0], Model = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null };
    }

    public bool Same(EngineChoice? other) =>
        other is not null &&
        string.Equals(Provider, other.Provider, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Model ?? string.Empty, other.Model ?? string.Empty, StringComparison.Ordinal);
}

/// <summary>إعدادات برنامج VoiceType (settings.json في مجلد البيانات). لا تحتوي أي مفتاح.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public int Version { get; set; } = 1;

    /// <summary>لغة الكلام: ar / en / فارغ = تلقائي.</summary>
    public string? Language { get; set; } = "ar";

    /// <summary>Raw / General / Technical</summary>
    public string TextMode { get; set; } = "General";

    /// <summary>None = فقرة واحدة، Sentences = كل جملة في سطر. الأمر الصوتي "سطر جديد" يعمل دائمًا.</summary>
    public string LineBreaks { get; set; } = "Sentences";

    /// <summary>يتعلم النموذج المحلي من نصوص Gemini في الخلفية.</summary>
    public bool LearnFromOnline { get; set; } = true;

    /// <summary>ضبط اتجاه العربي المختلط بالإنجليزي عند اللصق (علامات اتجاه غير مرئية، لا تُضاف في محررات الأكواد).</summary>
    public bool FixMixedDirection { get; set; } = true;

    /// <summary>
    /// الكتابة المباشرة أثناء الكلام (Gemini Live) — تجريبية ومغلقة افتراضيًا: دقتها أقل من التفريغ العادي وقد تتأخر في البداية.
    /// اسم جديد عمدًا حتى يُغلق تلقائيًا عند من فُعّل عنده الإصدار السابق.
    /// </summary>
    public bool LiveTypingBeta { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool LiveTyping
    {
        get => LiveTypingBeta;
        set => LiveTypingBeta = value;
    }

    /// <summary>طريقة الكتابة: normal (كما قيل) / formal (فصحى رسمية) / email / english (ترجمة).</summary>
    public string WritingMode { get; set; } = "normal";

    /// <summary>إعدادات خاصة لكل برنامج (المحادثات، المستندات، البريد، الأكواد). null = الافتراضية.</summary>
    public List<AppProfile>? AppProfiles { get; set; }

    public bool UseAppProfiles { get; set; } = true;

    /// <summary>
    /// الذكاء الاصطناعي للملخص و«صيغها لي» والترجمة:
    /// gemini (مجاني بمفتاح التفريغ) / ollama (على الجهاز أو موديلاته السحابية) / ollama-cloud (ollama.com بمفتاح) / lmstudio.
    /// لو الموديل المختار فشل، صدى يكمّل بـ Gemini.
    /// </summary>
    public string TextAiProvider { get; set; } = "gemini";

    /// <summary>اسم الموديل على Ollama أو LM Studio (مثل qwen3:8b أو gpt-oss:120b-cloud).</summary>
    public string? TextAiModel { get; set; }

    /// <summary>عنوان Ollama (الافتراضي على نفس الجهاز).</summary>
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>عنوان LM Studio (على نفس الجهاز، أو جهاز تاني، أو LM Link).</summary>
    public string LmStudioUrl { get; set; } = "http://127.0.0.1:1234";

    /// <summary>
    /// دقة النموذج المحلي (عرض البحث beam): 0 = تلقائي (5 على كرت الشاشة للملفات والإملاء، 1 على المعالج للإملاء)،
    /// 1 = الأسرع، 3 = متوازن، 5 = الأدق، 8 = أقصى دقة (أبطأ).
    /// </summary>
    public int LocalBeamSize { get; set; }

    /// <summary>«مين اللي بيتكلم» في الملفات والاجتماعات: -1 = مقفول، 0 = تلقائي، أو عدد المتكلمين.</summary>
    public int Speakers { get; set; } = -1;

    /// <summary>طول سطر الترجمة (SRT/VTT): 0 = زي ما هو، 42 = عادي، 24 = قصير للريلز، 60 = طويل.</summary>
    public int CaptionChars { get; set; } = 42;

    /// <summary>يقترح تسجيل الاجتماع لما Zoom أو Teams أو Meet يستخدموا الميكروفون.</summary>
    public bool DetectMeetings { get; set; } = true;

    /// <summary>يدوّر على تحديثات لصدى على GitHub مرة في اليوم.</summary>
    public bool CheckUpdates { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public List<AppProfile> Profiles => AppProfiles ??= AppProfile.Defaults();

    /// <summary>عند الإملاء بالعربي تبدأ الفقرة من اليمين (Word، المفكرة، المتصفح…).</summary>
    public bool ArabicStartsRight { get; set; } = true;

    /// <summary>النموذج الأساسي للإملاء.</summary>
    public EngineChoice? Primary { get; set; }

    /// <summary>النموذج البديل عند فشل الأساسي (مثل انقطاع الإنترنت).</summary>
    public EngineChoice? Fallback { get; set; }

    public bool UseFallback { get; set; } = true;

    /// <summary>النموذج المستخدم لتفريغ الملفات الصوتية (فارغ = نفس الأساسي).</summary>
    public EngineChoice? FilesEngine { get; set; }

    /// <summary>نماذج إضافية أضافها المستخدم لخدمات الإنترنت.</summary>
    public List<EngineChoice> ExtraModels { get; set; } = [];

    public bool ShowMic { get; set; } = true;

    public double? MicLeft { get; set; }

    public double? MicTop { get; set; }

    /// <summary>مكان زر المايك نفسه على الشاشة بالبكسل الحقيقي (بدل مكان النافذة القديم MicLeft/MicTop).</summary>
    public double? MicX { get; set; }

    public double? MicY { get; set; }

    /// <summary>حجم زر المايك: small / medium / large.</summary>
    public string MicSize { get; set; } = "small";

    /// <summary>مظهر البرنامج: spotlight (تحت الضوء) / orb (الكرة الزجاجية) / monolith (الخزف).</summary>
    public string Theme { get; set; } = "spotlight";

    /// <summary>لون زر المايك: gold / teal / copper / blue / purple / green / dark.</summary>
    public string MicColor { get; set; } = "gold";

    /// <summary>الزر فوق كل البرامج وشريط المهام دائمًا.</summary>
    public bool MicAlwaysOnTop { get; set; } = true;

    /// <summary>الزر شفاف قليلًا وهو غير مستخدم (يظهر بالكامل عند مرور الماوس).</summary>
    public bool MicDimWhenIdle { get; set; }

    /// <summary>-1 = الميكروفون الافتراضي في Windows.</summary>
    public int MicrophoneDevice { get; set; } = -1;

    public bool Sounds { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public static AppSettings Load(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // ملف تالف: نبدأ بالإعدادات الافتراضية
        }

        return new AppSettings();
    }

    public void Save(string? path = null)
    {
        path ??= AppPaths.SettingsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }
}
