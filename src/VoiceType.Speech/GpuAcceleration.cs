using System.Runtime.InteropServices;

namespace VoiceType.Speech;

/// <summary>
/// تسريع التفريغ المحلي بكرت الشاشة عبر Vulkan (يعمل مع NVIDIA وAMD وIntel).
/// ملفات التسريع كبيرة (~37MB مضغوطة)، لذلك لا تأتي مع البرنامج وتُنزّل بزر واحد عند الحاجة
/// إلى مجلد البرنامج نفسه (runtimes\vulkan\win-x64)، وهو المكان الذي يبحث فيه Whisper.net تلقائيًا.
/// </summary>
public static class GpuAcceleration
{
    public static string RuntimeVersion
    {
        get
        {
            var version = typeof(Whisper.net.WhisperFactory).Assembly.GetName().Version ?? new Version(1, 9, 1);
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string PackageUrl =>
        $"https://api.nuget.org/v3-flatcontainer/whisper.net.runtime.vulkan/{RuntimeVersion}/whisper.net.runtime.vulkan.{RuntimeVersion}.nupkg";

    public static string TargetDirectory => Path.Combine(AppContext.BaseDirectory, "runtimes", "vulkan", "win-x64");

    public static bool IsSupportedPlatform =>
        OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>هل تعريف كرت الشاشة يدعم Vulkan؟ (ملف vulkan-1.dll يأتي مع تعريفات NVIDIA/AMD/Intel)</summary>
    public static bool HasVulkanDriver =>
        OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll"));

    public static bool IsInstalled =>
        File.Exists(Path.Combine(TargetDirectory, "whisper.dll")) &&
        File.Exists(Path.Combine(TargetDirectory, "ggml-vulkan-whisper.dll"));

    public static async Task InstallAsync(IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedPlatform)
        {
            throw new PlatformNotSupportedException("GPU acceleration download is available for Windows x64 only.");
        }

        var count = await Downloader.DownloadAndExtractAsync(PackageUrl, "build/win-x64/", TargetDirectory, progress, cancellationToken)
            .ConfigureAwait(false);
        if (count == 0 || !IsInstalled)
        {
            throw new IOException("لم يُعثر على ملفات التسريع داخل الحزمة المنزّلة.");
        }
    }

    public static void Uninstall()
    {
        if (Directory.Exists(TargetDirectory))
        {
            Directory.Delete(TargetDirectory, recursive: true);
        }
    }

    /// <summary>وصف الحالة للمستخدم.</summary>
    public static string Describe()
    {
        var loaded = WhisperLocalEngine.LoadedRuntime;
        if (loaded is not null)
        {
            return loaded.Contains("Cpu", StringComparison.OrdinalIgnoreCase)
                ? "يعمل الآن على المعالج (CPU)."
                : $"يعمل الآن على كرت الشاشة ({loaded}).";
        }

        if (IsInstalled)
        {
            return HasVulkanDriver
                ? "تسريع كرت الشاشة مثبت وسيُستخدم تلقائيًا."
                : "تسريع كرت الشاشة مثبت، لكن تعريف كرت الشاشة لا يدعم Vulkan، فسيُستخدم المعالج.";
        }

        return "يعمل على المعالج (CPU). يمكنك تفعيل تسريع كرت الشاشة لسرعة أكبر.";
    }
}
