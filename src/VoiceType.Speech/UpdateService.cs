using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;


namespace VoiceType.Speech;

/// <summary>
/// تحديث صدى من GitHub: بيشوف آخر إصدار (Release) في المستودع، ولو أحدث من النسخة اللي شغالة
/// بينزّل الملف المضغوط، ويتأكد من بصمته (SHA-256 من ملف .sha256 جنبه في نفس الإصدار)، ويركّبه مكانه.
/// الملفات اللي شغالة دلوقتي (VoiceType.exe نفسه) ويندوز بيسمح نغيّر اسمها، فبنسمّيها .old ونحط الجديد مكانها،
/// وبعدين صدى يقفل ويفتح بالنسخة الجديدة، ويمسح ملفات .old في أول تشغيل.
/// مجلد data (المفاتيح والإعدادات والنماذج والسجل) مش جوه التحديث أصلًا، فمبيتلمسش.
/// </summary>
public static class UpdateService
{
    public const string Repository = "tictrac1985/VoiceType";

    public const string AssetName = "Sada-win-x64.zip";

    private static readonly HttpClient Http = CreateClient();

    public sealed record UpdateInfo(bool Available, string Current, string? Latest, string? Notes, string? ZipUrl, string? ShaUrl);

    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public static string CurrentText => CurrentVersion.ToString(3);

    /// <summary>بيسأل GitHub عن آخر إصدار.</summary>
    public static async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new UpdateInfo(false, CurrentText, null, "لسه مفيش إصدارات منشورة على GitHub.", null, null);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"مقدرتش أوصل لـ GitHub (HTTP {(int)response.StatusCode}). جرّب تاني بعد شوية.");
        }

        return Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), CurrentVersion);
    }

    public static UpdateInfo Parse(string json, Version current)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("رد GitHub مش مفهوم.");
        var tag = ((string?)root["tag_name"] ?? string.Empty).Trim().TrimStart('v', 'V');
        var assets = root["assets"]?.AsArray() ?? [];
        string? Url(string name) => assets.FirstOrDefault(a => string.Equals((string?)a?["name"], name, StringComparison.OrdinalIgnoreCase))?["browser_download_url"]?.GetValue<string>();
        var zip = Url(AssetName);
        var sha = Url(AssetName + ".sha256");
        var notes = ((string?)root["body"])?.Trim();
        if (notes is { Length: > 300 })
        {
            notes = notes[..300] + "…";
        }

        var newer = Version.TryParse(tag, out var latest) && latest > current && zip is not null && sha is not null;
        return new UpdateInfo(newer, current.ToString(3), latest?.ToString(3) ?? tag, notes, zip, sha);
    }

    /// <summary>
    /// بينزّل التحديث ويتأكد منه ويركّبه. بعدها البرنامج لازم يقفل ويفتح (RestartAfterUpdate).
    /// </summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken, string? appFolder = null)
    {
        if (!update.Available || update.ZipUrl is null || update.ShaUrl is null)
        {
            throw new InvalidOperationException("مفيش تحديث جديد.");
        }

        var work = Path.Combine(Path.GetTempPath(), "voicetype-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var shaFile = Path.Combine(work, "sha256.txt");
            await Downloader.DownloadFileAsync(update.ShaUrl, shaFile, null, cancellationToken).ConfigureAwait(false);
            var expected = (await File.ReadAllTextAsync(shaFile, cancellationToken).ConfigureAwait(false)).Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
            if (expected.Length != 64)
            {
                throw new IOException("بصمة التحديث مش مظبوطة، فمش هنركّبه.");
            }

            var zip = Path.Combine(work, AssetName);
            await Downloader.DownloadFileAsync(update.ZipUrl, zip, progress, cancellationToken, expected).ConfigureAwait(false);

            var staging = Path.Combine(work, "files");
            ZipFile.ExtractToDirectory(zip, staging);
            if (!File.Exists(Path.Combine(staging, "VoiceType.exe")))
            {
                throw new IOException("ملف التحديث ناقص (مفيهوش VoiceType.exe).");
            }

            Apply(staging, appFolder ?? AppContext.BaseDirectory);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// بيحط كل ملف جديد مكان القديم. القديم اللي شغال بيتسمّى .old الأول (ويندوز بيسمح بده).
    /// لو أي ملف فشل في النص (برنامج حماية قافل ملف، القرص مليان…) بنرجّع كل اللي اتغيّر زي ما كان،
    /// عشان البرنامج ميفضلش نصه قديم ونصه جديد.
    /// </summary>
    public static void Apply(string staging, string appFolder)
    {
        var done = new List<(string Target, string? Old)>();
        try
        {
            foreach (var source in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staging, source);
                if (relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relative, "data", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // بيانات المستخدم مبنلمسهاش أبدًا
                }

                var target = Path.Combine(appFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string? old = null;
                if (File.Exists(target))
                {
                    old = target + ".old";
                    if (File.Exists(old))
                    {
                        TryDelete(old);
                    }

                    if (File.Exists(old))
                    {
                        old = target + "." + Guid.NewGuid().ToString("N")[..6] + ".old";
                    }

                    File.Move(target, old);
                }

                done.Add((target, old));
                File.Copy(source, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Rollback(done);
            throw new IOException("التحديث متركّبش، ورجّعنا النسخة القديمة زي ما كانت: " + ex.Message, ex);
        }
    }

    private static void Rollback(List<(string Target, string? Old)> done)
    {
        for (var i = done.Count - 1; i >= 0; i--)
        {
            var (target, old) = done[i];
            try
            {
                if (File.Exists(target) && old is not null && File.Exists(old))
                {
                    File.Move(target, target + ".failed-" + Guid.NewGuid().ToString("N")[..6] + ".old");
                }
                else if (old is null)
                {
                    TryDelete(target); // ملف جديد مكانش موجود قبل كده
                }

                if (old is not null && File.Exists(old))
                {
                    File.Move(old, target, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // بنكمّل نرجّع الباقي
            }
        }
    }

    /// <summary>يقفل ويفتح صدى بالنسخة الجديدة.</summary>
    public static void RestartAfterUpdate()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "VoiceType.exe");
        Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory });
    }

    /// <summary>في أول تشغيل بعد التحديث: نمسح ملفات .old.</summary>
    public static void CleanupOldFiles()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.old", SearchOption.AllDirectories))
            {
                if (!old.Contains(Path.DirectorySeparatorChar + "data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(old);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sada-VoiceType-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
