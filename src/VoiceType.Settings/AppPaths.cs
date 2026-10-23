namespace VoiceType.Settings;

/// <summary>
/// أماكن ملفات VoiceType. كل شيء في مجلد واحد: <c>data</c> بجوار البرنامج نفسه
/// (الإعدادات، المفاتيح المشفرة، النماذج، التسجيلات، النتائج).
/// إذا كان مجلد البرنامج غير قابل للكتابة (مثل Program Files) يُستخدم %APPDATA%\VoiceType.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> DefaultRoot = new(ResolveDefaultRoot);

    /// <summary>المجلد الرئيسي لكل بيانات البرنامج.</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("VOICETYPE_HOME") is { Length: > 0 } home ? home
        : Environment.GetEnvironmentVariable("VOICETYPE_APPDATA") is { Length: > 0 } legacyOverride ? legacyOverride
        : DefaultRoot.Value;

    /// <summary>اسم قديم لـ Root (للتوافق).</summary>
    public static string AppData => Root;

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string KeysFile => Path.Combine(Root, "keys.dat");

    public static string CustomProviders => Path.Combine(Root, "providers");

    public static string Logs => Path.Combine(Root, "logs");

    public static string Models =>
        Environment.GetEnvironmentVariable("VOICETYPE_MODELS") is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Root, "models");

    /// <summary>
    /// مجلد نماذج مشترك مع برنامج Vibe (نفس صيغة whisper.cpp): يُقرأ من إعدادات Vibe إن كان مثبتًا.
    /// بهذا لا يتكرر نموذج حجمه جيجا على القرص، والبرنامجان يقرآن من نفس المكان.
    /// null إذا لم يكن Vibe مثبتًا أو لم يُحدَّد له مجلد موجود.
    /// </summary>
    public static string? SharedModels => SharedModelsLazy.Value;

    private static readonly Lazy<string?> SharedModelsLazy = new(() =>
        Environment.GetEnvironmentVariable("VOICETYPE_MODELS") is { Length: > 0 } ? null : DetectVibeModels());

    /// <summary>للاختبارات: إعادة اكتشاف المجلد المشترك من ملف إعدادات محدد.</summary>
    public static string? DetectVibeModels(string? configPath = null)
    {
        try
        {
            configPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "github.com.thewh1teagle.vibe", "app_config.json");
            if (!File.Exists(configPath))
            {
                return null;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.TryGetProperty("models_folder", out var folder) &&
                folder.ValueKind == System.Text.Json.JsonValueKind.String &&
                folder.GetString() is { Length: > 0 } path && Directory.Exists(path))
            {
                return Path.GetFullPath(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        {
            // إعدادات Vibe غير مقروءة: نكمل بمجلدنا فقط
        }

        return null;
    }

    /// <summary>تسجيلات جمل الاختبار.</summary>
    public static string LabAudio => Path.Combine(Root, "recordings");

    public static string LabResults => Path.Combine(Root, "results");

    /// <summary>ما تعلّمه البرنامج من Gemini (تصحيحات وكلمات إنجليزية) — ملف صغير ينتقل مع البرنامج.</summary>
    public static string LearnedFile => Path.Combine(Root, "learned.json");

    /// <summary>النصوص المفرغة من الملفات الصوتية.</summary>
    public static string Transcripts => Path.Combine(Root, "transcripts");

    /// <summary>ذاكرة مؤقتة للواجهة (ليست بيانات للمستخدم)، لذلك خارج مجلد البرنامج المحمول.</summary>
    public static string WebViewData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceType", "webview");

    // الأماكن القديمة (قبل جعل البيانات بجوار البرنامج) — تُنقل منها البيانات مرة واحدة
    public static string LegacyAppData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceType");

    public static string LegacyModels =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceType", "models");

    public static string LegacyLabAudio =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoiceType Lab", "audio");

    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string ResolveDefaultRoot()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return portable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceType");
        }
    }
}

/// <summary>
/// ينقل بيانات الإصدارات السابقة (المفاتيح، الإعدادات، الخدمات المخصصة، التسجيلات) إلى مجلد البرنامج.
/// يُنفذ مرة واحدة، ولا يحذف الأصل، ولا يستبدل ملفات موجودة.
/// النماذج الكبيرة لا تُنسخ: البرنامج يقرأها من مكانها القديم مباشرة.
/// </summary>
public static class LegacyMigration
{
    public static void RunOnce()
    {
        var marker = Path.Combine(AppPaths.Root, ".migrated-v1");
        if (File.Exists(marker) || string.Equals(Path.GetFullPath(AppPaths.Root).TrimEnd('\\', '/'),
                Path.GetFullPath(AppPaths.LegacyAppData).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            AppPaths.Ensure(AppPaths.Root);
            CopyIfMissing(Path.Combine(AppPaths.LegacyAppData, "keys.dat"), AppPaths.KeysFile);
            CopyFolder(Path.Combine(AppPaths.LegacyAppData, "providers"), AppPaths.CustomProviders, "*.json");
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // الترحيل اختياري: إذا فشل يعمل البرنامج بإعدادات جديدة
        }
    }

    /// <summary>
    /// ينقل النماذج من مكانها القديم (%LOCALAPPDATA%) إلى data\models بجوار البرنامج،
    /// حتى يكون البرنامج كاملًا في مجلد واحد يمكن نقله على فلاشة.
    /// النسخ إلى ملف مؤقت ثم إعادة التسمية، فلا يظهر نموذج ناقص أبدًا.
    /// </summary>
    public static void MoveLegacyModels()
    {
        var source = AppPaths.LegacyModels;
        var target = AppPaths.Models;
        if (!Directory.Exists(source) ||
            string.Equals(Path.GetFullPath(source).TrimEnd('\\', '/'), Path.GetFullPath(target).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(source, "*.bin"))
        {
            try
            {
                Directory.CreateDirectory(target);
                var destination = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(destination))
                {
                    var temporary = destination + ".moving";
                    File.Copy(file, temporary, overwrite: true);
                    File.Move(temporary, destination);
                }

                if (new FileInfo(destination).Length == new FileInfo(file).Length)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // نجرب مرة أخرى في التشغيل القادم
            }
        }
    }

    private static void CopyIfMissing(string source, string target)
    {
        if (File.Exists(source) && !File.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    private static void CopyFolder(string source, string target, string pattern)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(source, pattern))
        {
            CopyIfMissing(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }
}
