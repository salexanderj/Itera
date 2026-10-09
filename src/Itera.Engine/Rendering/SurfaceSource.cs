using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Itera.Engine.Rendering;

/// <summary>
/// Where the Vulkan surface comes from (<see cref="VulkanContext.Initialize"/>): the window's
/// (<see cref="WindowSurfaceSource"/>), or a headless one when the game runs without a window
/// (<see cref="HeadlessSurfaceSource"/>, <c>Engine:Window:Mode=Offscreen</c>). The swapchain and everything drawn into
/// it are the same either way.
/// </summary>
public interface ISurfaceSource
{
    /// <summary>The instance extensions the surface needs.</summary>
    IReadOnlyList<string> InstanceExtensions { get; }

    /// <summary>Makes the surface on <paramref name="instance"/>; the context destroys it.</summary>
    SurfaceKHR Create(Vk vk, Instance instance);
}

/// <summary>The window's surface, through Silk.NET's windowing (GLFW).</summary>
public sealed unsafe class WindowSurfaceSource(IVkSurface surface) : ISurfaceSource
{
    public IReadOnlyList<string> InstanceExtensions
    {
        get
        {
            // GLFW owns the strings; they are copied out.
            var names = surface.GetRequiredExtensions(out var count);
            return SilkMarshal.PtrToStringArray((nint)names, (int)count);
        }
    }

    public SurfaceKHR Create(Vk vk, Instance instance) =>
        surface.Create<AllocationCallbacks>(instance.ToHandle(), null).ToSurface();
}

/// <summary>
/// A surface with no window (<c>VK_EXT_headless_surface</c>): presenting to it shows nothing, and its size is the one
/// the swapchain is given. Mesa's drivers (RADV, llvmpipe) and MoltenVK offer it; a loader without it stops the run with
/// a message naming it.
/// </summary>
public sealed unsafe class HeadlessSurfaceSource : ISurfaceSource
{
    public IReadOnlyList<string> InstanceExtensions { get; } =
    [KhrSurface.ExtensionName, ExtHeadlessSurface.ExtensionName];

    public SurfaceKHR Create(Vk vk, Instance instance)
    {
        ArgumentNullException.ThrowIfNull(vk);
        if (!vk.TryGetInstanceExtension(instance, out ExtHeadlessSurface headless))
        {
            throw new VulkanException($"{ExtHeadlessSurface.ExtensionName} could not be loaded.");
        }

        var info = new HeadlessSurfaceCreateInfoEXT { SType = StructureType.HeadlessSurfaceCreateInfoExt };
        headless
            .CreateHeadlessSurface(instance, &info, null, out var surface)
            .ThrowIfFailed("vkCreateHeadlessSurfaceEXT");
        return surface;
    }
}
