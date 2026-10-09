using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Memory;

/// <summary>
/// A 2D Vulkan image with optimal tiling, its one view, and the memory bound to it, made by
/// <see cref="GpuMemoryAllocator.CreateImage"/>. Device-local only: the CPU never reads or writes it.
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    private readonly GpuMemoryAllocator _allocator;
    private readonly VulkanContext _context;
    private readonly GpuAllocation _allocation;
    private bool _disposed;

    internal GpuImage(
        GpuMemoryAllocator allocator,
        VulkanContext context,
        Image image,
        ImageView view,
        Format format,
        Extent2D extent,
        GpuAllocation allocation,
        string name,
        uint mips = 1,
        uint layers = 1
    )
    {
        _allocator = allocator;
        _context = context;
        Image = image;
        View = view;
        Format = format;
        Extent = extent;
        _allocation = allocation;
        Name = name;
        Mips = mips;
        Layers = layers;
    }

    /// <summary>The image handle.</summary>
    public Image Image { get; private set; }

    /// <summary>A view of the whole image, of the aspect it was created for.</summary>
    public ImageView View { get; private set; }

    /// <summary>The image's format.</summary>
    public Format Format { get; }

    /// <summary>The image's size in pixels.</summary>
    public Extent2D Extent { get; }

    /// <summary>The name given at creation, for messages.</summary>
    public string Name { get; }

    /// <summary>The image's mip levels.</summary>
    public uint Mips { get; }

    /// <summary>The image's array layers (1 for a 3D image).</summary>
    public uint Layers { get; }

    /// <summary>The memory type the image lives in.</summary>
    public int MemoryTypeIndex => _allocation.TypeIndex;

    /// <summary>
    /// Makes another view of the whole colour image in <paramref name="format"/>: the image's own format, or the one
    /// <see cref="GpuMemoryAllocator.CreateImage"/> was told it is also viewed as (the UNORM twin of an sRGB format).
    /// The caller destroys it before the image.
    /// </summary>
    public ImageView CreateView(Format format, string name)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _context.Vk.CreateImageView(_context.Device, &info, null, out var view).ThrowIfFailed("vkCreateImageView");
        _context.SetObjectName(ObjectType.ImageView, view.Handle, name);
        return view;
    }

    /// <summary>
    /// A 2D view of layer <paramref name="layer"/> alone, of <paramref name="aspect"/> (a cascade of a shadow map array
    /// to draw into). The caller destroys it before the image.
    /// </summary>
    public ImageView CreateLayerView(uint layer, ImageAspectFlags aspect, string name)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2D,
            Format = Format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, layer, 1),
        };
        _context.Vk.CreateImageView(_context.Device, &info, null, out var view).ThrowIfFailed("vkCreateImageView");
        _context.SetObjectName(ObjectType.ImageView, view.Handle, name);
        return view;
    }

    /// <summary>
    /// A 2D array view of every layer of mip level <paramref name="mip"/> alone, of <paramref name="aspect"/> (a level
    /// to write as a storage image). The caller destroys it before the image.
    /// </summary>
    public ImageView CreateMipView(uint mip, ImageAspectFlags aspect, string name)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2DArray,
            Format = Format,
            SubresourceRange = new ImageSubresourceRange(aspect, mip, 1, 0, Layers),
        };
        _context.Vk.CreateImageView(_context.Device, &info, null, out var view).ThrowIfFailed("vkCreateImageView");
        _context.SetObjectName(ObjectType.ImageView, view.Handle, name);
        return view;
    }

    /// <summary>
    /// Destroys the view and the image, then returns their memory. The GPU must be done with them. Does nothing to
    /// Vulkan after <see cref="VulkanContext.Abandon"/>. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_context.IsAbandoned)
        {
            if (View.Handle != 0)
            {
                _context.Vk.DestroyImageView(_context.Device, View, null);
            }

            if (Image.Handle != 0)
            {
                _context.Vk.DestroyImage(_context.Device, Image, null);
            }
        }

        View = default;
        Image = default;
        _allocator.Free(_allocation);
    }
}
