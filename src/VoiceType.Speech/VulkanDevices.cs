using System.Runtime.InteropServices;
using System.Text;

namespace VoiceType.Speech;

/// <summary>
/// كروت الشاشة اللي Vulkan شايفها، بنفس ترتيب مكتبات ggml (Whisper و CrispASR).
/// ليه؟ اللاب فيه كرتين: Intel المدمج رقم 0، و NVIDIA RTX رقم 1. والمكتبات بتاخد رقم 0 لوحدها،
/// فالتفريغ كان بيشتغل على الكرت الضعيف (Cohere: 16.8 ثانية بدل 4).
/// فبنختار أول كرت منفصل (discrete) لو موجود. على جهاز فيه كرت واحد مبيتغيرش حاجة.
/// </summary>
public static class VulkanDevices
{
    public sealed record Device(int Index, string Name, bool Discrete);

    private static readonly Lazy<IReadOnlyList<Device>> Cached = new(Enumerate);

    public static IReadOnlyList<Device> All => Cached.Value;

    /// <summary>رقم الكرت المنفصل لو مش هو رقم 0 (يعني محتاجين نختاره بنفسنا)، وإلا null.</summary>
    public static int? PreferredIndex => Choose(All);

    public static int? Choose(IReadOnlyList<Device> devices) =>
        devices.FirstOrDefault(d => d.Discrete) is { Index: > 0 } discrete ? discrete.Index : null;

    private static IReadOnlyList<Device> Enumerate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return EnumerateCore();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return []; // مفيش تعريف Vulkan: المكتبات هتشتغل زي ما هي
        }
    }

    private static IReadOnlyList<Device> EnumerateCore()
    {
        var info = new InstanceCreateInfo { SType = 1 }; // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
        if (vkCreateInstance(ref info, 0, out var instance) != 0 || instance == 0)
        {
            return [];
        }

        try
        {
            uint count = 0;
            if (vkEnumeratePhysicalDevices(instance, ref count, null) != 0 || count == 0)
            {
                return [];
            }

            var handles = new nint[count];
            if (vkEnumeratePhysicalDevices(instance, ref count, handles) != 0)
            {
                return [];
            }

            var devices = new List<Device>();
            var properties = new byte[1024]; // VkPhysicalDeviceProperties = 824 بايت
            for (var i = 0; i < count; i++)
            {
                Array.Clear(properties);
                vkGetPhysicalDeviceProperties(handles[i], properties);

                var type = BitConverter.ToUInt32(properties, 16); // deviceType: 1 = مدمج، 2 = منفصل
                var nameBytes = properties.AsSpan(20, 256);
                var end = nameBytes.IndexOf((byte)0);
                var name = Encoding.UTF8.GetString(end >= 0 ? nameBytes[..end] : nameBytes);
                devices.Add(new Device(i, name, type == 2));
            }

            return devices;
        }
        finally
        {
            vkDestroyInstance(instance, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceCreateInfo
    {
        public uint SType;
        public nint Next;
        public uint Flags;
        public nint ApplicationInfo;
        public uint EnabledLayerCount;
        public nint EnabledLayerNames;
        public uint EnabledExtensionCount;
        public nint EnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll")]
    private static extern int vkCreateInstance(ref InstanceCreateInfo createInfo, nint allocator, out nint instance);

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumeratePhysicalDevices(nint instance, ref uint count, [Out] nint[]? devices);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceProperties(nint physicalDevice, [Out] byte[] properties);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(nint instance, nint allocator);
}
