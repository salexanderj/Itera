using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// <see cref="VoxelWorld.Raycast"/> with <c>coarse</c> (a round's flight): where level 0 is not resident it reads the
/// finest level that is, so a far wall stops the ray; where level 0 is resident it gives the plain ray's hit.
/// </summary>
public sealed class VoxelCoarseRaycastTests : IDisposable
{
    private const int Floor = 0;

    // A stone block 1 m wide and tall from 8 m out: past level 0 (3 m) and level 1 (6 m), within level 2 (12 m).
    private static readonly VoxelBox s_wall = new(800, Floor, -50, 899, Floor + 99, 49, BoxesGenerator.Stone);

    private readonly VoxelWorld _world = TestWorlds.Load(
        new BoxesGenerator(Floor, s_wall),
        new Vector3(0, 1, 0),
        radiusMetres: 3,
        levels: 3
    );

    public void Dispose() => _world.Dispose();

    [Fact]
    public void A_far_wall_stops_a_coarse_ray_at_its_level()
    {
        var origin = new Vector3(0.005f, 0.505f, 0.005f);

        Assert.False(_world.Raycast(origin, Vector3.UnitX, 20, out _));
        Assert.True(_world.Raycast(origin, Vector3.UnitX, 20, out var hit, coarse: true));

        Assert.Equal(2, hit.Level);
        Assert.Equal(BoxesGenerator.Stone, hit.Material);
        Assert.Equal(new VoxelCoord(-1, 0, 0), hit.Normal);
        Assert.Equal(8, hit.DistanceMetres, 0.04f);
    }

    [Fact]
    public void The_ground_far_out_is_met_at_a_coarse_level()
    {
        var origin = new Vector3(10.005f, 2.005f, 1.005f);

        Assert.True(_world.Raycast(origin, -Vector3.UnitY, 5, out var hit, coarse: true));

        Assert.Equal(2, hit.Level);
        Assert.Equal(BoxesGenerator.Dirt, hit.Material);
        Assert.Equal(new VoxelCoord(0, 1, 0), hit.Normal);
        Assert.Equal(2, hit.DistanceMetres, 0.04f);
    }

    /// <summary>
    /// A ray from inside level 0 down to the ground past its ring meets the ground at level 1, where it is
    /// (no ray crossed the ring into the ground).
    /// </summary>
    [Fact]
    public void A_ray_down_past_the_level_0_ring_meets_the_ground_at_level_1()
    {
        // From 10.5 cm up at the centre, falling 2.1 cm a metre: it meets the ground 5 m out, past level 0's 3 m.
        var origin = new Vector3(0.005f, 0.105f, 0.005f);
        var direction = new Vector3(1, -0.021f, 0);

        Assert.False(_world.Raycast(origin, direction, 7, out _));
        Assert.True(_world.Raycast(origin, direction, 7, out var hit, coarse: true));

        Assert.Equal(1, hit.Level);
        Assert.Equal(BoxesGenerator.Dirt, hit.Material);
        Assert.Equal(new VoxelCoord(0, 1, 0), hit.Normal);
        Assert.Equal(5, hit.DistanceMetres, 0.05f);
    }

    [Fact]
    public void Within_level_0_a_coarse_ray_hits_what_a_plain_one_does()
    {
        var random = new Random(41);
        var hits = 0;
        for (var i = 0; i < 500; i++)
        {
            var origin = new Vector3(
                random.NextSingle() * 3 - 1.5f,
                random.NextSingle() * 1.5f + 0.01f,
                random.NextSingle() * 3 - 1.5f
            );
            var direction = Vector3.Normalize(
                new Vector3(random.NextSingle() - 0.5f, -random.NextSingle() - 0.1f, random.NextSingle() - 0.5f)
            );
            var plain = _world.Raycast(origin, direction, 1, out var expected);
            var coarse = _world.Raycast(origin, direction, 1, out var hit, coarse: true);

            Assert.Equal(plain, coarse);
            Assert.Equal(expected, hit);
            hits += plain ? 1 : 0;
        }

        Assert.True(hits > 100, $"{hits} hits of 500.");
    }

    /// <summary>
    /// <see cref="VoxelWorld.LineBlocked"/> (a blast's fragments and a sight line passed
    /// through everything past level 0): the far wall blocks a line at any height, and the coarse ground, whose 4 cm
    /// voxels stand a centimetre above the true ground here, blocks no line that skims it or starts inside it.
    /// </summary>
    [Fact]
    public void A_far_wall_blocks_a_line_and_coarse_ground_under_a_skimming_line_does_not()
    {
        // Dirt below y = 3: a level-2 voxel from 0 to 4 cm is sampled at y = 2, dirt, so the coarse ground stands at
        // 4 cm, a centimetre above the true ground at 3 cm.
        var wall = new VoxelBox(800, 3, -50, 899, 102, 49, BoxesGenerator.Stone);
        using var world = TestWorlds.Load(
            new BoxesGenerator(3, wall),
            new Vector3(0, 1, 0),
            radiusMetres: 3,
            levels: 3
        );
        var skim = new Vector3(6.505f, 0.035f, 5.005f);

        Assert.True(world.LineBlocked(new Vector3(0.005f, 0.505f, 0.005f), Vector3.UnitX, 20));
        Assert.True(world.LineBlocked(new Vector3(6.505f, 0.035f, 0.005f), Vector3.UnitX, 4));
        Assert.True(world.Raycast(skim, Vector3.UnitX, 4, out var ground, coarse: true));
        Assert.Equal(2, ground.Level);
        Assert.False(world.LineBlocked(skim, Vector3.UnitX, 4));
        Assert.False(world.LineBlocked(new Vector3(6.505f, 0.5f, 5.005f), Vector3.UnitX, 4));
    }

    [Fact]
    public void Casting_coarse_rays_and_lines_allocates_nothing()
    {
        var origin = new Vector3(0.005f, 0.505f, 0.005f);
        for (var i = 0; i < 20; i++)
        {
            _world.Raycast(origin, new Vector3(1, -0.01f * i, 0.02f * i), 20, out _, coarse: true);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 200; i++)
        {
            _world.Raycast(origin, new Vector3(1, -0.001f * i, 0.002f * i), 20, out _, coarse: true);
            _world.LineBlocked(origin, new Vector3(1, -0.001f * i, 0.002f * i), 20);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
