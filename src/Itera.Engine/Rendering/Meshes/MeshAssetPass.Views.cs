using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// The mesh assets in the image views (<see cref="RenderViewTarget"/>): drawn with the pass's pipelines through a set of its
/// layout that names the view's draws, from the view's commands.
/// </summary>
public sealed unsafe partial class MeshAssetPass
{
    /// <summary>
    /// Makes the camera's pipelines at one sample a pixel too, which image views draw with, while the world is
    /// multisampled (now, and whenever the pipelines are made again). At the run's "image views" step, before the render
    /// thread.
    /// </summary>
    internal void WantViewPipelines()
    {
        _viewPipelinesWanted = true;
        CreateViewPipelines();
    }

    /// <summary>
    /// A set of the pass's layout naming an image view's <paramref name="draws"/>, from a pool of its own
    /// (<paramref name="pool"/>, which the view destroys).
    /// </summary>
    internal DescriptorSet CreateViewSet(GpuBuffer draws, string name, out DescriptorPool pool)
    {
        pool = CreatePool();
        return WriteSet(pool, draws, name);
    }

    /// <summary>
    /// Draws what an image view sees of the frame's mesh assets, inside its rendering after the models (its viewport is
    /// set): both of the view's sets, its constants, and each range's commands.
    /// </summary>
    internal void RecordViewSeen(
        CommandBuffer commandBuffer,
        RenderViewTarget target,
        int slot,
        in ViewConstants view,
        in MeshAssetList list
    )
    {
        BindSets(commandBuffer, _layout, target.MeshSet, target.AssetSet);
        var constants = view;
        _context.Vk.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(ViewConstants),
            &constants
        );
        DrawRanges(
            commandBuffer,
            target.AssetCommands.Buffer,
            RenderViewTarget.AssetCommandsOffset(slot, RenderViewTarget.AssetSeenList),
            RenderViewTarget.MaxAssetCommands,
            in list,
            _viewBlendPipeline.Handle != 0 ? _viewPipelines : _pipelines
        );
    }

    /// <summary>
    /// Draws an image view's blended mesh assets, back to front, inside its rendering after the sky: both of the view's
    /// sets, its constants, and one indirect draw of every command in order.
    /// </summary>
    internal void RecordViewBlended(
        CommandBuffer commandBuffer,
        RenderViewTarget target,
        int slot,
        in ViewConstants view,
        int commands
    )
    {
        commands = Math.Clamp(commands, 0, RenderViewTarget.MaxAssetCommands);
        if (commands <= 0)
        {
            return;
        }

        var vk = _context.Vk;
        BindSets(commandBuffer, _layout, target.MeshSet, target.AssetSet);
        var constants = view;
        vk.CmdPushConstants(
            commandBuffer,
            _layout,
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            0,
            (uint)sizeof(ViewConstants),
            &constants
        );
        vk.CmdBindPipeline(
            commandBuffer,
            PipelineBindPoint.Graphics,
            _viewBlendPipeline.Handle != 0 ? _viewBlendPipeline : _blendPipeline
        );
        vk.CmdDrawIndexedIndirect(
            commandBuffer,
            target.AssetCommands.Buffer,
            RenderViewTarget.AssetCommandsOffset(slot, RenderViewTarget.AssetBlendedList),
            (uint)commands,
            (uint)sizeof(DrawIndexedIndirectCommand)
        );
    }
}
