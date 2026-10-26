using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Meshes;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Grass;

/// <summary>
/// The grass's blades, made afresh each frame on the GPU (<see cref="IRenderPass"/> at the <see cref="RenderStage.Compute"/>
/// stage), which <see cref="GrassDrawPass"/> draws; a game opts in (<see cref="GrassServiceCollectionExtensions.AddIteraGrass{TSource}"/>).
/// <see cref="Write"/> asks the game's <see cref="IGrassSource"/> what to grow, and lists the camera's blocks whose
/// centres lie within the field's reach (<see cref="WorldMeshes.CameraCommands"/>, nearest first) and the frame's numbers
/// into the slot's region. <see cref="Record"/> then runs <c>grass-place.comp</c>, one invocation
/// a triangle of those blocks, which reads the triangles from the mesh heap and writes the blades that stand on the
/// grass, near and far; then <c>grass-count.comp</c>, which writes the two draws. The blades and the draws are one copy
/// each, on the device: each fresh frame waits for the last one's draws before writing them again, and a frame
/// presented again draws the last ones made. Nothing is listed while the source gives no grass or none to a square
/// metre, under an orthographic lens, or with no block near, and then nothing is drawn.
/// </summary>
public sealed unsafe class GrassPlacePass(WorldMeshes meshes, IGrassSource source) : IRenderPass
{
    /// <summary>The shaders, under the shader folder.</summary>
    public const string PlaceShader = "grass-place.comp";

    /// <inheritdoc cref="PlaceShader"/>
    public const string CountShader = "grass-count.comp";

    /// <summary>The blocks a frame slot lists (<c>GRASS_MOST_BLOCKS</c>): enough for a field of 10 cm voxels' blocks.</summary>
    public const int MostBlocks = 1024;

    /// <summary>Room for the near blades (three segments each) and the far ones (one triangle each).</summary>
    public const int NearMost = 65_536;

    /// <inheritdoc cref="NearMost"/>
    public const int FarMost = 65_536;

    /// <summary>Blades an instance of a draw holds: its index pattern repeats this many (<c>GRASS_BLADES_AN_INSTANCE</c>).</summary>
    public const int BladesAnInstance = 64;

    /// <summary>A near blade's corners and triangles, and a far one's (<c>grass.vert</c>).</summary>
    public const int NearCorners = 7;

    /// <inheritdoc cref="NearCorners"/>
    public const int NearTriangles = 5;

    /// <inheritdoc cref="NearCorners"/>
    public const int FarCorners = 3;

    /// <summary>A blade's bytes (<c>GrassBlade</c>, the GPU's own).</summary>
    public const int BladeBytes = 32;

    /// <summary>The two draws and the two counts of blades placed (<c>GrassDraws</c>), in bytes.</summary>
    public const int DrawsBytes = 2 * 20 + 2 * sizeof(uint);

    /// <summary>
    /// How far past the field's radius a block's centre may lie and still hold grass, in blocks of the finest level
    /// (<see cref="WorldMeshes.FinestBlockMetres"/>): half a block's diagonal, and a voxel more.
    /// </summary>
    public const float BlockReach = 0.9f;

    // The two counts' place in the draws' buffer, after the two commands.
    private const ulong PlacedOffset = 2 * 20;

    // Invocations in a workgroup of grass-place.comp, one a triangle.
    private const uint GroupSize = 64;

    // The frame's seconds wrap here, where a float still holds them to a millisecond, so the wind moves smoothly.
    private const double RepeatSeconds = 8192;

    private const ShaderStageFlags SetStages = ShaderStageFlags.ComputeBit | ShaderStageFlags.VertexBit;

    private static readonly ulong s_frameBytes = (ulong)sizeof(GrassFrame);
    private static readonly ulong s_blocksBytes = (ulong)(sizeof(GrassBlock) * MostBlocks);

    // What each frame slot places, written by the main thread with the slot's records and read by the render thread as
    // it records the slot's frame: whether it places any, and the triangles of its blocks.
    private readonly bool[] _active = new bool[RenderPassSetup.FramesInFlight];
    private readonly uint[] _triangles = new uint[RenderPassSetup.FramesInFlight];

    // Whether each slot's frame draws blades (render thread): set as its compute stage records, read by the draw pass.
    private readonly bool[] _shown = new bool[RenderPassSetup.FramesInFlight];
    private RenderPassSetup? _setup;
    private GpuBuffer? _frames;
    private GpuBuffer? _blocks;
    private GpuBuffer? _blades;
    private GpuBuffer? _draws;
    private GpuBuffer? _indices;
    private RenderPassBufferSet? _set;
    private PipelineLayout _layout;
    private Pipeline _place;
    private Pipeline _count;
    private bool _placed;
    private bool _disposed;

    public string Name => "grass place";

    public RenderStage Stage => RenderStage.Compute;

    /// <summary>The grass's set (set 1 of its shaders): the frames, the blocks, the blades, and the draws.</summary>
    internal RenderPassBufferSet? Set => _set;

    /// <summary>The index pattern of <see cref="BladesAnInstance"/> near blades, then of as many far ones (16-bit).</summary>
    internal GpuBuffer? Indices => _indices;

    /// <summary>The two draws, the near one first (indexed indirect commands, 20 bytes each).</summary>
    internal GpuBuffer? Draws => _draws;

    /// <summary>The near pattern's indices: the far pattern's first index.</summary>
    public static int NearIndices => BladesAnInstance * NearTriangles * 3;

    private RenderPassSetup Setup =>
        _setup ?? throw new InvalidOperationException("The grass's place pass has not been initialised.");

    public void Initialize(RenderPassSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        var slots = (ulong)RenderPassSetup.FramesInFlight;
        _frames = setup.Allocator.CreateBuffer(
            s_frameBytes * slots,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "grass frames"
        );
        _blocks = setup.Allocator.CreateBuffer(
            s_blocksBytes * slots,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.Stream,
            "grass blocks"
        );
        _blades = setup.Allocator.CreateBuffer(
            (ulong)(NearMost + FarMost) * BladeBytes,
            BufferUsageFlags.StorageBufferBit,
            MemoryUsage.DeviceLocal,
            "grass blades"
        );
        _draws = setup.Allocator.CreateBuffer(
            DrawsBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryUsage.DeviceLocal,
            "grass draws"
        );
        var indexCount = NearIndices + BladesAnInstance * FarCorners;
        _indices = setup.Allocator.CreateBuffer(
            (ulong)(indexCount * sizeof(ushort)),
            BufferUsageFlags.IndexBufferBit,
            MemoryUsage.Stream,
            "grass indices"
        );
        WritePattern(new Span<ushort>(_indices.Mapped, indexCount));
        _indices.Flush(0, (ulong)(indexCount * sizeof(ushort)));
        _set = setup.CreateBufferSet([_frames, _blocks, _blades, _draws], SetStages, "grass");
        var sets = stackalloc DescriptorSetLayout[2];
        sets[0] = setup.MeshSetLayout;
        sets[1] = _set.Layout;
        _layout = setup.CreateLayout(
            new ReadOnlySpan<DescriptorSetLayout>(sets, 2),
            ShaderStageFlags.ComputeBit,
            (uint)sizeof(ViewConstants),
            "grass place layout"
        );
        ReplacePipelines(CompileShaders());
    }

    public void Write(int slot, in RenderPassFrame frame)
    {
        _active[slot] = false;
        if (
            _frames is null
            || _blocks is null
            || frame.View.Orthographic
            || !source.TryGetLook(out var look)
            || look.Density <= 0
            || look.Radius <= 0
        )
        {
            return;
        }

        var radius = look.Radius;
        var reach = radius + BlockReach * meshes.FinestBlockMetres;
        var into = new Span<GrassBlock>(_blocks.Mapped + s_blocksBytes * (ulong)slot, MostBlocks);
        var listed = ListBlocks(meshes.CameraCommands, meshes.CameraKeys, reach * reach, into, out var triangles);
        if (triangles == 0)
        {
            return;
        }

        // Written once, whole: the memory is the device's, never read back.
        *(GrassFrame*)(_frames.Mapped + s_frameBytes * (ulong)slot) = FrameOf(
            in look,
            (uint)listed,
            triangles,
            frame.Time.TotalSeconds
        );
        _triangles[slot] = triangles;
        _active[slot] = true;
    }

    public void Record(in RenderPassContext context)
    {
        var slot = context.Slot;
        _shown[slot] = false;
        if (
            !_active[slot]
            || _frames is null
            || _blocks is null
            || _set is null
            || _draws is null
            || _place.Handle == 0
        )
        {
            return;
        }

        // The draw's vertices read the frame's numbers too.
        _frames.Flush(s_frameBytes * (ulong)slot, s_frameBytes);
        if (!context.Fresh)
        {
            // Presented again: the last blades made are still there.
            _shown[slot] = _placed;
            return;
        }

        _blocks.Flush(s_blocksBytes * (ulong)slot, s_blocksBytes);
        var vk = Setup.Context.Vk;
        var commandBuffer = context.CommandBuffer;
        // The last frame's draws, indirect reads, and writes finish before this frame writes what they read.
        Barrier(
            commandBuffer,
            PipelineStageFlags2.VertexShaderBit
                | PipelineStageFlags2.DrawIndirectBit
                | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ClearBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
        );
        vk.CmdFillBuffer(commandBuffer, _draws.Buffer, PlacedOffset, 2 * sizeof(uint), 0);
        Barrier(
            commandBuffer,
            PipelineStageFlags2.ClearBit,
            AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
        );
        var sets = stackalloc DescriptorSet[2];
        sets[0] = context.MeshSet;
        sets[1] = _set.Set;
        var view = context.View;
        vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Compute, _layout, 0, 2, sets, 0, null);
        vk.CmdPushConstants(commandBuffer, _layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ViewConstants), &view);
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _place);
        vk.CmdDispatch(commandBuffer, (_triangles[slot] + GroupSize - 1) / GroupSize, 1, 1);
        Barrier(
            commandBuffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit
        );
        vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, _count);
        vk.CmdDispatch(commandBuffer, 1, 1, 1);
        // The blades, the counts, and the draws are read by this frame's draw, its indirect commands, and its vertices.
        Barrier(
            commandBuffer,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.VertexShaderBit,
            AccessFlags2.IndirectCommandReadBit | AccessFlags2.ShaderStorageReadBit
        );
        _placed = true;
        _shown[slot] = true;
    }

    /// <summary>True when the frame of slot <paramref name="slot"/> draws blades: its compute stage placed them, or a frame presented again keeps the last. Render thread.</summary>
    internal bool Shows(int slot) => _shown[slot];

    public void OnTargetsChanged(RenderPassSetup setup) { }

    public object CompileShaders() => new[] { Setup.Shaders.Compile(PlaceShader), Setup.Shaders.Compile(CountShader) };

    public int ReplacePipelines(object compiled)
    {
        var shaders = (byte[][])compiled;
        DestroyPipelines();
        _place = Setup.CreateCompute(shaders[0], _layout, "grass place");
        _count = Setup.CreateCompute(shaders[1], _layout, "grass count");
        return 2;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyPipelines();
        if (_setup is { Context: { IsAbandoned: false } context } && _layout.Handle != 0)
        {
            context.Vk.DestroyPipelineLayout(context.Device, _layout, null);
        }

        _layout = default;
        _set?.Dispose();
        _set = null;
        _indices?.Dispose();
        _indices = null;
        _draws?.Dispose();
        _draws = null;
        _blades?.Dispose();
        _blades = null;
        _blocks?.Dispose();
        _blocks = null;
        _frames?.Dispose();
        _frames = null;
    }

    /// <summary>
    /// The frame's numbers for <paramref name="look"/>, over <paramref name="blocks"/> blocks of
    /// <paramref name="triangles"/> triangles at <paramref name="seconds"/>: its angles in radians or cosines, its wind's
    /// heading as a way on the ground, its full density no farther out than its radius.
    /// </summary>
    public static GrassFrame FrameOf(in GrassLook look, uint blocks, uint triangles, double seconds)
    {
        var heading = look.WindHeadingDegrees * (MathF.PI / 180);
        return new GrassFrame
        {
            Blocks = blocks,
            Triangles = triangles,
            Material = look.Material,
            NearMost = NearMost,
            FarMost = FarMost,
            Radius = look.Radius,
            FullMetres = MathF.Min(look.FullMetres, look.Radius),
            NearMetres = look.NearMetres,
            Density = look.Density,
            Height = look.Height,
            HeightSpread = look.HeightSpread,
            Width = look.Width,
            LeastPixels = look.LeastPixels,
            LeastUp = MathF.Cos(look.SteepestDegrees * (MathF.PI / 180)),
            Lean = look.LeanDegrees * (MathF.PI / 180),
            Wind = look.Wind,
            WindSpeed = look.WindSpeed,
            // North is -z and west -x (a heading of 90 degrees).
            WindWay = new(-MathF.Sin(heading), -MathF.Cos(heading)),
            Seconds = (float)(seconds % RepeatSeconds),
            TipLight = look.TipLight,
            RootShade = look.RootShade,
            ColourSpread = look.ColourSpread,
            HueTolerance = look.HueTolerance,
        };
    }

    /// <summary>
    /// Lists into <paramref name="into"/> the camera's commands whose blocks' centres lie within the square root of
    /// <paramref name="reachSquared"/> metres (<paramref name="keys"/>: their squared distances, nearest first), up to its
    /// length, each with the triangles of those before it, which <paramref name="triangles"/> gives in all. Writes each
    /// block once, in order, and reads nothing back. Allocates nothing.
    /// </summary>
    public static int ListBlocks(
        ReadOnlySpan<DrawIndexedIndirectCommand> commands,
        ReadOnlySpan<float> keys,
        float reachSquared,
        Span<GrassBlock> into,
        out uint triangles
    )
    {
        triangles = 0;
        var listed = 0;
        var count = Math.Min(commands.Length, keys.Length);
        for (var i = 0; i < count && listed < into.Length; i++)
        {
            if (keys[i] > reachSquared)
            {
                // Nearest first: the rest lie farther.
                break;
            }

            var command = commands[i];
            var blockTriangles = command.IndexCount / 3;
            into[listed++] = new GrassBlock
            {
                FirstIndex = command.FirstIndex,
                Triangles = blockTriangles,
                Draw = command.FirstInstance,
                FirstTriangle = triangles,
            };
            triangles += blockTriangles;
        }

        return listed;
    }

    /// <summary>
    /// The index pattern into <paramref name="into"/>: <see cref="BladesAnInstance"/> near blades, each two triangles a
    /// segment up its three pairs of corners and one to its tip, then as many far ones, one triangle each; each blade's
    /// corners numbered from its own first (blade times its corners).
    /// </summary>
    public static void WritePattern(Span<ushort> into)
    {
        var at = 0;
        for (var blade = 0; blade < BladesAnInstance; blade++)
        {
            var first = blade * NearCorners;
            for (var pair = 0; pair < 2; pair++)
            {
                var low = first + 2 * pair;
                into[at++] = (ushort)low;
                into[at++] = (ushort)(low + 1);
                into[at++] = (ushort)(low + 2);
                into[at++] = (ushort)(low + 2);
                into[at++] = (ushort)(low + 1);
                into[at++] = (ushort)(low + 3);
            }

            into[at++] = (ushort)(first + 4);
            into[at++] = (ushort)(first + 5);
            into[at++] = (ushort)(first + 6);
        }

        for (var blade = 0; blade < BladesAnInstance; blade++)
        {
            var first = blade * FarCorners;
            into[at++] = (ushort)first;
            into[at++] = (ushort)(first + 1);
            into[at++] = (ushort)(first + 2);
        }
    }

    private void Barrier(
        CommandBuffer commandBuffer,
        PipelineStageFlags2 sourceStages,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 targetStages,
        AccessFlags2 targetAccess
    )
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = sourceStages,
            SrcAccessMask = sourceAccess,
            DstStageMask = targetStages,
            DstAccessMask = targetAccess,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        Setup.Context.Vk.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    private void DestroyPipelines()
    {
        if (_setup is { Context: { IsAbandoned: false } context })
        {
            if (_place.Handle != 0)
            {
                context.Vk.DestroyPipeline(context.Device, _place, null);
            }

            if (_count.Handle != 0)
            {
                context.Vk.DestroyPipeline(context.Device, _count, null);
            }
        }

        _place = default;
        _count = default;
    }
}
