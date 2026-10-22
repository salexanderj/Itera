using System.Numerics;
using System.Runtime.InteropServices;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// What the world meshes' test classes share. The slope world, its models, the draw target and staging in memory, and
/// the meshes over them are each made when a test first asks for them, so a test that needs none builds none.
/// </summary>
public abstract class WorldMeshesTestBase(ITestOutputHelper output) : IDisposable
{
    internal const double RadiusMetres = 4;
    internal const int Levels = 2;
    internal static readonly Vector3 Centre = new(0, 2.5f, 0);

    /// <summary>
    /// From 3.5 m over the middle (inside the height of the slope's blocks, so the columns' distances are across them) a
    /// wide view down (<see cref="AboveSpread"/>) sees the level-0 columns round the middle and the level-1 ones past
    /// them.
    /// </summary>
    internal static readonly Vector3 Above = new(0, 3.5f, 0);

    /// <summary>How far the view from <see cref="Above"/> reaches to each side per metre down: about 85 degrees.</summary>
    internal const float AboveSpread = 12;

    private VoxelWorld? _world;
    private VoxelModels? _models;
    private FakeWorldTargets? _targets;
    private WorldMeshes? _meshes;
    private protected int _frame;

    internal ITestOutputHelper Output { get; } = output;

    internal CountingPalette Palette { get; } = new();

    /// <summary>The slope world: 4 m radius round <see cref="Centre"/>, two levels.</summary>
    internal VoxelWorld World =>
        _world ??= TestWorlds.Load(new SlopeGenerator(), Centre, radiusMetres: RadiusMetres, levels: Levels);

    internal VoxelModels Models => _models ??= new VoxelModels(World);

    internal FakeWorldTargets Targets => _targets ??= new FakeWorldTargets();

    /// <summary>The meshes over <see cref="Targets"/>.</summary>
    internal WorldMeshes Meshes => _meshes ??= NewMeshes(Targets);

    internal WorldMeshes NewMeshes(FakeWorldTargets targets, int levels = Levels, float distantShadowMetres = 0)
    {
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = RadiusMetres, Levels = levels },
            Renderer = new RendererSettings { MeshFinestLevel = 0, DistantShadowMetres = distantShadowMetres },
        };
        var meshes = new WorldMeshes(
            Palette,
            targets,
            new MeshView(targets, Options.Create(options)),
            targets,
            Options.Create(options),
            NullLogger<WorldMeshes>.Instance
        );
        meshes.Initialize();
        return meshes;
    }

    /// <summary>
    /// A frame as GameLoop builds it: the view's start, the world's upload and draws, then the view's end with the
    /// events' <paramref name="lights"/>. Returns the copy regions staged.
    /// </summary>
    internal static int Frame(
        WorldMeshes meshes,
        int slot,
        double nowSeconds,
        in ViewConstants view,
        ReadOnlySpan<ModelDraw> models = default,
        ReadOnlySpan<PointLight> lights = default
    )
    {
        meshes.View.Begin(slot, in view);
        var regions = meshes.Upload(slot, nowSeconds, in view, models);
        meshes.View.End(slot, lights);
        return regions;
    }

    public void Dispose()
    {
        _meshes?.Dispose();
        _targets?.Dispose();
        _world?.Dispose();
        GC.SuppressFinalize(this);
    }

    internal void Begin() => Meshes.BeginMatch(World, Models, new NoVoxelTintMap());

    /// <summary>
    /// One frame's upload, its copies applied to the emulated heap, with the sun's shadows when
    /// <paramref name="shadows"/>, and every block from its plain mesh when <paramref name="plain"/>. Returns the slot it
    /// used.
    /// </summary>
    internal int Frame(
        Vector3? at = null,
        bool shadows = false,
        ModelDraw[]? draws = null,
        bool plain = false,
        VoxelCoord origin = default
    )
    {
        var slot = _frame++ % FrameSync.FramesInFlight;
        var view = at == Above ? LookingDown(Above, AboveSpread) : LookingDown(at ?? Centre);
        (view.OriginX, view.OriginY, view.OriginZ) = (origin.X, origin.Y, origin.Z);
        if (shadows)
        {
            view.SunDirection = ViewConstants.DefaultSunDirection;
            view.Switches = ViewSwitches.Shadows;
        }

        if (plain)
        {
            view.Switches |= ViewSwitches.Plain;
        }

        var count = Frame(Meshes, slot, _frame / 60.0, in view, draws);
        Targets.Apply(Meshes.Regions(slot, count));
        return slot;
    }

    /// <summary>Frames, with the world updated, until <paramref name="done"/> holds.</summary>
    internal void RunUntil(Func<bool> done, ModelDraw[]? draws = null, Vector3? at = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
            World.Update(0);
            Frame(at, draws: draws);
            Thread.Sleep(1);
        }
    }

    internal void RunUntilUpToDate(Vector3? at = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
            World.Update(0);
            Frame(at);
            Thread.Sleep(1);
        } while (!Meshes.IsUpToDate);
    }

    /// <summary>The index of the draw a command names in its slot.</summary>
    internal static int RecordOf(DrawIndexedIndirectCommand command, int slot) =>
        (int)command.FirstInstance - slot * MeshPass.MaxRecords;

    /// <summary>
    /// A view at <paramref name="at"/> looking straight down, with a 90 degree field of view, or wider: reaching
    /// <paramref name="spread"/> metres to each side per metre down.
    /// </summary>
    internal static ViewConstants LookingDown(Vector3 at, float spread = 1) =>
        new()
        {
            Position = at,
            Forward = new Vector3(0, -1, 0),
            Right = new Vector3(spread, 0, 0),
            Up = new Vector3(0, 0, -spread),
            MaxDistance = 100,
        };

    /// <summary>A model of one solid cube of <paramref name="size"/> voxels a side, at level 0 alone.</summary>
    internal int AddCube(int size) => Models.Add([Models.Encode(CubeVoxels(size))], new VoxelCoord(size, size, size));

    /// <summary>A chunk's voxels holding a solid cube of material 1, <paramref name="size"/> voxels a side, at its corner.</summary>
    internal static byte[] CubeVoxels(int size)
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    voxels[x + (y << VoxelConstants.ChunkShift) + (z << (2 * VoxelConstants.ChunkShift))] = 1;
                }
            }
        }

        return voxels;
    }
}

internal sealed class CountingPalette : IVoxelPalette
{
    public int Prepared { get; private set; }

    public void Prepare() => Prepared++;

    public VoxelLook Look(byte material) => VoxelLook.Plain(new Vector3(material / 255f, 0.5f, 0.25f));
}

/// <summary>The draw target and staging in memory, and the heap emulated by applying the copy regions.</summary>
internal sealed unsafe class FakeWorldTargets(int heapBytes = 64 << 20) : IMeshDrawTarget, IStagingRegions, IDisposable
{
    private const ulong RegionSize = 8UL << 20;
    private readonly byte* _staging = (byte*)NativeMemory.AllocZeroed((nuint)(RegionSize * FrameSync.FramesInFlight));
    private readonly MeshDraw[][] _draws = [new MeshDraw[MeshPass.MaxRecords], new MeshDraw[MeshPass.MaxRecords]];
    private readonly DrawIndexedIndirectCommand[][] _commands = CommandLists();
    private readonly MeshLighting[] _shadows = new MeshLighting[FrameSync.FramesInFlight];
    private readonly MeshLight[] _lights = new MeshLight[FrameSync.FramesInFlight * MeshPass.MostLights];
    private readonly MeshModelDraw[][] _modelDraws = Lists<MeshModelDraw>(MeshPass.MaxModelRecords);
    private readonly DrawIndexedIndirectCommand[][] _modelCommands = Lists<DrawIndexedIndirectCommand>(
        MeshPass.MaxModelCommands
    );

    public byte[] Heap { get; } = new byte[heapBytes];

    public ulong HeapBytes => (ulong)Heap.Length;

    public ulong RegionBytes => RegionSize;

    public ulong RegionOffset(int slot) => (ulong)slot * RegionSize;

    public byte* Region(int slot) => _staging + RegionOffset(slot);

    /// <summary>
    /// Checks the size with a plain comparison: <c>Assert.InRange</c> makes a comparer and boxes its values in
    /// unoptimised code, which the allocation tests measure as the engine's while the JIT has not optimised it yet.
    /// </summary>
    public void Flush(int slot, ulong bytes) => Assert.True(bytes <= RegionSize);

    /// <summary>Byte <paramref name="index"/> of what was staged at <paramref name="offset"/> of the staging buffer.</summary>
    public byte Staged(ulong offset, int index) => _staging[offset + (ulong)index];

    public Span<MeshDraw> DrawsOf(int slot) => _draws[slot];

    public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) =>
        _commands[slot * MeshPass.WorldLists + list];

    public ref MeshLighting LightingOf(int slot) => ref _shadows[slot];

    public Span<MeshLight> LightsOf(int slot) => _lights.AsSpan(slot * MeshPass.MostLights, MeshPass.MostLights);

    public Span<MeshModelDraw> ModelDrawsOf(int slot, int list) => _modelDraws[slot * MeshPass.Lists + list];

    public Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list) =>
        _modelCommands[slot * MeshPass.Lists + list];

    /// <summary>
    /// Applies a frame's copy regions to the heap, after checking that no two write the same place: one copy
    /// command's destination regions must not overlap (VUID-vkCmdCopyBuffer-pRegions-12482 in the Vulkan spec).
    /// </summary>
    public void Apply(ReadOnlySpan<BufferCopy> regions)
    {
        var sorted = regions.ToArray();
        Array.Sort(sorted, static (a, b) => a.DstOffset.CompareTo(b.DstOffset));
        for (var i = 1; i < sorted.Length; i++)
        {
            Assert.True(
                sorted[i - 1].DstOffset + sorted[i - 1].Size <= sorted[i].DstOffset,
                $"Two copies of one frame write heap bytes {sorted[i].DstOffset} on."
            );
        }

        foreach (var region in regions)
        {
            Assert.True(region.DstOffset + region.Size <= HeapBytes);
            new ReadOnlySpan<byte>(_staging + region.SrcOffset, (int)region.Size).CopyTo(
                Heap.AsSpan((int)region.DstOffset, (int)region.Size)
            );
        }
    }

    public void Dispose() => NativeMemory.Free(_staging);

    private static T[][] Lists<T>(int length)
    {
        var lists = new T[FrameSync.FramesInFlight * MeshPass.Lists][];
        for (var i = 0; i < lists.Length; i++)
        {
            lists[i] = new T[length];
        }

        return lists;
    }

    private static DrawIndexedIndirectCommand[][] CommandLists()
    {
        var lists = new DrawIndexedIndirectCommand[FrameSync.FramesInFlight * MeshPass.WorldLists][];
        for (var i = 0; i < lists.Length; i++)
        {
            lists[i] = new DrawIndexedIndirectCommand[MeshPass.MaxDraws];
        }

        return lists;
    }
}
