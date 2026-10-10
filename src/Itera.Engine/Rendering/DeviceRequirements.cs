using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Itera.Engine.Rendering;

/// <summary>
/// What Itera needs from a Vulkan device: the API version, the device extensions, and the features. Device
/// selection checks these and device creation enables them, both through this class, so a new requirement is
/// added here and nowhere else.
/// </summary>
internal static class DeviceRequirements
{
    /// <summary>Lowest instance and device API version. Dynamic rendering and synchronization2 are core in it.</summary>
    public static readonly uint MinimumApiVersion = Vk.Version13;

    /// <summary>Device extensions every selected device must support and that device creation enables.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [KhrSwapchain.ExtensionName];

    /// <summary>
    /// The format of the depth image the world's pass, the particles, and the lines share (<c>FrameRenderer</c>): 32-bit float, for reverse
    /// depth (0.01 m over the distance) out to the view distance.
    /// </summary>
    public const Format DepthFormat = Format.D32Sfloat;

    /// <summary>
    /// Image formats a selected device must support with optimal tiling, the features each needs, and its use.
    /// </summary>
    public static IReadOnlyList<(Format Format, FormatFeatureFlags Features, string Use)> Formats { get; } =
    [(DepthFormat, FormatFeatureFlags.DepthStencilAttachmentBit, "D32_SFLOAT depth attachment")];

    /// <summary>Returns the name of the first required feature the device lacks, or null when it has them all.</summary>
    public static string? FindMissingFeature(
        PhysicalDeviceFeatures core,
        PhysicalDeviceVulkan12Features v12,
        PhysicalDeviceVulkan13Features v13
    )
    {
        return VisitFeatures(ref core, ref v12, ref v13, enable: false);
    }

    /// <summary>Sets every required feature in structs that are passed to <c>vkCreateDevice</c>.</summary>
    public static void EnableFeatures(
        ref PhysicalDeviceFeatures core,
        ref PhysicalDeviceVulkan12Features v12,
        ref PhysicalDeviceVulkan13Features v13
    )
    {
        VisitFeatures(ref core, ref v12, ref v13, enable: true);
    }

    /// <summary>
    /// The one list of required features. With <paramref name="enable"/> false it returns the first feature the
    /// structs lack; with true it sets each one. Selection and creation share the list, so they cannot disagree.
    /// </summary>
    private static string? VisitFeatures(
        ref PhysicalDeviceFeatures core,
        ref PhysicalDeviceVulkan12Features v12,
        ref PhysicalDeviceVulkan13Features v13,
        bool enable
    )
    {
        string? missing = null;

        // The raster pipeline draws every block of the world with one indexed indirect draw (MeshPass), each command's
        // first instance naming its block's MeshDraw (RADV reports both; MoltenVK on GPUs with base-instance drawing).
        Feature(ref core.MultiDrawIndirect, "multiDrawIndirect");
        Feature(ref core.DrawIndirectFirstInstance, "drawIndirectFirstInstance");
        // The materials' texture sets are block-compressed (BC4, BC5, BC7; 99.6 % of Windows, 97.9 % of Linux, and every
        // macOS Vulkan device on gpuinfo.org, 2026-10-08).
        Feature(ref core.TextureCompressionBC, "textureCompressionBC");
        // Buffers and push constants that mirror C# structs use layout(scalar) (ecs-vulkan skill).
        Feature(ref v12.ScalarBlockLayout, "scalarBlockLayout");
        // The mesh assets' textures are one array the fragment shader indexes by the draw's material
        // (mesh-asset-data.glsl), which may differ between the draws of one indirect call: nonuniformEXT (RADV and
        // MoltenVK 1.4 on macOS report both).
        Feature(ref core.ShaderSampledImageArrayDynamicIndexing, "shaderSampledImageArrayDynamicIndexing");
        Feature(ref v12.ShaderSampledImageArrayNonUniformIndexing, "shaderSampledImageArrayNonUniformIndexing");
        // Rendering without render pass and framebuffer objects.
        Feature(ref v13.DynamicRendering, "dynamicRendering");
        // CmdPipelineBarrier2 and QueueSubmit2.
        Feature(ref v13.Synchronization2, "synchronization2");

        return missing;

        void Feature(ref Bool32 value, string name)
        {
            if (enable)
            {
                value = true;
            }
            else if (missing is null && !value)
            {
                missing = name;
            }
        }
    }
}
