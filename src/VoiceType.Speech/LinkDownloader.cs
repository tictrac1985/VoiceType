using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VoiceType.Settings;

namespace VoiceType.Speech;

/// <summary>
/// تفريغ لينك (يوتيوب، فيسبوك، X، فيميو، تيك توك، إنستجرام…): بننزّل الصوت بأداة yt-dlp (مجانية ومفتوحة المصدر)،
/// وبعدين نفرّغه زي أي ملف. الأداة بتنزل مرة واحدة من إصدارها الرسمي على GitHub، وبنتأكد من بصمتها،
/// وبتشتغل مخفية من غير أي نافذة.
/// </summary>
public static partial class LinkDownloader
{
    private const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    public static string ToolPath => Path.Combine(AppPaths.Root, "tools", OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    public static string DownloadFolder => Path.Combine(AppPaths.Root, "downloads");

    public static bool IsInstalled => File.Exists(ToolPath);

    /// <summary>
    /// يوتيوب والمواقع بتغيّر طريقتها كل كام أسبوع، والنسخ القديمة من yt-dlp بتبطل تشتغل.
    /// فلو الأداة عدّى عليها أكتر من أسبوعين، بننزّل آخر نسخة قبل الاستخدام.
    /// </summary>
    public static bool NeedsRefresh => !IsInstalled || DateTime.UtcNow - File.GetLastWriteTimeUtc(ToolPath) > TimeSpan.FromDays(14);

    /// <summary>بيمسح الأصوات اللي اتنزلت من أكتر من أسبوع، وأي تنزيل ناقص (.part).</summary>
    public static void CleanupDownloads()
    {
        if (!Directory.Exists(DownloadFolder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(DownloadFolder))
        {
            try
            {
                var old = DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7);
                var partial = file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase);
                if (old || (partial && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1)))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>لينك ويب صالح؟ (http/https بس)</summary>
    public static bool IsValidLink(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.Host.Contains('.');

    /// <summary>ينزّل yt-dlp من الإصدار الرسمي ويتأكد من بصمته (SHA2-256SUMS من نفس الإصدار).</summary>
    public static async Task InstallAsync(IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("yt-dlp download is set up for Windows only.");
        }

        var name = Path.GetFileName(ToolPath);
        var sums = Path.Combine(Path.GetTempPath(), $"voicetype-ytdlp-{Guid.NewGuid():N}.txt");
        try
        {
            await Downloader.DownloadFileAsync(ReleaseBase + "SHA2-256SUMS", sums, null, cancellationToken).ConfigureAwait(false);
            var expected = ExpectedHash(await File.ReadAllTextAsync(sums, cancellationToken).ConfigureAwait(false), name)
                           ?? throw new IOException("مش لاقي بصمة yt-dlp في الإصدار الرسمي.");
            await Downloader.DownloadFileAsync(ReleaseBase + name, ToolPath, progress, cancellationToken, expected).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(sums);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>البصمة المكتوبة للملف في SHA2-256SUMS (سطر: «البصمة  اسم-الملف»).</summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.Ordinal) && parts[0].Length == 64)
            {
                return parts[0].ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>
    /// ينزّل صوت اللينك ويرجّع مسار الملف. بنفضّل m4a (بيتقري على أي ويندوز من غير برامج زيادة)،
    /// ولو مش متاح ناخد mp4. فيديو واحد بس (من غير قوائم التشغيل).
    /// </summary>
    public static async Task<string> DownloadAudioAsync(string link, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsValidLink(link))
        {
            throw new InvalidOperationException("ده مش لينك صحيح. الصق لينك يبدأ بـ https://");
        }

        if (!IsInstalled)
        {
            throw new InvalidOperationException("أداة تنزيل اللينكات مش متسطّبة.");
        }

        Directory.CreateDirectory(DownloadFolder);
        var start = new ProcessStartInfo(ToolPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = DownloadFolder,
        };
        foreach (var arg in new[]
                 {
                     // --ignore-config: أي ملف إعدادات لـ yt-dlp على الجهاز (ممكن يشغّل أوامر) مبيتقريش
                     // --progress: من غيرها --print بيخفي نسبة التنزيل
                     "--ignore-config", "--progress", "--no-playlist", "--newline", "--no-colors", "--windows-filenames", "--no-mtime",
                     "--encoding", "utf-8",
                     "-f", "bestaudio[ext=m4a]/bestaudio[ext=mp4]/best[ext=mp4]/bestaudio/best",
                     "-o", Path.Combine(DownloadFolder, "%(title).80s [%(id)s].%(ext)s"),
                     "--print", "after_move:filepath",
                     link.Trim(),
                 })
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["PYTHONIOENCODING"] = "utf-8";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("تعذّر تشغيل أداة التنزيل.");
        var errors = new StringBuilder();
        string? file = null;
        var readErrors = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                errors.AppendLine(line);
            }
        }, cancellationToken);

        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                if (Percent().Match(line) is { Success: true } match &&
                    double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    progress?.Report(Math.Clamp(percent / 100, 0, 1));
                }
                else if (!line.StartsWith('[') && File.Exists(line.Trim()))
                {
                    file = line.Trim();
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await readErrors.ConfigureAwait(false);
        }
        finally
        {
            // إلغاء أو أي خطأ: الأداة متفضلش شغالة في الخلفية
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        if (!process.HasExited || process.ExitCode != 0 || file is null)
        {
            var message = errors.ToString().Split('\n').LastOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase))?.Trim();
            throw new InvalidOperationException("تعذّر تنزيل الصوت من اللينك. " + (message ?? "اتأكد إن اللينك شغال وعام."));
        }

        return file;
    }

    [GeneratedRegex(@"\[download\]\s+([\d.]+)%")]
    private static partial Regex Percent();
}
