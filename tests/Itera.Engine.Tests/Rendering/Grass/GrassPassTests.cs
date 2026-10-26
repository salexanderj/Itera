using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Grass;
using Itera.Engine.Rendering.Shaders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering.Grass;

/// <summary>
/// The grass's passes (<see cref="GrassPlacePass"/>, <see cref="GrassDrawPass"/>) without a device: their shaders compile
/// and read <see cref="GrassFrame"/> and <see cref="GrassBlock"/> as the CPU writes them, in set 1; a game's look becomes
/// the frame's numbers; the camera's blocks are listed nearest first within the field's reach, each with the triangles
/// before it, up to the room, allocating nothing; the index pattern numbers each blade's corners from its own first; and
/// a game registers the source and both passes, the place pass first, once.
/// </summary>
public sealed class GrassPassTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "itera-tests", Guid.NewGuid().ToString("N"));
    private readonly ShaderCompiler _compiler;

    public GrassPassTests()
    {
        _compiler = new ShaderCompiler(NullLogger<ShaderCompiler>.Instance, _cache);
        _compiler.Initialize();
    }

    public void Dispose()
    {
        _compiler.Dispose();
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public void Its_shaders_read_the_frame_and_the_blocks_as_the_cpu_writes_them_in_set_1()
    {
        var place = _compiler.Compile(GrassPlacePass.PlaceShader);
        var count = _compiler.Compile(GrassPlacePass.CountShader);
        var vertex = _compiler.Compile(GrassDrawPass.VertexShader);
        var fragment = _compiler.Compile(GrassDrawPass.FragmentShader);

        Assert.Empty(GpuLayout.ArrayElementDifferences<GrassFrame>(place, 1, 0));
        Assert.Empty(GpuLayout.ArrayElementDifferences<GrassBlock>(place, 1, 1));
        Assert.Empty(GpuLayout.ArrayElementDifferences<GrassFrame>(count, 1, 0));
        Assert.Empty(GpuLayout.ArrayElementDifferences<GrassFrame>(vertex, 1, 0));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(place));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(vertex));
        Assert.Empty(GpuLayout.PushConstantDifferences<ViewConstants>(fragment));
        Assert.Equal([0u, 1u], GpuLayout.DescriptorSets(place));
        Assert.Equal([1u], GpuLayout.DescriptorSets(vertex));
        Assert.Equal([0u], GpuLayout.DescriptorSets(fragment));
    }

    [Fact]
    public void A_look_becomes_the_frames_numbers_in_radians_cosines_and_a_way_on_the_ground()
    {
        var look = new GrassLook
        {
            Material = 7,
            Radius = 20,
            FullMetres = 25,
            SteepestDegrees = 60,
            LeanDegrees = 90,
            WindHeadingDegrees = 90,
            HueTolerance = 0.1f,
        };

        var frame = GrassPlacePass.FrameOf(in look, 3, 600, 8192 + 1.5);

        Assert.Equal((3u, 600u, 7u), (frame.Blocks, frame.Triangles, frame.Material));
        // The full density reaches no farther than the field.
        Assert.Equal(20, frame.FullMetres);
        Assert.Equal(0.5f, frame.LeastUp, 5);
        Assert.Equal(MathF.PI / 2, frame.Lean, 5);
        // A heading of 90 degrees blows west, along -x.
        Assert.Equal(-1, frame.WindWay.X, 5);
        Assert.Equal(0, frame.WindWay.Y, 5);
        Assert.Equal(1.5f, frame.Seconds, 3);
        Assert.Equal(0.1f, frame.HueTolerance);
        Assert.Equal(((uint)GrassPlacePass.NearMost, (uint)GrassPlacePass.FarMost), (frame.NearMost, frame.FarMost));
    }

    [Fact]
    public void The_cameras_blocks_within_reach_are_listed_nearest_first_with_the_triangles_before_each()
    {
        DrawIndexedIndirectCommand[] commands =
        [
            new(30, 1, 100, 0, 7),
            new(60, 1, 400, 0, 3),
            new(90, 1, 900, 0, 12),
            new(300, 1, 2000, 0, 5),
        ];
        float[] keys = [10, 200, 899, 901];
        var into = new GrassBlock[4];

        var listed = GrassPlacePass.ListBlocks(commands, keys, 900, into, out var triangles);

        Assert.Equal(3, listed);
        Assert.Equal(60u, triangles);
        Assert.Equal(
            [(100u, 10u, 7u, 0u), (400u, 20u, 3u, 10u), (900u, 30u, 12u, 30u)],
            into[..3].Select(b => (b.FirstIndex, b.Triangles, b.Draw, b.FirstTriangle))
        );
    }

    [Fact]
    public void A_full_list_stops_at_its_room()
    {
        var commands = new DrawIndexedIndirectCommand[5];
        Array.Fill(commands, new DrawIndexedIndirectCommand(6, 1, 0, 0, 0));
        var keys = new float[5];
        var into = new GrassBlock[2];

        Assert.Equal(2, GrassPlacePass.ListBlocks(commands, keys, 100, into, out var triangles));
        Assert.Equal(4u, triangles);
    }

    [Fact]
    public void Listing_the_blocks_allocates_nothing()
    {
        var commands = new DrawIndexedIndirectCommand[64];
        Array.Fill(commands, new DrawIndexedIndirectCommand(3000, 1, 0, 0, 0));
        var keys = new float[64];
        var into = new GrassBlock[GrassPlacePass.MostBlocks];
        GrassPlacePass.ListBlocks(commands, keys, 100, into, out _);

        var start = SystemAllocation.StartMeasurement();
        GrassPlacePass.ListBlocks(commands, keys, 100, into, out _);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Fact]
    public void The_pattern_numbers_each_blades_corners_from_its_own_first()
    {
        var pattern = new ushort[
            GrassPlacePass.NearIndices + GrassPlacePass.BladesAnInstance * GrassPlacePass.FarCorners
        ];

        GrassPlacePass.WritePattern(pattern);

        // A near blade: two triangles a segment up its three pairs, and one to its tip.
        Assert.Equal([0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5, 4, 5, 6], pattern[..15].Select(i => (int)i));
        Assert.Equal([7, 8, 9], pattern[15..18].Select(i => (int)i));
        var near = pattern[..GrassPlacePass.NearIndices];
        Assert.Equal(GrassPlacePass.BladesAnInstance * GrassPlacePass.NearCorners - 1, near.Max());
        // The far blades: one triangle each.
        var far = pattern[GrassPlacePass.NearIndices..];
        Assert.Equal([0, 1, 2, 3, 4, 5], far[..6].Select(i => (int)i));
        Assert.Equal(GrassPlacePass.BladesAnInstance * GrassPlacePass.FarCorners - 1, far.Max());
    }

    [Fact]
    public void A_game_registers_its_source_and_the_place_pass_before_the_draw_pass_once()
    {
        var services = new ServiceCollection();

        services.AddIteraGrass<NoGrass>();
        services.AddIteraGrass<NoGrass>();

        Assert.Single(services, d => d.ServiceType == typeof(IGrassSource));
        var passes = services.Where(d => d.ServiceType == typeof(IRenderPass)).ToList();
        Assert.Equal(2, passes.Count);
        Assert.NotNull(passes[0].ImplementationFactory);
        Assert.Equal(typeof(GrassDrawPass), passes[1].ImplementationType);
    }

    private sealed class NoGrass : IGrassSource
    {
        public bool TryGetLook(out GrassLook look)
        {
            look = default;
            return false;
        }
    }
}
