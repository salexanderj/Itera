using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>Swapchain plus its images and image views. Recreated on resize.</summary>
public sealed unsafe partial class VulkanSwapchain : IDisposable
{
    private readonly VulkanContext _context;
    private volatile bool _vsync;
    private readonly ILogger<VulkanSwapchain> _logger;
    private bool _disposed;

    public VulkanSwapchain(VulkanContext context, IOptions<EngineOptions> options, ILogger<VulkanSwapchain> logger)
    {
        _context = context;
        _vsync = options.Value.Window.VSync;
        _logger = logger;
    }

    /// <summary>
    /// Sets the present mode the next <see cref="Initialize"/> or <see cref="Recreate"/> asks for: FIFO with VSync,
    /// mailbox without. Any thread; the render thread reads it when it makes the swapchain again.
    /// </summary>
    public void SetVSync(bool vsync) => _vsync = vsync;

    public SwapchainKHR Swapchain { get; private set; }

    public Format ImageFormat { get; private set; }

    public Extent2D Extent { get; private set; }

    public IReadOnlyList<Image> Images { get; private set; } = [];

    public IReadOnlyList<ImageView> ImageViews { get; private set; } = [];

    /// <summary>True while the window is minimised or otherwise has no drawable area.</summary>
    public bool IsZeroSized => Extent.Width == 0 || Extent.Height == 0;

    /// <summary>
    /// True when the images may be copied from (<c>TransferSrc</c> usage, which the surface need not offer): the
    /// screenshots' copy (<see cref="ScreenshotPass"/>) needs it.
    /// </summary>
    public bool CanCopyImages { get; private set; }

    public void Initialize(Vector2D<int> framebufferSize)
    {
        Create(framebufferSize, default);
    }

    /// <summary>Rebuilds the swapchain for a new framebuffer size. The caller must wait for device idle first.</summary>
    public void Recreate(Vector2D<int> framebufferSize)
    {
        DestroyViews();
        var oldSwapchain = Swapchain;
        Swapchain = default;
        try
        {
            // Passing the old swapchain lets the driver reuse its resources and hand over presentation smoothly.
            Create(framebufferSize, oldSwapchain);
        }
        finally
        {
            if (oldSwapchain.Handle != 0)
            {
                _context.KhrSwapchain.DestroySwapchain(_context.Device, oldSwapchain, null);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_context.IsAbandoned)
        {
            return;
        }

        Destroy();
    }

    private void Create(Vector2D<int> framebufferSize, SwapchainKHR oldSwapchain)
    {
        var vk = _context.Vk;
        var khrSurface = _context.KhrSurface;
        var physicalDevice = _context.PhysicalDevice;
        var surface = _context.Surface;

        khrSurface
            .GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out var capabilities)
            .ThrowIfFailed("vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        uint formatCount = 0;
        khrSurface
            .GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, &formatCount, null)
            .ThrowIfFailed("vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* p = formats)
        {
            khrSurface
                .GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, &formatCount, p)
                .ThrowIfFailed("vkGetPhysicalDeviceSurfaceFormatsKHR");
        }

        uint presentModeCount = 0;
        khrSurface
            .GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, &presentModeCount, null)
            .ThrowIfFailed("vkGetPhysicalDeviceSurfacePresentModesKHR");
        var presentModes = new PresentModeKHR[presentModeCount];
        fixed (PresentModeKHR* p = presentModes)
        {
            khrSurface
                .GetPhysicalDeviceSurfacePresentModes(physicalDevice, surface, &presentModeCount, p)
                .ThrowIfFailed("vkGetPhysicalDeviceSurfacePresentModesKHR");
        }

        var surfaceFormat = ChooseSurfaceFormat(formats);
        // A surface with no sRGB format (a headless one offers UNORM only): its images are drawn into through sRGB views,
        // so the hardware encodes the linear output as it does for a window, and screenshots hold the same colours.
        var viewFormat = surfaceFormat.Format;
        if (
            _context.SupportsMutableSwapchainFormat
            && surfaceFormat.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr
            && SrgbTwin(surfaceFormat.Format) is { } srgb
            && CanDrawInto(vk, physicalDevice, srgb)
        )
        {
            viewFormat = srgb;
        }

        var mutable = viewFormat != surfaceFormat.Format;
        var viewFormats = stackalloc Format[] { surfaceFormat.Format, viewFormat };
        var formatList = new ImageFormatListCreateInfo
        {
            SType = StructureType.ImageFormatListCreateInfo,
            ViewFormatCount = 2,
            PViewFormats = viewFormats,
        };
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            var offered = string.Join(", ", formats.Select(f => $"{f.Format}/{f.ColorSpace}"));
            LogFormatsOffered(offered);
        }

        var presentMode = ChoosePresentMode(presentModes, _vsync);
        var extent = ChooseExtent(capabilities, framebufferSize);

        var imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        var graphicsFamily = _context.GraphicsQueueFamily;
        var presentFamily = _context.PresentQueueFamily;
        var familyIndices = stackalloc uint[] { graphicsFamily, presentFamily };
        var concurrent = graphicsFamily != presentFamily;

        // Copying the images out is for screenshots; a surface that does not offer it still gets a swapchain.
        var copyable = (capabilities.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0;
        var usage = ImageUsageFlags.ColorAttachmentBit | (copyable ? ImageUsageFlags.TransferSrcBit : 0);
        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            PNext = mutable ? &formatList : null,
            Flags = mutable ? SwapchainCreateFlagsKHR.MutableFormatBitKhr : 0,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = usage,
            ImageSharingMode = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = concurrent ? 2u : 0u,
            PQueueFamilyIndices = concurrent ? familyIndices : null,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = oldSwapchain,
        };

        Extent = extent;
        // What the passes draw into and the image views are: the sRGB view of mutable UNORM images.
        ImageFormat = viewFormat;
        CanCopyImages = copyable;

        if (extent.Width == 0 || extent.Height == 0)
        {
            Swapchain = default;
            Images = [];
            ImageViews = [];
            return;
        }

        _context
            .KhrSwapchain.CreateSwapchain(_context.Device, &createInfo, null, out var swapchain)
            .ThrowIfFailed("vkCreateSwapchainKHR");
        Swapchain = swapchain;

        uint actualCount = 0;
        _context
            .KhrSwapchain.GetSwapchainImages(_context.Device, Swapchain, &actualCount, null)
            .ThrowIfFailed("vkGetSwapchainImagesKHR");
        var images = new Image[actualCount];
        fixed (Image* p = images)
        {
            _context
                .KhrSwapchain.GetSwapchainImages(_context.Device, Swapchain, &actualCount, p)
                .ThrowIfFailed("vkGetSwapchainImagesKHR");
        }

        var views = new ImageView[actualCount];
        for (var i = 0; i < actualCount; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = images[i],
                ViewType = ImageViewType.Type2D,
                Format = ImageFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity
                ),
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };

            vk.CreateImageView(_context.Device, &viewInfo, null, out views[i]).ThrowIfFailed("vkCreateImageView");
        }

        Images = images;
        ImageViews = views;

        LogCreated(extent.Width, extent.Height, ImageFormat, presentMode, actualCount);
    }

    private void Destroy()
    {
        DestroyViews();

        if (Swapchain.Handle != 0)
        {
            _context.KhrSwapchain.DestroySwapchain(_context.Device, Swapchain, null);
            Swapchain = default;
        }
    }

    private void DestroyViews()
    {
        var vk = _context.Vk;
        foreach (var view in ImageViews)
        {
            vk.DestroyImageView(_context.Device, view, null);
        }

        ImageViews = [];
        Images = [];
    }

    /// <summary>The sRGB format with the same bytes as a UNORM one, or null.</summary>
    internal static Format? SrgbTwin(Format format) =>
        format switch
        {
            Format.B8G8R8A8Unorm => Format.B8G8R8A8Srgb,
            Format.R8G8B8A8Unorm => Format.R8G8B8A8Srgb,
            Format.A8B8G8R8UnormPack32 => Format.A8B8G8R8SrgbPack32,
            _ => null,
        };

    /// <summary>True when <paramref name="format"/> can be a colour attachment with optimal tiling.</summary>
    private static bool CanDrawInto(Vk vk, PhysicalDevice device, Format format)
    {
        vk.GetPhysicalDeviceFormatProperties(device, format, out var properties);
        return (properties.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBit) != 0;
    }

    /// <summary>An sRGB format in the sRGB colour space when the surface offers one; otherwise its first format.</summary>
    internal static SurfaceFormatKHR ChooseSurfaceFormat(SurfaceFormatKHR[] formats)
    {
        // An sRGB format makes the hardware encode linear shader output for the display.
        foreach (var preferred in (ReadOnlySpan<Format>)[Format.B8G8R8A8Srgb, Format.R8G8B8A8Srgb])
        {
            foreach (var format in formats)
            {
                if (format.Format == preferred && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }
        }

        return formats[0];
    }

    /// <summary>Opaque when supported, then inherit, pre-multiplied, and post-multiplied.</summary>
    internal static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
    {
        // The spec guarantees at least one bit. Opaque is preferred; the others still show an opaque clear.
        ReadOnlySpan<CompositeAlphaFlagsKHR> order =
        [
            CompositeAlphaFlagsKHR.OpaqueBitKhr,
            CompositeAlphaFlagsKHR.InheritBitKhr,
            CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
            CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
        ];
        foreach (var mode in order)
        {
            if ((supported & mode) != 0)
            {
                return mode;
            }
        }

        return CompositeAlphaFlagsKHR.OpaqueBitKhr;
    }

    /// <summary>FIFO with VSync; without it, mailbox when offered, else FIFO (every surface offers FIFO).</summary>
    internal static PresentModeKHR ChoosePresentMode(PresentModeKHR[] modes, bool vsync)
    {
        if (vsync)
        {
            return PresentModeKHR.FifoKhr;
        }

        return modes.Contains(PresentModeKHR.MailboxKhr) ? PresentModeKHR.MailboxKhr : PresentModeKHR.FifoKhr;
    }

    /// <summary>
    /// The surface's current extent, or, when it leaves the choice to the app (a width of <c>uint.MaxValue</c>), the
    /// framebuffer's size held within the surface's limits.
    /// </summary>
    internal static Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities, Vector2D<int> framebufferSize)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        var width = Math.Clamp(
            (uint)Math.Max(0, framebufferSize.X),
            capabilities.MinImageExtent.Width,
            capabilities.MaxImageExtent.Width
        );
        var height = Math.Clamp(
            (uint)Math.Max(0, framebufferSize.Y),
            capabilities.MinImageExtent.Height,
            capabilities.MaxImageExtent.Height
        );
        return new Extent2D(width, height);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Swapchain: the surface offers {Formats}")]
    private partial void LogFormatsOffered(string formats);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Swapchain: {Width}x{Height} {Format} {PresentMode}, {ImageCount} images"
    )]
    private partial void LogCreated(
        uint width,
        uint height,
        Format format,
        PresentModeKHR presentMode,
        uint imageCount
    );
}
