using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Pipelines;
using Silk.NET.Vulkan;

namespace Itera.MeshSample.Rendering;

/// <summary>
/// The mesh sample's own render pass, an example of a game's (<see cref="IRenderPass"/>, at the
/// <see cref="RenderStage.Transparent"/> stage): it draws a glow round the yard block the player aims at
/// (<see cref="AimHighlight"/>): a box a little larger than the block whose edges glow, and whose faces glow faintly
/// where they turn from the eye, pulsing, added to the picture and hidden behind what is nearer (depth tested, not
/// written). Its data (<see cref="AimHighlightData"/>) is one record a frame slot in host-visible memory; its shaders
/// are the sample's (<c>Shaders/Game/aim-highlight.*</c>, copied beside the engine's, whose <c>view.glsl</c> they
/// include). It draws nothing while no block is aimed at.
/// </summary>
public sealed unsafe class AimHighlightPass(AimHighlight highlight) : IRenderPass
{
    /// <summary>The vertex shader, under the shader folder.</summary>
    public const string VertexShader = "Game/aim-highlight.vert";

    /// <summary>The fragment shader, under the shader folder.</summary>
    public const string FragmentShader = "Game/aim-highlight.frag";

    /// <summary>How far in from the box's edges the glow falls to a third, in metres.</summary>
    public const float EdgeMetres = 0.04f;

    /// <summary>The pulse's seconds wrap at this many (whole periods of it), so a float keeps them exact.</summary>
    private const double PulseRepeatSeconds = 50 * Math.PI;

    private const ShaderStageFlags Stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    /// <summary>The glow's colour (display values, added to the picture) and, in alpha, its strength.</summary>
    public static readonly Vector4 Colour = new(1f, 0.8f, 0.45f, 0.6f);

    private static readonly ulong s_slotBytes = (ulong)sizeof(AimHighlightData);

    // Whether each frame slot draws the highlight: written by the main thread with the slot's record, read by the render
    // thread after the slot's frame is published.
    private readonly bool[] _shown = new bool[RenderPassSetup.FramesInFlight];
    private RenderPassSetup? _setup;
    private GpuBuffer? _buffer;
    private RenderPassBufferSet? _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private byte[] _vertexSpirv = [];
    private byte[] _fragmentSpirv = [];
    private bool _disposed;

    public string Name => "aim highlight";

    public RenderStage Stage => RenderStage.Transparent;

    private RenderPassSetup Setup =>
        _setup ?? throw new InvalidOperationException("The aim highlight pass has not been initialised.");

    public void Initialize(RenderPassSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        _buffer = setup.Allocator.CreateBuffer(
            s_slotBytes * (ulong)RenderPassSetup.FramesInFlight,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Upload,
            "aim highlight"
        );
        _set = setup.CreateBufferSet([_buffer], Stages, "aim highlight");
        var setLayout = _set.Layout;
        _layout = setup.CreateLayout(
            new ReadOnlySpan<DescriptorSetLayout>(&setLayout, 1),
            Stages,
            (uint)sizeof(ViewConstants),
            "aim highlight layout"
        );
        ReplacePipelines(CompileShaders());
    }

    public void Write(int slot, in RenderPassFrame frame)
    {
        _shown[slot] = highlight.Active && _buffer is not null;
        if (!_shown[slot])
        {
            return;
        }

        *(AimHighlightData*)(_buffer!.Mapped + s_slotBytes * (ulong)slot) = new AimHighlightData
        {
            Transform = highlight.Transform,
            Colour = Colour,
            Seconds = (float)(frame.Time.TotalSeconds % PulseRepeatSeconds),
            EdgeMetres = EdgeMetres,
        };
    }

    public void Record(in RenderPassContext context)
    {
        if (!_shown[context.Slot] || _buffer is null || _set is null)
        {
            return;
        }

        _buffer.Flush(s_slotBytes * (ulong)context.Slot, s_slotBytes);
        var vk = Setup.Context.Vk;
        var commandBuffer = context.CommandBuffer;
        var set = _set.Set;
        var view = context.View;
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(commandBuffer, _layout, Stages, 0, (uint)sizeof(ViewConstants), &view);
        vk.CmdDraw(commandBuffer, 36, 1, 0, 0);
    }

    public void OnTargetsChanged(RenderPassSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (_setup is null)
        {
            return;
        }

        var changed = setup.ColorFormat != _setup.ColorFormat || setup.Samples != _setup.Samples;
        _setup = setup;
        if (changed)
        {
            DestroyPipeline();
            CreatePipeline();
        }
    }

    public object CompileShaders() =>
        new[] { Setup.Shaders.Compile(VertexShader), Setup.Shaders.Compile(FragmentShader) };

    public int ReplacePipelines(object compiled)
    {
        var shaders = (byte[][])compiled;
        _vertexSpirv = shaders[0];
        _fragmentSpirv = shaders[1];
        DestroyPipeline();
        CreatePipeline();
        return 1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyPipeline();
        if (_setup is { Context: { IsAbandoned: false } context } && _layout.Handle != 0)
        {
            context.Vk.DestroyPipelineLayout(context.Device, _layout, null);
        }

        _layout = default;
        _set?.Dispose();
        _set = null;
        _buffer?.Dispose();
        _buffer = null;
    }

    private void CreatePipeline() =>
        _pipeline = Setup.CreateTriangles(
            _vertexSpirv,
            _fragmentSpirv,
            _layout,
            CullModeFlags.BackBit,
            depthTest: true,
            depthWrite: false,
            ColorBlend.Additive,
            "aim highlight"
        );

    private void DestroyPipeline()
    {
        if (_setup is { Context: { IsAbandoned: false } context } && _pipeline.Handle != 0)
        {
            context.Vk.DestroyPipeline(context.Device, _pipeline, null);
        }

        _pipeline = default;
    }
}
