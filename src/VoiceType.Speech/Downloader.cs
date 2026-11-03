using System.IO.Compression;

namespace VoiceType.Speech;

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value : null;

    public override string ToString() =>
        TotalBytes is > 0
            ? $"{BytesReceived / 1048576.0:0} / {TotalBytes.Value / 1048576.0:0} MB"
            : $"{BytesReceived / 1048576.0:0} MB";
}

/// <summary>تنزيل النماذج وملفات تسريع كرت الشاشة، مع شريط تقدم وإمكانية الإلغاء.</summary>
public static class Downloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static Downloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("VoiceType/0.1");
    }

    /// <summary>ينزّل ملفًا إلى مكانه النهائي. يُكتب أولًا كملف .part حتى لا يبقى ملف ناقص عند الانقطاع.</summary>
    public static async Task DownloadFileAsync(
        string url,
        string destination,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + ".part";

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long received = 0;
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                    {
                        progress?.Report(new DownloadProgress(received, total));
                        lastReport = DateTime.UtcNow;
                    }
                }

                progress?.Report(new DownloadProgress(received, total));
                if (total is > 0 && received != total)
                {
                    throw new IOException("انقطع التنزيل قبل اكتماله. حاول مرة أخرى.");
                }
            }

            File.Move(partial, destination, overwrite: true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>ينزّل حزمة NuGet ويستخرج منها الملفات التي تبدأ بمسار معين.</summary>
    public static async Task<int> DownloadAndExtractAsync(
        string packageUrl,
        string entryPrefix,
        string targetDirectory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<ZipArchiveEntry, bool>? include = null,
        string? expectedSha256 = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"voicetype-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadFileAsync(packageUrl, temp, progress, cancellationToken).ConfigureAwait(false);
            if (expectedSha256 is not null)
            {
                await using var stream = File.OpenRead(temp);
                var hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("الملف اللي نزل مش مطابق للنسخة الرسمية (البصمة مختلفة)، فمش هنستخدمه.");
                }
            }

            Directory.CreateDirectory(targetDirectory);
            var count = 0;
            using var archive = ZipFile.OpenRead(temp);
            foreach (var entry in archive.Entries.Where(e =>
                         e.FullName.StartsWith(entryPrefix, StringComparison.OrdinalIgnoreCase) && e.Name.Length > 0 && (include?.Invoke(e) ?? true)))
            {
                entry.ExtractToFile(Path.Combine(targetDirectory, entry.Name), overwrite: true);
                count++;
            }

            return count;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
