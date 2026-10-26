using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Pipelines;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Grass;

/// <summary>
/// The grass's blades (<see cref="IRenderPass"/> at the <see cref="RenderStage.Opaque"/> stage), as
/// <see cref="GrassPlacePass"/> placed them this frame: one indexed indirect call of its two draws, the near blades' and
/// the far ones', over its index pattern (<c>grass.vert</c>, <c>grass.frag</c>). The vertex shader builds each blade from its
/// record; the fragment shader lights it as the ground is lit, cheaply, and grades it as every surface of the world.
/// Solid, both faces drawn, writing depth, so the blades the world hides are not shaded. It draws only in a frame whose
/// compute stage placed blades (<see cref="GrassPlacePass.Shows"/>), and owns no buffer: the place pass's set, index
/// pattern, and draws are its. Initialised after the place pass (registered after it,
/// <see cref="GrassServiceCollectionExtensions.AddIteraGrass{TSource}"/>).
/// </summary>
public sealed unsafe class GrassDrawPass(GrassPlacePass place) : IRenderPass
{
    /// <summary>The shaders, under the shader folder.</summary>
    public const string VertexShader = "grass.vert";

    /// <inheritdoc cref="VertexShader"/>
    public const string FragmentShader = "grass.frag";

    private const ShaderStageFlags Stages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    private RenderPassSetup? _setup;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private byte[] _vertexSpirv = [];
    private byte[] _fragmentSpirv = [];
    private bool _disposed;

    public string Name => "grass";

    public RenderStage Stage => RenderStage.Opaque;

    private RenderPassSetup Setup =>
        _setup ?? throw new InvalidOperationException("The grass's draw pass has not been initialised.");

    public void Initialize(RenderPassSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        var set =
            place.Set
            ?? throw new InvalidOperationException("The grass's place pass is initialised before its draw pass.");
        _setup = setup;
        var sets = stackalloc DescriptorSetLayout[2];
        sets[0] = setup.MeshSetLayout;
        sets[1] = set.Layout;
        _layout = setup.CreateLayout(
            new ReadOnlySpan<DescriptorSetLayout>(sets, 2),
            Stages,
            (uint)sizeof(ViewConstants),
            "grass layout"
        );
        ReplacePipelines(CompileShaders());
    }

    public void Write(int slot, in RenderPassFrame frame) { }

    public void Record(in RenderPassContext context)
    {
        if (
            !place.Shows(context.Slot)
            || place.Set is not { } set
            || place.Indices is not { } indices
            || place.Draws is not { } draws
            || _pipeline.Handle == 0
        )
        {
            return;
        }

        var vk = Setup.Context.Vk;
        var commandBuffer = context.CommandBuffer;
        var sets = stackalloc DescriptorSet[2];
        sets[0] = context.MeshSet;
        sets[1] = set.Set;
        var view = context.View;
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipeline);
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);
        vk.CmdPushConstants(commandBuffer, _layout, Stages, 0, (uint)sizeof(ViewConstants), &view);
        vk.CmdBindIndexBuffer(commandBuffer, indices.Buffer, 0, IndexType.Uint16);
        vk.CmdDrawIndexedIndirect(commandBuffer, draws.Buffer, 0, 2, (uint)sizeof(DrawIndexedIndirectCommand));
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
    }

    // Both faces: a blade is seen from either side.
    private void CreatePipeline() =>
        _pipeline = Setup.CreateTriangles(
            _vertexSpirv,
            _fragmentSpirv,
            _layout,
            CullModeFlags.None,
            depthTest: true,
            depthWrite: true,
            ColorBlend.None,
            "grass"
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
