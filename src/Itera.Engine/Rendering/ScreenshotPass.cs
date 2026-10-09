using System.Diagnostics;
using Itera.Engine.Rendering.Memory;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// Copies the swapchain image of a frame that takes a screenshot (<see cref="RenderFrame.Screenshot"/>) into a readback
/// buffer, reads it after the slot's fence (<see cref="Collect"/>), and hands it to a worker that writes the PNG
/// (<see cref="ScreenshotEncoder"/>). One screenshot is on its way at a time (<see cref="Screenshots"/> refuses a second),
/// so one buffer serves, grown when the screen is larger. Render thread, except the worker.
/// </summary>
public sealed unsafe partial class ScreenshotPass : IDisposable
{
    private static readonly ImageSubresourceRange ColorRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly GpuMemoryAllocator _memory;
    private readonly ILogger<ScreenshotPass> _logger;
    private readonly Copy[] _copies = new Copy[FrameSync.FramesInFlight];
    private GpuBuffer? _buffer;
    private bool _disposed;

    public ScreenshotPass(
        VulkanContext context,
        VulkanSwapchain swapchain,
        GpuMemoryAllocator memory,
        ILogger<ScreenshotPass> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _memory = memory;
        _logger = logger;
    }

    /// <summary>
    /// Records the copy of <paramref name="image"/>, in ColorAttachmentOptimal after the passes that drew it, and leaves
    /// it in <paramref name="after"/> (PresentSrcKhr at the frame's end, or ColorAttachmentOptimal for the UI to draw
    /// over). Returns false, having failed the request and recorded nothing, when the swapchain cannot be copied from or
    /// its format is not one of 8 bits a channel; the caller then moves the image on itself.
    /// </summary>
    public bool Record(CommandBuffer commandBuffer, Image image, int slot, ScreenshotRequest request, ImageLayout after)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = _swapchain.ImageFormat;
        if (!_swapchain.CanCopyImages)
        {
            request.Fail("the window's surface does not let its images be copied");
            return false;
        }

        if (!TryBlueFirst(format, out var blueFirst))
        {
            request.Fail($"the swapchain's format {format} is not one a screenshot reads");
            return false;
        }

        var extent = _swapchain.Extent;
        var size = (ulong)extent.Width * extent.Height * 4;
        if (_buffer is null || _buffer.Size < size)
        {
            // Only one screenshot is on its way, and it was not recorded yet: nothing uses the old buffer.
            _buffer?.Dispose();
            _buffer = _memory.CreateBuffer(
                size,
                BufferUsageFlags.TransferDstBit,
                MemoryUsage.Readback,
                "screenshot readback"
            );
        }

        var vk = _context.Vk;
        Transition(
            commandBuffer,
            image,
            ImageLayout.ColorAttachmentOptimal,
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.TransferReadBit
        );
        var region = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = default,
            ImageExtent = new Extent3D(extent.Width, extent.Height, 1),
        };
        vk.CmdCopyImageToBuffer(commandBuffer, image, ImageLayout.TransferSrcOptimal, _buffer.Buffer, 1, &region);

        // Back for the UI to draw over, or on to presentation: the frame's semaphore signals at the colour output stage,
        // which this barrier's second scope chains into, as the frame's own transition to PresentSrcKhr does.
        var toUi = after == ImageLayout.ColorAttachmentOptimal;
        Transition(
            commandBuffer,
            image,
            ImageLayout.TransferSrcOptimal,
            after,
            PipelineStageFlags2.CopyBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            toUi ? AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit : AccessFlags2.None
        );

        // The copy's writes, for the host to read after the slot's fence.
        var memoryBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.CopyBit,
            SrcAccessMask = AccessFlags2.TransferWriteBit,
            DstStageMask = PipelineStageFlags2.HostBit,
            DstAccessMask = AccessFlags2.HostReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &memoryBarrier,
        };
        vk.CmdPipelineBarrier2(commandBuffer, &dependency);

        _copies[slot] = new Copy(request, (int)extent.Width, (int)extent.Height, blueFirst, Stopwatch.GetTimestamp());
        return true;
    }

    /// <summary>
    /// Reads the copy recorded in <paramref name="slot"/>, if any, and starts writing its file on a worker. Render thread,
    /// right after the slot's fence wait.
    /// </summary>
    public void Collect(int slot)
    {
        if (_copies[slot].Request is not { } request || _buffer is null)
        {
            return;
        }

        var copy = _copies[slot];
        _copies[slot] = default;
        var size = copy.Width * copy.Height * 4;
        _buffer.Invalidate(0, (ulong)size);
        var pixels = new byte[size];
        new ReadOnlySpan<byte>(_buffer.Mapped, size).CopyTo(pixels);
        StartWrite(request, pixels, copy);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _copies.Length; i++)
        {
            _copies[i].Request?.Fail("the game stopped before the screenshot was read");
            _copies[i] = default;
        }

        _buffer?.Dispose();
        _buffer = null;
    }

    /// <summary>True for the formats a screenshot reads, with the order of their channels.</summary>
    internal static bool TryBlueFirst(Format format, out bool blueFirst)
    {
        switch (format)
        {
            case Format.B8G8R8A8Srgb:
            case Format.B8G8R8A8Unorm:
                blueFirst = true;
                return true;
            case Format.R8G8B8A8Srgb:
            case Format.R8G8B8A8Unorm:
                blueFirst = false;
                return true;
            default:
                blueFirst = false;
                return false;
        }
    }

    /// <summary>
    /// Writes the file on the thread pool. The closure lives here, not in <see cref="Collect"/>: C# makes a closure on
    /// entry to the scope of the variables it holds, there the whole method, so every frame allocated 72 bytes on the
    /// render thread, with or without a screenshot (found 2026-10-02).
    /// </summary>
    private void StartWrite(ScreenshotRequest request, byte[] pixels, Copy copy) =>
        _ = Task.Run(() => Write(request, pixels, copy));

    private void Write(ScreenshotRequest request, byte[] pixels, Copy copy)
    {
        try
        {
            var (width, height) = ScreenshotEncoder.Write(pixels, copy.Width, copy.Height, copy.BlueFirst, request);
            request.Succeed(width, height, copy.Width, copy.Height);
            var ms = Stopwatch.GetElapsedTime(copy.Recorded).TotalMilliseconds;
            LogWritten(request.Path, width, height, ms);
        }
        catch (Exception exception)
        {
            // Any failure fails the request: one left unanswered held back every later screenshot.
            request.Fail(exception.Message);
            LogFailed(request.Path, exception.Message);
        }
    }

    private void Transition(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        PipelineStageFlags2 srcStage,
        AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage,
        AccessFlags2 dstAccess
    )
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = ColorRange,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screenshot: wrote {Path} ({Width}x{Height}) in {Ms:F0} ms")]
    private partial void LogWritten(string path, int width, int height, double ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Screenshot: {Path} was not written: {Reason}")]
    private partial void LogFailed(string path, string reason);

    private readonly record struct Copy(
        ScreenshotRequest? Request,
        int Width,
        int Height,
        bool BlueFirst,
        long Recorded
    );
}
