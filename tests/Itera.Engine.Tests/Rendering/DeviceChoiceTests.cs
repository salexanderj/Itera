using Itera.Engine.Rendering;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// The choices the renderer makes from what the device and surface report, without a device: the
/// swapchain's format, composite alpha, present mode, and extent; which physical device is preferred; and the required
/// features, which device selection checks and device creation enables from one list.
/// </summary>
public sealed class DeviceChoiceTests
{
    [Fact]
    public void An_srgb_surface_format_is_chosen_when_offered_and_the_first_otherwise()
    {
        SurfaceFormatKHR[] offered =
        [
            new(Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            new(Format.R8G8B8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            new(Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
        ];
        SurfaceFormatKHR[] otherSpace =
        [
            new(Format.A2B10G10R10UnormPack32, ColorSpaceKHR.SpaceSrgbNonlinearKhr),
            new(Format.B8G8R8A8Srgb, ColorSpaceKHR.SpaceExtendedSrgbLinearExt),
        ];

        Assert.Equal(Format.B8G8R8A8Srgb, VulkanSwapchain.ChooseSurfaceFormat(offered).Format);
        Assert.Equal(Format.A2B10G10R10UnormPack32, VulkanSwapchain.ChooseSurfaceFormat(otherSpace).Format);
    }

    [Fact]
    public void Composite_alpha_prefers_opaque_then_inherit()
    {
        Assert.Equal(
            CompositeAlphaFlagsKHR.OpaqueBitKhr,
            VulkanSwapchain.ChooseCompositeAlpha(
                CompositeAlphaFlagsKHR.OpaqueBitKhr | CompositeAlphaFlagsKHR.InheritBitKhr
            )
        );
        Assert.Equal(
            CompositeAlphaFlagsKHR.InheritBitKhr,
            VulkanSwapchain.ChooseCompositeAlpha(
                CompositeAlphaFlagsKHR.InheritBitKhr | CompositeAlphaFlagsKHR.PreMultipliedBitKhr
            )
        );
        Assert.Equal(
            CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
            VulkanSwapchain.ChooseCompositeAlpha(CompositeAlphaFlagsKHR.PostMultipliedBitKhr)
        );
    }

    [Fact]
    public void Vsync_is_fifo_and_without_it_mailbox_when_offered()
    {
        PresentModeKHR[] both = [PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr];
        PresentModeKHR[] fifo = [PresentModeKHR.FifoKhr, PresentModeKHR.ImmediateKhr];

        Assert.Equal(PresentModeKHR.FifoKhr, VulkanSwapchain.ChoosePresentMode(both, vsync: true));
        Assert.Equal(PresentModeKHR.MailboxKhr, VulkanSwapchain.ChoosePresentMode(both, vsync: false));
        Assert.Equal(PresentModeKHR.FifoKhr, VulkanSwapchain.ChoosePresentMode(fifo, vsync: false));
    }

    [Fact]
    public void The_extent_is_the_surfaces_or_the_framebuffer_held_within_its_limits()
    {
        var fixedSize = new SurfaceCapabilitiesKHR { CurrentExtent = new Extent2D(800, 600) };
        var appChooses = new SurfaceCapabilitiesKHR
        {
            CurrentExtent = new Extent2D(uint.MaxValue, uint.MaxValue),
            MinImageExtent = new Extent2D(1, 1),
            MaxImageExtent = new Extent2D(4096, 2048),
        };

        Assert.Equal(new Extent2D(800, 600), VulkanSwapchain.ChooseExtent(fixedSize, new Vector2D<int>(1280, 720)));
        Assert.Equal(new Extent2D(1280, 720), VulkanSwapchain.ChooseExtent(appChooses, new Vector2D<int>(1280, 720)));
        Assert.Equal(new Extent2D(4096, 1), VulkanSwapchain.ChooseExtent(appChooses, new Vector2D<int>(9000, -5)));
    }

    [Fact]
    public void A_discrete_gpu_is_preferred_unless_the_option_names_another()
    {
        var discrete = VulkanContext.DeviceScore(PhysicalDeviceType.DiscreteGpu, "AMD Radeon RX 5700 XT", null);
        var integrated = VulkanContext.DeviceScore(PhysicalDeviceType.IntegratedGpu, "AMD Radeon Graphics", null);
        var software = VulkanContext.DeviceScore(PhysicalDeviceType.Cpu, "llvmpipe", null);
        var named = VulkanContext.DeviceScore(
            PhysicalDeviceType.IntegratedGpu,
            "AMD Radeon Graphics",
            "radeon graphics"
        );

        Assert.True(discrete > integrated && integrated > software);
        Assert.True(named > discrete);
        Assert.Equal(discrete, VulkanContext.DeviceScore(PhysicalDeviceType.DiscreteGpu, "AMD Radeon RX 5700 XT", " "));
    }

    [Fact]
    public void Selection_names_each_missing_feature_and_creation_enables_them_all()
    {
        var core = default(PhysicalDeviceFeatures);
        var v12 = new PhysicalDeviceVulkan12Features();
        var v13 = new PhysicalDeviceVulkan13Features();

        Assert.Equal("multiDrawIndirect", DeviceRequirements.FindMissingFeature(core, v12, v13));

        DeviceRequirements.EnableFeatures(ref core, ref v12, ref v13);

        Assert.Null(DeviceRequirements.FindMissingFeature(core, v12, v13));
        var noScalar = v12;
        noScalar.ScalarBlockLayout = false;
        Assert.Equal("scalarBlockLayout", DeviceRequirements.FindMissingFeature(core, noScalar, v13));
        var noDynamic = v13;
        noDynamic.DynamicRendering = false;
        Assert.Equal("dynamicRendering", DeviceRequirements.FindMissingFeature(core, v12, noDynamic));
        var noSync = v13;
        noSync.Synchronization2 = false;
        Assert.Equal("synchronization2", DeviceRequirements.FindMissingFeature(core, v12, noSync));
    }
}
