using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace VoiceType.Settings;

/// <summary>
/// إعدادات المقارنة المشتركة بين برنامج المختبر وأداة سطر الأوامر (والوكيل البرمجي).
/// تُحفظ في %APPDATA%\VoiceType\lab.json. لا تحتوي أي مفتاح.
/// </summary>
public sealed class LabSettings
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

    /// <summary>إصدار ملف الإعدادات (للترقية التلقائية).</summary>
    public int Version { get; set; }

    /// <summary>لغة الكلام. فارغ = تلقائي.</summary>
    public string? Language { get; set; } = "ar";

    /// <summary>مجلد التسجيلات. فارغ = Documents\VoiceType Lab\audio.</summary>
    public string? AudioFolder { get; set; }

    public int Parallelism { get; set; } = 4;

    public List<LabModel> Models { get; set; } = [];

    /// <summary>المزودات التي أُضيفت نماذجها مسبقًا (حتى لا نعيد إضافة ما حذفه المستخدم).</summary>
    public List<string> KnownProviders { get; set; } = [];

    public static string FilePath => Path.Combine(AppPaths.AppData, "lab.json");

    public string ResolvedAudioFolder => string.IsNullOrWhiteSpace(AudioFolder) ? AppPaths.LabAudio : AudioFolder;

    public static LabSettings Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<LabSettings>(File.ReadAllText(path), JsonOptions) ?? new LabSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // ملف تالف: نبدأ من جديد بدل أن يتعطل البرنامج
        }

        return new LabSettings();
    }

    public void Save(string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public LabModel? Find(string provider, string? model) =>
        Models.FirstOrDefault(m =>
            string.Equals(m.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(m.Model ?? string.Empty, model ?? string.Empty, StringComparison.Ordinal));

    /// <summary>يضيف نموذجًا إن لم يكن موجودًا. يعيد true إذا أُضيف.</summary>
    public bool Add(string provider, string? model, bool enabled = true)
    {
        if (Find(provider, model) is { } existing)
        {
            existing.Enabled = existing.Enabled || enabled;
            return false;
        }

        Models.Add(new LabModel { Provider = provider, Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(), Enabled = enabled });
        return true;
    }

    public bool Remove(string provider, string? model) =>
        Find(provider, model) is { } existing && Models.Remove(existing);
}

public sealed class LabModel
{
    public string Provider { get; set; } = string.Empty;

    public string? Model { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>تعديل متغيرات المزود لهذا النموذج فقط (مثل base_url لخادم خاص).</summary>
    public Dictionary<string, string>? Variables { get; set; }

    public override string ToString() => Model is null ? Provider : $"{Provider}:{Model}";
}
