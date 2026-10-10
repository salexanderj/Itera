using Itera.Engine.Rendering.GpuData;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The image views (<see cref="RenderViews"/>, <see cref="RenderViewTarget"/>): each drawn with the pass's pipelines and
/// shaders through a set of its layout that names the view's own frame records, into the view's own images, then resolved
/// or copied into the image the shaders and the HUD read.
/// </summary>
public sealed unsafe partial class MeshPass
{
    /// <summary>
    /// The stages that may read a view's shown image: the HUD's and a material's fragment shaders, and a game's pass at
    /// any stage.
    /// </summary>
    private const PipelineStageFlags2 ShownReaders =
        PipelineStageFlags2.FragmentShaderBit
        | PipelineStageFlags2.VertexShaderBit
        | PipelineStageFlags2.ComputeShaderBit;

    /// <summary>
    /// Makes the world's, the models', and the sky's pipelines at one sample a pixel too, which image views draw with,
    /// while the world is multisampled (now, and whenever the pipelines are made again). At the run's "image views" step,
    /// before the render thread.
    /// </summary>
    internal void WantViewPipelines()
    {
        _viewPipelinesWanted = true;
        CreateViewPipelines();
    }

    /// <summary>The world's pipelines image views draw with: at one sample a pixel.</summary>
    private Pipeline ViewMeshPipeline => _viewMeshPipeline.Handle != 0 ? _viewMeshPipeline : _meshPipeline;

    private Pipeline ViewModelPipeline => _viewModelPipeline.Handle != 0 ? _viewModelPipeline : _modelPipeline;

    private Pipeline ViewSkyPipeline => _viewSkyPipeline.Handle != 0 ? _viewSkyPipeline : _skyPipeline;

    /// <summary>
    /// A set of the pass's layout for <paramref name="target"/>, from a pool of its own (<paramref name="pool"/>, which the
    /// target destroys): its frame records, and the pass's heap, maps, sky field, textures, and looks.
    /// </summary>
    internal DescriptorSet CreateViewSet(RenderViewTarget target, out DescriptorPool pool)
    {
        pool = CreatePool();
        return WriteSet(
            pool,
            target.Draws,
            target.Lighting,
            target.ModelDraws,
            target.Lights,
            target.Clusters,
            $"image view {target.Info.Name} meshes"
        );
    }

    /// <summary>
    /// Records an update of image view <paramref name="target"/> in <paramref name="slot"/>: what the main thread wrote
    /// made visible, the view's lights sorted into its clusters, then the world, the models, the mesh assets, the sky, and
    /// the blended mesh assets drawn into its images, one sample a pixel, which are copied into the image the shaders read.
    /// Outside any rendering, after the frame's skinning. Render thread.
    /// </summary>
    internal void RecordView(
        CommandBuffer commandBuffer,
        RenderViewTarget target,
        int slot,
        in RenderViewFrame frame,
        MeshAssetPass? assets
    )
    {
        var vk = _context.Vk;
        target.Flush(slot, in frame);
        var extent = target.Extent;
        var constants = frame.View;
        constants.Width = extent.Width;
        constants.Height = extent.Height;
        RecordClusters(commandBuffer, in constants, target.MeshSet);

        // The last update's resolve or copy read the colour; its contents and the depth's go.
        ImageBarrier(
            commandBuffer,
            target.Colour.Image,
            ImageAspectFlags.ColorBit,
            1,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.None,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            ImageLayout.ColorAttachmentOptimal
        );
        ImageBarrier(
            commandBuffer,
            target.Depth.Image,
            ImageAspectFlags.DepthBit,
            1,
            DepthTests,
            AccessFlags2.DepthStencilAttachmentWriteBit,
            DepthTests,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit,
            ImageLayout.DepthAttachmentOptimal
        );
        var colorAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target.Colour.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
        };
        var depthAttachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target.Depth.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(0, 0) },
        };
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment,
        };
        vk.CmdBeginRendering(commandBuffer, &renderingInfo);
        var viewport = new Viewport(0, extent.Height, extent.Width, -(float)extent.Height, 0, 1);
        var scissor = new Rect2D(new Offset2D(0, 0), extent);
        var set = target.MeshSet;
        vk.CmdSetViewport(commandBuffer, 0, 1, &viewport);
        vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(commandBuffer, _layout, Stages, 0, (uint)sizeof(ViewConstants), &constants);
        var draws = Math.Clamp(frame.Draws, 0, RenderViewTarget.MaxDraws);
        if (draws > 0)
        {
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, ViewMeshPipeline);
            vk.CmdBindIndexBuffer(commandBuffer, Heap.Buffer, 0, IndexType.Uint16);
            vk.CmdDrawIndexedIndirect(
                commandBuffer,
                target.Commands.Buffer,
                RenderViewTarget.CommandsOffset(slot),
                (uint)draws,
                (uint)sizeof(DrawIndexedIndirectCommand)
            );
        }

        var models = Math.Clamp(frame.Models.Commands, 0, RenderViewTarget.MaxModelCommands);
        if (models > 0)
        {
            vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, ViewModelPipeline);
            vk.CmdBindIndexBuffer(commandBuffer, Heap.Buffer, 0, IndexType.Uint16);
            vk.CmdDrawIndexedIndirect(
                commandBuffer,
                target.ModelCommands.Buffer,
                RenderViewTarget.ModelCommandsOffset(slot),
                (uint)models,
                (uint)sizeof(DrawIndexedIndirectCommand)
            );
        }

        if (assets is not null && frame.Assets.Commands > 0)
        {
            assets.RecordViewSeen(commandBuffer, target, slot, in constants, in frame.Assets);
            // The sky with this pass's set and constants again, whatever the assets bound.
            vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
            vk.CmdPushConstants(commandBuffer, _layout, Stages, 0, (uint)sizeof(ViewConstants), &constants);
        }

        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, ViewSkyPipeline);
        vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
        if (assets is not null && frame.Blended > 0)
        {
            assets.RecordViewBlended(commandBuffer, target, slot, in constants, frame.Blended);
        }

        vk.CmdEndRendering(commandBuffer);
        RecordShown(commandBuffer, target);
    }

    /// <summary>
    /// Clears a view's shown image to black and leaves it for the shaders to read, its first frame, so nothing reads it
    /// undefined before its first update. Outside any rendering, before anything reads it. Render thread.
    /// </summary>
    internal void RecordViewCleared(CommandBuffer commandBuffer, RenderViewTarget target)
    {
        ImageBarrier(
            commandBuffer,
            target.Shown.Image,
            ImageAspectFlags.ColorBit,
            1,
            PipelineStageFlags2.None,
            AccessFlags2.None,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit,
            ImageLayout.TransferDstOptimal
        );
        var black = new ClearColorValue(0f, 0f, 0f, 1f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        _context.Vk.CmdClearColorImage(
            commandBuffer,
            target.Shown.Image,
            ImageLayout.TransferDstOptimal,
            &black,
            1,
            &range
        );
        ImageBarrier(
            commandBuffer,
            target.Shown.Image,
            ImageAspectFlags.ColorBit,
            1,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit,
            ShownReaders,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferDstOptimal
        );
        target.ShownReady = true;
    }

    /// <summary>
    /// The update's colour copied into the view's shown image, which waits for the reads of it before (the HUD's, a
    /// material's) and is left for the fragment shaders to read.
    /// </summary>
    private void RecordShown(CommandBuffer commandBuffer, RenderViewTarget target)
    {
        var vk = _context.Vk;
        ImageBarrier(
            commandBuffer,
            target.Colour.Image,
            ImageAspectFlags.ColorBit,
            1,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferReadBit,
            ImageLayout.TransferSrcOptimal,
            ImageLayout.ColorAttachmentOptimal
        );
        ImageBarrier(
            commandBuffer,
            target.Shown.Image,
            ImageAspectFlags.ColorBit,
            1,
            ShownReaders,
            AccessFlags2.None,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit,
            ImageLayout.TransferDstOptimal,
            ImageLayout.ShaderReadOnlyOptimal
        );
        var layers = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1);
        var copy = new ImageCopy
        {
            SrcSubresource = layers,
            DstSubresource = layers,
            Extent = new Extent3D(target.Extent.Width, target.Extent.Height, 1),
        };
        vk.CmdCopyImage(
            commandBuffer,
            target.Colour.Image,
            ImageLayout.TransferSrcOptimal,
            target.Shown.Image,
            ImageLayout.TransferDstOptimal,
            1,
            &copy
        );

        ImageBarrier(
            commandBuffer,
            target.Shown.Image,
            ImageAspectFlags.ColorBit,
            1,
            PipelineStageFlags2.AllTransferBit,
            AccessFlags2.TransferWriteBit,
            ShownReaders,
            AccessFlags2.ShaderSampledReadBit,
            ImageLayout.ShaderReadOnlyOptimal,
            ImageLayout.TransferDstOptimal
        );
    }
}
