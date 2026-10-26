using System.Diagnostics;
using Itera.Engine.Diagnostics;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Settings;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace Itera.Engine.Rendering;

/// <summary>
/// Draws frames on the render thread: waits on the frame slot's fence, acquires a swapchain image, records the passes,
/// submits, and presents. <see cref="RenderThread"/> calls <see cref="BeginFrame"/>, <see cref="TryAcquire"/>, and
/// <see cref="Present"/> in that order, and times their CPU work (<see cref="FrameCpuTimings"/>): the fence wait and
/// acquire, the recording and submit, and the present. Apart from <see cref="SetFramebufferSize"/> and the resize
/// handler, every member is for the render thread only: after the render thread starts, it alone uses the queues, the swapchain, and
/// <see cref="FrameSync"/>.
/// </summary>
public sealed unsafe partial class FrameRenderer : IFrameRenderer
{
    private static readonly ImageSubresourceRange ColorRange = new(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly FrameSync _sync;
    private readonly MeshPass _meshPass;
    private readonly MeshAssetPass _assetPass;
    private readonly WorldMeshes _worldMeshes;
    private readonly DebugLinePass _linePass;
    private readonly UiPass _uiPass;
    private readonly UpscalePass _upscalePass;
    private readonly SmaaPass _smaaPass;
    private readonly LoadingPass _loadingPass;
    private readonly ParticlePass _particlePass;
    private readonly ScreenshotPass _screenshotPass;
    private readonly RenderPasses _renderPasses;
    private readonly RenderViewPass _viewPass;
    private readonly DepthTarget _depth;
    private readonly GpuTimer _timer;
    private readonly FrameCpuTimings _cpu;
    private readonly ILogger<FrameRenderer> _logger;
    private readonly object _resizeLock = new();

    private readonly SwapchainState _state = new();

    /// <summary>Set by each resize, cleared by <see cref="WaitForResize"/>; guarded by <see cref="_resizeLock"/>.</summary>
    private bool _resizeSignalled;
    private bool _frameBegun;
    private uint _acquiredImage = uint.MaxValue;
    private Result _acquireResult;

    /// <summary>When <see cref="BeginFrame"/> began its fence wait, for the time to the acquire's end.</summary>
    private long _fenceWaitStart;

    public FrameRenderer(
        VulkanContext context,
        VulkanSwapchain swapchain,
        FrameSync sync,
        WindowService window,
        MeshPass meshPass,
        MeshAssetPass assetPass,
        WorldMeshes worldMeshes,
        DebugLinePass linePass,
        UiPass uiPass,
        UpscalePass upscalePass,
        SmaaPass smaaPass,
        LoadingPass loadingPass,
        ParticlePass particlePass,
        ScreenshotPass screenshotPass,
        RenderPasses renderPasses,
        RenderViewPass viewPass,
        DepthTarget depth,
        GpuTimer timer,
        FrameCpuTimings cpu,
        PlayerSettings settings,
        ILogger<FrameRenderer> logger
    )
    {
        _context = context;
        _swapchain = swapchain;
        _sync = sync;
        _meshPass = meshPass;
        _assetPass = assetPass;
        _worldMeshes = worldMeshes;
        _linePass = linePass;
        _uiPass = uiPass;
        _upscalePass = upscalePass;
        _smaaPass = smaaPass;
        _loadingPass = loadingPass;
        _particlePass = particlePass;
        _screenshotPass = screenshotPass;
        _renderPasses = renderPasses;
        _viewPass = viewPass;
        _depth = depth;
        _timer = timer;
        _cpu = cpu;
        _logger = logger;
        window.FramebufferResized += OnFramebufferResized;
        settings.PresentModeChanged += () => OnPresentModeChanged(settings.VSync);
        settings.SamplesChanged += () => OnSamplesChanged(settings.MsaaSamples);
    }

    /// <summary>
    /// Stores the framebuffer size the swapchain was made for. The main thread calls it before the render thread starts,
    /// because the render thread cannot ask GLFW; resizes come through the window's event.
    /// </summary>
    public void SetFramebufferSize(Vector2D<int> size) => _state.Start(size.X, size.Y);

    /// <summary>
    /// Recreates the swapchain if needed, then waits until the GPU has finished the last frame that used this slot and
    /// resets the slot's command pool. Returns false when there is nothing to draw to (a zero-sized window); call
    /// <see cref="WaitForResize"/> and try again.
    /// </summary>
    public bool BeginFrame()
    {
        _frameBegun = false;
        if (_swapchain.IsZeroSized || _state.NeedsRecreate)
        {
            if (!TryRecreateSwapchain())
            {
                return false;
            }
        }

        // Timed after a swapchain recreate, so its rare wait for the device does not count as the fence wait.
        _fenceWaitStart = Stopwatch.GetTimestamp();
        var fence = _sync.CurrentInFlightFence;
        _context.Vk.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue).ThrowIfFailed("vkWaitForFences");
        _timer.Collect(_sync.CurrentFrame);
        _particlePass.Collect(_sync.CurrentFrame);
        _screenshotPass.Collect(_sync.CurrentFrame);
        _sync.ResetCurrentCommandPool();
        _frameBegun = true;
        return true;
    }

    /// <summary>
    /// Acquires the next swapchain image. Returns false when the swapchain is out of date; the next
    /// <see cref="BeginFrame"/> recreates it. After a true result, <see cref="Present"/> must follow: the slot's fence
    /// is reset here and only a submit signals it again.
    /// </summary>
    public bool TryAcquire(out uint imageIndex)
    {
        if (!_frameBegun)
        {
            throw new InvalidOperationException("TryAcquire was called without a successful BeginFrame.");
        }

        _frameBegun = false;
        uint acquired = 0;
        var result = _context.KhrSwapchain.AcquireNextImage(
            _context.Device,
            _swapchain.Swapchain,
            ulong.MaxValue,
            _sync.CurrentImageAvailable,
            default,
            &acquired
        );
        if (result == Result.ErrorOutOfDateKhr)
        {
            // The fence is still signalled (it was not reset), so the next BeginFrame does not wait on it.
            LogSwapchainStale("acquire", result);
            _state.OutOfDate();
            imageIndex = 0;
            return false;
        }

        if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            throw new VulkanException("vkAcquireNextImageKHR", result);
        }

        if (result == Result.SuboptimalKhr)
        {
            // The image is acquired and must be presented; the swapchain is made again after that.
            _state.Suboptimal();
        }

        var fence = _sync.CurrentInFlightFence;
        _context.Vk.ResetFences(_context.Device, 1, &fence).ThrowIfFailed("vkResetFences");
        _acquiredImage = acquired;
        _acquireResult = result;
        imageIndex = acquired;
        _cpu.Record(FrameCpuInterval.FenceAndAcquire, Stopwatch.GetTimestamp() - _fenceWaitStart);
        return true;
    }

    /// <summary>The request for the frame about to be drawn: the current slot and the swapchain's size.</summary>
    public RenderFrameRequest CreateRequest()
    {
        var extent = _swapchain.Extent;
        return new RenderFrameRequest(_sync.CurrentFrame, extent.Width, extent.Height);
    }

    /// <summary>
    /// Records the passes for <paramref name="frame"/>, submits, presents, and moves to the next slot. The frame's
    /// mesh copies are recorded only when <paramref name="fresh"/>: a frame presented a second time (on the way to
    /// stopping) must not copy its staging data again.
    /// </summary>
    public void Present(uint imageIndex, in RenderFrame frame, bool fresh)
    {
        if (imageIndex != _acquiredImage)
        {
            throw new InvalidOperationException($"Image {imageIndex} was not acquired by the last TryAcquire.");
        }

        _acquiredImage = uint.MaxValue;
        var recordStart = Stopwatch.GetTimestamp();
        if (fresh && frame.ShaderReload is { } reload)
        {
            ReloadShaders(reload);
        }

        var commandBuffer = _sync.CurrentCommandBuffer;
        Record(commandBuffer, imageIndex, in frame, fresh);

        var signalSemaphore = _sync.RenderFinishedFor(imageIndex);
        Submit(commandBuffer, _sync.CurrentImageAvailable, signalSemaphore, _sync.CurrentInFlightFence);
        _timer.OnSubmitted(_sync.CurrentFrame);
        _cpu.Record(FrameCpuInterval.Record, Stopwatch.GetTimestamp() - recordStart);

        var swapchain = _swapchain.Swapchain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        var presentStart = Stopwatch.GetTimestamp();
        var present = _context.KhrSwapchain.QueuePresent(_context.PresentQueue, &presentInfo);
        _cpu.Record(FrameCpuInterval.Present, Stopwatch.GetTimestamp() - presentStart);
        LogFrame(frame.Time.FrameIndex, imageIndex, _acquireResult, present);
        if (present == Result.ErrorOutOfDateKhr)
        {
            LogSwapchainStale("present", present);
            _state.OutOfDate();
        }
        else if (present == Result.SuboptimalKhr)
        {
            LogSwapchainStale("present", present);
            _state.Suboptimal();
        }
        else if (present != Result.Success)
        {
            throw new VulkanException("vkQueuePresentKHR", present);
        }

        _sync.Advance();
    }

    /// <summary>
    /// Compiles every pass's shaders again and, when all compiled, waits for the device and makes their pipelines again;
    /// on a compile error the old pipelines stay. Before the frame is recorded.
    /// </summary>
    private void ReloadShaders(ShaderReloadRequest request)
    {
        var start = Stopwatch.GetTimestamp();
        IReloadsShaders[] passes =
        [
            _meshPass,
            _assetPass,
            _particlePass,
            _linePass,
            _uiPass,
            _upscalePass,
            _smaaPass,
            _loadingPass,
            .. _renderPasses.Passes,
        ];
        var compiled = new object[passes.Length];
        try
        {
            for (var i = 0; i < passes.Length; i++)
            {
                compiled[i] = passes[i].CompileShaders();
            }
        }
        catch (ShaderCompilationException exception)
        {
            var failedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            request.Fail(exception.Message, failedMs);
            LogShadersNotReloaded(exception.Message);
            return;
        }

        _context.WaitIdle();
        var pipelines = 0;
        for (var i = 0; i < passes.Length; i++)
        {
            pipelines += passes[i].ReplacePipelines(compiled[i]);
        }

        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        request.Succeed(pipelines, ms);
        LogShadersReloaded(pipelines, ms);
    }

    /// <summary>Waits until the window is resized or the timeout passes. Render thread, after BeginFrame returned false.</summary>
    public void WaitForResize(int millisecondsTimeout)
    {
        lock (_resizeLock)
        {
            if (!_resizeSignalled)
            {
                Monitor.Wait(_resizeLock, millisecondsTimeout);
            }

            _resizeSignalled = false;
        }
    }

    /// <summary>VSync changed (main thread): the next frame makes the swapchain again with the new present mode.</summary>
    private void OnPresentModeChanged(bool vsync)
    {
        _swapchain.SetVSync(vsync);
        _state.PresentModeChanged();
    }

    /// <summary>
    /// The world's samples a pixel changed (main thread): the next frame makes the swapchain again, and the mesh pass its
    /// pipelines and multisampled images with it (<see cref="MeshPass.OnSwapchainRecreated"/>), the device idle.
    /// </summary>
    private void OnSamplesChanged(int samples)
    {
        _meshPass.RequestSamples(samples);
        _state.SamplesChanged();
    }

    private void OnFramebufferResized(Vector2D<int> size)
    {
        // Main thread. The size is stored before the request, so the render thread never sees it with an old size.
        _state.Resized(size.X, size.Y);
        lock (_resizeLock)
        {
            _resizeSignalled = true;
            Monitor.PulseAll(_resizeLock);
        }
    }

    private bool TryRecreateSwapchain()
    {
        // The requests are cleared before the size is read: a resize that arrives during this call asks again.
        if (!_state.TryBeginRecreate(out var width, out var height))
        {
            return false;
        }

        var size = new Vector2D<int>(width, height);
        _context.WaitIdle();
        _swapchain.Recreate(size);
        if (_swapchain.IsZeroSized)
        {
            return false;
        }

        _sync.OnSwapchainRecreated(_swapchain.Images.Count);
        _depth.OnSwapchainRecreated();
        _meshPass.OnSwapchainRecreated();
        _assetPass.OnSwapchainRecreated();
        // The image views' images follow the mesh pass's samples and format.
        _viewPass.OnSwapchainRecreated();
        _linePass.OnSwapchainRecreated();
        _particlePass.OnSwapchainRecreated();
        _uiPass.OnSwapchainRecreated();
        _upscalePass.OnSwapchainRecreated();
        _smaaPass.OnSwapchainRecreated();
        _loadingPass.OnSwapchainRecreated();
        _renderPasses.OnSwapchainRecreated();
        LogSwapchainRecreated(size.X, size.Y);
        return true;
    }

    private void Record(CommandBuffer commandBuffer, uint imageIndex, in RenderFrame frame, bool fresh)
    {
        var vk = _context.Vk;
        var image = _swapchain.Images[(int)imageIndex];
        var slot = _sync.CurrentFrame;

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        vk.BeginCommandBuffer(commandBuffer, &beginInfo).ThrowIfFailed("vkBeginCommandBuffer");
        _timer.Reset(commandBuffer, slot);
        _timer.Begin(commandBuffer, slot, GpuPass.Frame);
        if (fresh && frame.MeshCopies > 0)
        {
            _timer.Begin(commandBuffer, slot, GpuPass.Upload);
            _meshPass.RecordUploads(commandBuffer, _worldMeshes.Regions(frame.Slot, frame.MeshCopies));
            _timer.End(commandBuffer, slot, GpuPass.Upload);
        }

        // The mesh assets go to the GPU in the first frame, and their upload buffer later; under the loading screen too.
        _assetPass.RecordUploads(commandBuffer);
        // The image views' shown images are cleared in the first frame, before anything reads them.
        _viewPass.RecordFirstFrame(commandBuffer);

        // A new sky field goes into its image with the frame's other copies, under the loading screen too.
        if (fresh)
        {
            var meshes = frame.Meshes;
            _meshPass.RecordSkyField(commandBuffer, in meshes);
        }

        // The source stage is the stage the submit waits on the acquire semaphore at, so the layout transition waits
        // for the presentation engine to release the image. TopOfPipe would let it run before that.
        TransitionImage(
            commandBuffer,
            image,
            ImageLayout.Undefined,
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit
        );

        // While the loading screen covers the picture, the world and everything drawn over it are not recorded; the
        // mesh copies above still are, so the world is on the GPU when it shows. As it fades, it is blended over them.
        var screen = _swapchain.Extent;
        var loading = frame.Loading;
        if (!loading.Covers)
        {
            RecordWorld(commandBuffer, imageIndex, in frame, slot, fresh);
        }
        else
        {
            _particlePass.SkipSimulate(frame.Slot, frame.Particles);
        }

        if (loading.Drawn)
        {
            _timer.Begin(commandBuffer, slot, GpuPass.Loading);
            _loadingPass.Record(
                commandBuffer,
                _swapchain.ImageViews[(int)imageIndex],
                screen,
                loading.Seconds,
                loading.Alpha
            );
            _timer.End(commandBuffer, slot, GpuPass.Loading);
        }

        // A screenshot without the UI is copied before it is drawn; one with it, at the end. Only a fresh frame takes one:
        // a frame presented a second time has already.
        var screenshot = fresh ? frame.Screenshot : null;
        if (screenshot is { WithUi: false })
        {
            _screenshotPass.Record(commandBuffer, image, slot, screenshot, ImageLayout.ColorAttachmentOptimal);
            screenshot = null;
        }

        // The UI goes over everything, with the picture dimmed under it. Its changed rows are copied here, outside
        // rendering, only when the frame is fresh: a frame presented a second time must not copy its upload region again.
        var uiBands = fresh ? frame.UiBands : default;
        // The image views the HUD shows, under the UI's pixels.
        var panels = _viewPass.PanelsOf(frame.Slot);
        if (uiBands.Count > 0 || frame.UiVisible || frame.ScreenDim > 0 || !panels.IsEmpty)
        {
            _timer.Begin(commandBuffer, slot, GpuPass.Ui);
            _uiPass.RecordUpload(commandBuffer, frame.Slot, in uiBands);
            if (frame.UiVisible || frame.ScreenDim > 0 || !panels.IsEmpty)
            {
                _uiPass.Record(
                    commandBuffer,
                    _swapchain.ImageViews[(int)imageIndex],
                    _swapchain.Extent,
                    frame.UiVisible,
                    frame.ScreenDim,
                    panels
                );
            }

            _timer.End(commandBuffer, slot, GpuPass.Ui);
        }

        // The destination stage is the stage the submit signals the render-finished semaphore at, so the layout
        // transition finishes before the signal that present waits on. A screenshot's copy ends in the same layout.
        if (
            screenshot is null
            || !_screenshotPass.Record(commandBuffer, image, slot, screenshot, ImageLayout.PresentSrcKhr)
        )
        {
            TransitionImage(
                commandBuffer,
                image,
                ImageLayout.ColorAttachmentOptimal,
                ImageLayout.PresentSrcKhr,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit,
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.None
            );
        }

        _timer.End(commandBuffer, slot, GpuPass.Frame);
        vk.EndCommandBuffer(commandBuffer).ThrowIfFailed("vkEndCommandBuffer");
    }

    /// <summary>
    /// Records the world and the models, the particles' simulation (against the world's depth) and draws, and the debug
    /// lines, into the swapchain image or, below render scale 1, into the upscale's source image, then the upscale. With
    /// SMAA they draw into its scene image, and SMAA draws into the swapchain image or the upscale's source. The
    /// particles' compute work is recorded only when <paramref name="fresh"/>.
    /// </summary>
    private void RecordWorld(CommandBuffer commandBuffer, uint imageIndex, in RenderFrame frame, int slot, bool fresh)
    {
        // Below render scale 1 the world, models, and debug lines draw into the top-left part of the upscale's source
        // image, the drawn size of the screen-sized image, and FSR 1 upscales it into the swapchain image; the depth image is the screen's size, and the passes use its
        // top-left part. At scale 1 they draw straight into the swapchain image. With SMAA they draw into its scene
        // image, the drawn size, at any scale.
        var screen = _swapchain.Extent;
        var scale = frame.RenderScale;
        var drawn = new Extent2D(RenderScale.Apply(screen.Width, scale), RenderScale.Apply(screen.Height, scale));
        var upscaled = drawn.Width != screen.Width || drawn.Height != screen.Height;
        // A multisampled world has smooth edges already.
        var antiAlias = frame.AntiAlias && !_meshPass.Multisampled;
        ImageView target;
        if (antiAlias)
        {
            target = _smaaPass.BeginScene(commandBuffer, slot, drawn);
        }
        else
        {
            _smaaPass.NotUsed(slot);
            target = upscaled
                ? _upscalePass.BeginSource(commandBuffer, slot, drawn, screen)
                : _swapchain.ImageViews[(int)imageIndex];
        }

        if (!upscaled)
        {
            _upscalePass.DrawnAtFullSize(slot);
        }

        var view = frame.View;

        // The world and the models write the depth image themselves.
        var particles = frame.Particles.Active;
        _timer.Begin(commandBuffer, slot, GpuPass.Voxels);
        _depth.RecordFrameStart(commandBuffer, resolvedInto: _meshPass.Multisampled);
        var meshes = frame.Meshes;
        var assets = frame.MeshAssets;
        _assetPass.BeginFrame(frame.Slot, in assets, fresh);
        _renderPasses.BeginFrame(frame.Slot, slot, fresh);
        _meshPass.Record(
            commandBuffer,
            target,
            _depth.View,
            drawn,
            in view,
            frame.Slot,
            in meshes,
            slot,
            _assetPass,
            _renderPasses
        );
        _timer.End(commandBuffer, slot, GpuPass.Voxels);

        // The image views due this frame, after the world's pass (the frame's posed mesh assets are skinned), each into
        // its own images; a frame presented again keeps their last updates.
        if (fresh && _viewPass.AnyDue(frame.Slot))
        {
            _timer.Begin(commandBuffer, slot, GpuPass.ImageViews);
            _viewPass.Record(commandBuffer, frame.Slot);
            _timer.End(commandBuffer, slot, GpuPass.ImageViews);
        }

        // The particles move after the world is drawn: they meet it through its depth image, and lit ones take their
        // light from its shadow cascades and sky field. A frame with none alive records nothing and marks its slot as
        // not simulated.
        if (particles)
        {
            _timer.Begin(commandBuffer, slot, GpuPass.ParticleSimulate);
        }

        _particlePass.RecordSimulate(commandBuffer, frame.Slot, frame.Particles, fresh);
        if (particles)
        {
            _timer.End(commandBuffer, slot, GpuPass.ParticleSimulate);
        }

        if (particles)
        {
            _timer.Begin(commandBuffer, slot, GpuPass.Particles);
            _particlePass.RecordDraw(commandBuffer, target, _depth.View, drawn, in view, particles);
            _timer.End(commandBuffer, slot, GpuPass.Particles);
        }

        if (frame.DebugLineVertices > 0)
        {
            _timer.Begin(commandBuffer, slot, GpuPass.DebugLines);
            _linePass.Record(commandBuffer, target, _depth.View, drawn, in view, frame.Slot, frame.DebugLineVertices);
            _timer.End(commandBuffer, slot, GpuPass.DebugLines);
        }

        // A game's passes over the resolved scene, before anti-aliasing and the upscale.
        _renderPasses.RecordScene(commandBuffer, target, _depth.View, drawn, in view);

        if (antiAlias)
        {
            _timer.Begin(commandBuffer, slot, GpuPass.AntiAlias);
            var smaaTarget = upscaled
                ? _upscalePass.BeginSource(commandBuffer, slot, drawn, screen)
                : _swapchain.ImageViews[(int)imageIndex];
            _smaaPass.Record(commandBuffer, slot, smaaTarget, frame.AntiAliasPicture);
            _timer.End(commandBuffer, slot, GpuPass.AntiAlias);
        }

        if (upscaled)
        {
            _timer.Begin(commandBuffer, slot, GpuPass.Upscale);
            _upscalePass.Record(
                commandBuffer,
                slot,
                _swapchain.ImageViews[(int)imageIndex],
                screen,
                FormatIsSrgb(_swapchain.ImageFormat)
            );
            _timer.End(commandBuffer, slot, GpuPass.Upscale);
        }

        // A game's passes over the screen's picture, before the UI.
        _renderPasses.RecordScreen(
            commandBuffer,
            _swapchain.Images[(int)imageIndex],
            _swapchain.ImageViews[(int)imageIndex],
            screen,
            in view
        );
    }

    /// <summary>True for the sRGB formats a swapchain may have: an image of one encodes the linear colour it is given.</summary>
    private static bool FormatIsSrgb(Format format) => UpscalePass.Unorm(format) != format;

    private void TransitionImage(
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

    private void Submit(CommandBuffer commandBuffer, VkSemaphore wait, VkSemaphore signal, Fence fence)
    {
        var waitInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = wait,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        var signalInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = signal,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        var commandBufferInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = commandBuffer,
        };
        var submitInfo = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = 1,
            PWaitSemaphoreInfos = &waitInfo,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandBufferInfo,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signalInfo,
        };

        _context.Vk.QueueSubmit2(_context.GraphicsQueue, 1, &submitInfo, fence).ThrowIfFailed("vkQueueSubmit2");
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Shaders: reloaded, {Pipelines} pipelines made again in {Ms:F0} ms"
    )]
    private partial void LogShadersReloaded(int pipelines, double ms);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shaders: not reloaded, the old pipelines stay: {Error}")]
    private partial void LogShadersNotReloaded(string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Swapchain recreated for {Width}x{Height}")]
    private partial void LogSwapchainRecreated(int width, int height);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Swapchain stale on {Operation} ({Result}); recreating")]
    private partial void LogSwapchainStale(string operation, Result result);

    [LoggerMessage(
        Level = LogLevel.Trace,
        Message = "Frame {FrameIndex}: image {ImageIndex}, acquire {Acquire}, present {Present}"
    )]
    private partial void LogFrame(ulong frameIndex, uint imageIndex, Result acquire, Result present);
}
