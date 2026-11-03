using VoiceType.Settings;

namespace VoiceType.Speech;

/// <summary>نموذج Whisper يمكن تنزيله وتشغيله على جهاز المستخدم.</summary>
public sealed record LocalModelInfo(
    string FileName,
    string DisplayName,
    double SizeMegabytes,
    string Description,
    string DownloadUrl,
    bool Recommended = false);

/// <summary>
/// النماذج المحلية: قائمة مقترحة للتنزيل + أي ملف .bin يضيفه المستخدم بنفسه.
/// كل النماذج تُحفظ في %LOCALAPPDATA%\VoiceType\models.
/// </summary>
public static class LocalModels
{
    public const string ProviderId = "local";

    private const string BaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static IReadOnlyList<LocalModelInfo> Suggested { get; } =
    [
        new("ggml-large-v3-q5_0.bin", "Whisper Large-v3 Q5 (الأدق للعربي)", 1081,
            "الأفضل للعامية المصرية والعربي المخلوط بالإنجليزي. هو المقترح كنموذج احتياطي.", BaseUrl + "ggml-large-v3-q5_0.bin", Recommended: true),
        new("ggml-large-v3-turbo-q5_0.bin", "Whisper Turbo Q5 (سريع وخفيف)", 574,
            "أسرع وأصغر، لكنه أضعف في العامية المصرية. مناسب للأجهزة الضعيفة أو للإنجليزي.", BaseUrl + "ggml-large-v3-turbo-q5_0.bin"),
        new("ggml-large-v3-turbo-q8_0.bin", "Whisper Turbo Q8", 874,
            "نسخة Turbo بضغط أقل. في العربي المصري غالبًا أضعف من Large-v3.", BaseUrl + "ggml-large-v3-turbo-q8_0.bin"),
        new("ggml-large-v3-turbo.bin", "Whisper Turbo (كامل)", 1620,
            "Turbo بدون ضغط. حجم كبير وتحسن بسيط؛ Large-v3 أفضل منه في العربي.", BaseUrl + "ggml-large-v3-turbo.bin"),
        new(CohereArabicFile, "Cohere Arabic Q4 (تجريبي)", 1440,
            "نموذج Cohere الجديد المتخصص في العربي (2 مليار). بيشتغل بمحرك CrispASR المجاني (29 ميجا، بينزل معاه تلقائي). " +
            "لسه بنجربه على العامية المصرية قبل ما يبقى الاحتياطي.",
            "https://huggingface.co/cstr/cohere-transcribe-arabic-07-2026-GGUF/resolve/main/" + CohereArabicFile),
    ];

    /// <summary>Cohere Transcribe Arabic (يوليو 2026) بضغط Q4 متظبط على العربي، زي ما صاحب النسخة بينصح.</summary>
    public const string CohereArabicFile = "cohere-transcribe-arabic-q4_k-imatrix.gguf";

    /// <summary>نموذج بيشتغل بمحرك CrispASR (مش Whisper).</summary>
    public static bool IsCrispModel(string fileName) =>
        fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
        fileName.StartsWith("cohere-transcribe", StringComparison.OrdinalIgnoreCase);

    /// <summary>هل النموذج جاهز يشتغل؟ نماذج CrispASR محتاجة المحرك بتاعها كمان.</summary>
    public static bool IsRunnable(string fileName) => IsInstalled(fileName) && (!IsCrispModel(fileName) || CrispRuntime.IsInstalled);

    /// <summary>المحرك المناسب لملف النموذج.</summary>
    public static ILocalEngine CreateEngine(string fileName, string? displayName = null) =>
        IsCrispModel(fileName)
            ? new CrispLocalEngine(PathOf(fileName), displayName: displayName)
            : new WhisperLocalEngine(PathOf(fileName), displayName: displayName);

    /// <summary>
    /// مكان تنزيل النماذج الجديدة: المجلد المشترك مع Vibe إن وُجد (حتى لا تتكرر)، وإلا data\models بجوار البرنامج.
    /// </summary>
    public static string Folder => AppPaths.SharedModels ?? AppPaths.Models;

    /// <summary>مجلدات صدى نفسه (يُحذف منها فقط؛ المجلد المشترك ملك Vibe أيضًا).</summary>
    private static IReadOnlyList<string> OwnFolders =>
        new[] { AppPaths.Models, AppPaths.LegacyModels }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>كل الأماكن التي قد توجد بها نماذج: المشترك أولًا، ثم مجلد صدى، ثم القديم.</summary>
    public static IReadOnlyList<string> SearchFolders =>
        new[] { AppPaths.SharedModels, AppPaths.Models, AppPaths.LegacyModels }
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>مسار النموذج إن كان موجودًا في أي مكان، وإلا المسار الذي سيُنزّل إليه.</summary>
    public static string PathOf(string fileName) =>
        SearchFolders.Select(folder => System.IO.Path.Combine(folder, fileName)).FirstOrDefault(File.Exists)
        ?? System.IO.Path.Combine(Folder, fileName);

    /// <summary>هل النموذج موجود في المجلد المشترك مع Vibe؟</summary>
    public static bool IsShared(string fileName) =>
        AppPaths.SharedModels is { } shared && File.Exists(System.IO.Path.Combine(shared, fileName));

    public static bool IsInstalled(string fileName) =>
        SearchFolders.Any(folder => File.Exists(System.IO.Path.Combine(folder, fileName)));

    /// <summary>كل ملفات النماذج الموجودة على الجهاز (المقترحة والمضافة يدويًا).</summary>
    public static IReadOnlyList<string> Installed() =>
        SearchFolders
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.bin").Concat(Directory.EnumerateFiles(folder, "*.gguf")))
            .Select(System.IO.Path.GetFileName)
            .OfType<string>()
            .Where(f => f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || IsCrispModel(f))
            .Where(IsTranscriptionModel)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(IsCrispModel) // Whisper الأول: الاحتياطي التلقائي يفضل النموذج المجرّب
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// ملفات .bin ليست كلها نماذج تفريغ: Vibe يضع في نفس المجلد نموذج كشف الكلام (Silero VAD).
    /// </summary>
    public static bool IsTranscriptionModel(string fileName) =>
        !fileName.Contains("silero", StringComparison.OrdinalIgnoreCase) &&
        !fileName.Contains("-vad", StringComparison.OrdinalIgnoreCase) &&
        !fileName.StartsWith("vad", StringComparison.OrdinalIgnoreCase);

    public static string DisplayNameOf(string fileName) =>
        Suggested.FirstOrDefault(m => string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase))?.DisplayName
        ?? System.IO.Path.GetFileNameWithoutExtension(fileName);

    /// <summary>ينسخ ملف نموذج (.bin بصيغة whisper.cpp) اختاره المستخدم إلى مجلد النماذج.</summary>
    public static string AddFromFile(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("الملف غير موجود.", sourcePath);
        }

        if (!string.Equals(System.IO.Path.GetExtension(sourcePath), ".bin", StringComparison.OrdinalIgnoreCase) &&
            !IsCrispModel(System.IO.Path.GetFileName(sourcePath)))
        {
            throw new InvalidOperationException("ملف النموذج يجب أن يكون بصيغة whisper.cpp وامتداده .bin (مثل ggml-large-v3-turbo.bin).");
        }

        var name = System.IO.Path.GetFileName(sourcePath);
        var sourceFolder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sourcePath));
        if (SearchFolders.Any(folder => string.Equals(System.IO.Path.GetFullPath(folder).TrimEnd('\\', '/'), sourceFolder?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
        {
            return name; // موجود بالفعل في مكان يقرأ منه صدى: لا ننسخه مرة ثانية
        }

        Directory.CreateDirectory(Folder);
        var target = System.IO.Path.Combine(Folder, name);
        File.Copy(sourcePath, target, overwrite: true);
        return name;
    }

    public static bool Remove(string fileName)
    {
        var removed = false;
        foreach (var path in OwnFolders.Select(folder => System.IO.Path.Combine(folder, fileName)).Where(File.Exists))
        {
            File.Delete(path);
            removed = true;
        }

        return removed;
    }
}
