using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Pipelines;
using Itera.Engine.Rendering.Shaders;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// Draws the loading screen (<c>loading.frag</c>) over the whole swapchain image: covering it while loading, when the
/// world is not drawn, and blended over the world while it fades out. One full-screen triangle with push constants
/// only; no buffer, image, or descriptor. Drawn at the screen's size whatever the render scale, before the UI. Created
/// before the render thread starts; recorded by the render thread.
/// </summary>
public sealed unsafe class LoadingPass : IDisposable, IReloadsShaders
{
    private readonly VulkanContext _context;
    private readonly VulkanSwapchain _swapchain;
    private readonly ShaderCompiler _shaders;
    private byte[] _vertexSpirv = [];
    private byte[] _fragmentSpirv = [];
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private Format _format;
    private bool _disposed;

    public LoadingPass(VulkanContext context, VulkanSwapchain swapchain, ShaderCompiler shaders)
    {
        _context = context;
        _swapchain = swapchain;
        _shaders = shaders;
    }

    /// <summary>Compiles the shaders and makes the pipeline for the swapchain's format.</summary>
    public void Initialize()
    {
        TakeShaders((byte[][])CompileShaders());
        _layout = PipelineFactory.CreateLayout(
            _context,
            ReadOnlySpan<DescriptorSetLayout>.Empty,
            ShaderStageFlags.FragmentBit,
            (uint)sizeof(LoadingConstants),
            "loading layout"
        );
        CreatePipeline(_swapchain.ImageFormat);
    }

    /// <summary>Makes a new pipeline when the swapchain's format changed. Render thread, device idle.</summary>
    public void OnSwapchainRecreated()
    {
        if (_swapchain.ImageFormat != _format)
        {
            _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
            _pipeline = default;
            CreatePipeline(_swapchain.ImageFormat);
        }
    }

    /// <summary>
    /// Records the loading screen into <paramref name="target"/> at <paramref name="alpha"/>. At 1 it covers every
    /// pixel, so the image's contents are not loaded; below 1 it is blended over what earlier passes drew, and the
    /// barrier waits for their colour writes. Render thread.
    /// </summary>
    public void Record(CommandBuffer commandBuffer, ImageView target, Extent2D extent, float seconds, float alpha)
    {
        var vk = _context.Vk;
        var opaque = alpha >= 1;
        if (!opaque)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags2.ColorAttachmentWriteBit,
                DstStageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit,
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier,
            };
            vk.CmdPipelineBarrier2(commandBuffer, &dependency);
        }

        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = opaque ? AttachmentLoadOp.DontCare : AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
        };
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
        };
        vk.CmdBeginRendering(commandBuffer, &renderingInfo);
        // A negative height flips Y (core since Vulkan 1.1), as in every pass; loading.frag reads gl_FragCoord, which
        // counts from the top-left corner either way.
        var viewport = new Viewport(0, extent.Height, extent.Width, -(float)extent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        var constants = new LoadingConstants
        {
            Width = extent.Width,
            Height = extent.Height,
            Time = seconds,
            Alpha = Math.Clamp(alpha, 0, 1),
            LinearOutput = UpscalePass.Unorm(_format) != _format ? 1u : 0u,
        };
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        vk.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(LoadingConstants),
            &constants
        );
        vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        vk.CmdEndRendering(commandBuffer);
    }

    /// <summary>Destroys the pipeline and its layout. The GPU must be done with them. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_context.IsAbandoned)
        {
            var vk = _context.Vk;
            var device = _context.Device;
            if (_pipeline.Handle != 0)
            {
                vk.DestroyPipeline(device, _pipeline, null);
            }

            if (_layout.Handle != 0)
            {
                vk.DestroyPipelineLayout(device, _layout, null);
            }
        }

        _pipeline = default;
        _layout = default;
    }

    public object CompileShaders() => new[] { _shaders.Compile("fullscreen.vert"), _shaders.Compile("loading.frag") };

    public int ReplacePipelines(object compiled)
    {
        if (_pipeline.Handle != 0)
        {
            _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
        }

        _pipeline = default;
        TakeShaders((byte[][])compiled);
        CreatePipeline(_format);
        return 1;
    }

    private void TakeShaders(byte[][] compiled)
    {
        _vertexSpirv = compiled[0];
        _fragmentSpirv = compiled[1];
    }

    private void CreatePipeline(Format format)
    {
        var noDepth = new DepthState(Format.Undefined, CompareOp.Always, Write: false);
        _pipeline = PipelineFactory.CreateFullscreen(
            _context,
            _vertexSpirv,
            _fragmentSpirv,
            _layout,
            format,
            in noDepth,
            "loading",
            ColorBlend.Premultiplied
        );
        _format = format;
    }
}
