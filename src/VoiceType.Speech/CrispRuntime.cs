namespace VoiceType.Speech;

/// <summary>
/// محرك CrispASR (MIT، مجاني) اللي بيشغّل نماذج زي Cohere Transcribe Arabic.
/// المكتبة (~29MB، فيها تسريع Vulkan لكروت AMD و NVIDIA و Intel) مش جاية مع البرنامج:
/// بتنزل مرة واحدة مع أول نموذج محتاجها، من الإصدار الرسمي على GitHub، وبنتأكد من بصمتها قبل الاستخدام.
/// البرنامج المساعد VoiceType.CrispHost بيتنزل مع صدى نفسه في نفس المجلد.
/// </summary>
public static class CrispRuntime
{
    public const string Version = "0.8.49";

    public const string PackageUrl =
        "https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.49/crispasr-0.8.49%2Bvulkan-py3-none-win_amd64.whl";

    /// <summary>بصمة SHA-256 للحزمة الرسمية (لو اتغيرت، مش هنستخدمها).</summary>
    public const string PackageSha256 = "e554245a5d2232215a13fd2d201c88e53086e5821e7195b4e7337cae2acf8afb";

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "crispasr");

    public static string LibraryPath => Path.Combine(Directory, OperatingSystem.IsWindows() ? "crispasr.dll" : "libcrispasr.so");

    public static string HostPath => Path.Combine(Directory, OperatingSystem.IsWindows() ? "VoiceType.CrispHost.exe" : "VoiceType.CrispHost");

    public static bool IsInstalled => File.Exists(LibraryPath) && File.Exists(HostPath);

    public static async Task InstallAsync(IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("CrispASR download is available for Windows x64 only.");
        }

        if (!File.Exists(HostPath))
        {
            throw new IOException("البرنامج المساعد VoiceType.CrispHost.exe مش موجود جنب صدى. اعمل build للبرنامج تاني.");
        }

        // من الحزمة: ملفات المكتبة (.dll) والتراخيص بس
        var count = await Downloader.DownloadAndExtractAsync(PackageUrl, "crispasr/", Directory, progress, cancellationToken,
                entry => entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("/licenses/", StringComparison.OrdinalIgnoreCase),
                PackageSha256)
            .ConfigureAwait(false);
        if (count == 0 || !IsInstalled)
        {
            throw new IOException("ملفات CrispASR مش موجودة في الحزمة اللي نزلت.");
        }
    }
}
