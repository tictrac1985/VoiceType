using VoiceType.Speech;
using Xunit;

namespace VoiceType.Tests.Speech;

public class VulkanDevicesTests
{
    [Fact]
    public void Picks_the_discrete_gpu_when_the_integrated_one_comes_first()
    {
        var laptop = new[]
        {
            new VulkanDevices.Device(0, "Intel(R) Graphics", false),
            new VulkanDevices.Device(1, "NVIDIA GeForce RTX 5070 Ti Laptop GPU", true),
        };
        Assert.Equal(1, VulkanDevices.Choose(laptop));
    }

    [Fact]
    public void Leaves_the_default_alone_when_it_is_already_right_or_there_is_no_discrete_gpu()
    {
        Assert.Null(VulkanDevices.Choose([new VulkanDevices.Device(0, "AMD Radeon 890M", false)]));
        Assert.Null(VulkanDevices.Choose([new VulkanDevices.Device(0, "RTX", true), new VulkanDevices.Device(1, "Intel", false)]));
        Assert.Null(VulkanDevices.Choose([]));
    }

    [Fact]
    public void Enumerating_never_throws()
    {
        _ = VulkanDevices.All; // على Linux / من غير Vulkan: قائمة فاضية
    }
}
