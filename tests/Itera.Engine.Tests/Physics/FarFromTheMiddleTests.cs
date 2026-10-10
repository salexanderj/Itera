using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Where the tests far from the map's middle run: 150,528 m along x, 1,024 m up, and 150,528 m along -z (whole metres,
/// and whole level-4 columns of 10 cm voxels, so <see cref="TranslatedGenerator"/> can move a world there), with the
/// match's origin there.
/// </summary>
internal static class FarPlace
{
    public static readonly VoxelCoord Centimetres = new(15_052_800, 102_400, -15_052_800);

    public static WorldPoint Point => WorldPoint.FromCentimetres(Centimetres);

    /// <summary>An origin at the far place.</summary>
    public static WorldOrigin Origin()
    {
        var origin = new WorldOrigin();
        origin.MoveTo(Centimetres);
        return origin;
    }

    /// <summary>
    /// A world of <see cref="DensityTerrainGenerator"/>'s ground in 10 cm voxels, at the middle with the origin there, or
    /// moved to the far place with the origin there: either way the same local positions see the same ground.
    /// </summary>
    public static VoxelWorld DensityWorld(bool far, Vector3 centre, double radiusMetres)
    {
        var ground = new DensityTerrainGenerator(new VoxelGrid(10));
        return far
            ? TestWorlds.Load(new TranslatedGenerator(ground, Centimetres), centre, radiusMetres, origin: Origin())
            : TestWorlds.Load(ground, centre, radiusMetres);
    }
}

/// <summary>The ground of <see cref="DensityMoverFixture"/> 150 km out (<see cref="FarPlace"/>), read from an origin there.</summary>
public sealed class DensityMoverFarFixture : IDensityGround, IDisposable
{
    public DensityMoverFarFixture()
    {
        World = FarPlace.DensityWorld(far: true, new Vector3(3, 2, 13), radiusMetres: 16);
        Mover = new VoxelCharacterMover(World, new NothingPassable());
    }

    public VoxelWorld World { get; }

    public VoxelCharacterMover Mover { get; }

    public bool FirstReadsAllocate => false;

    public bool Solid(int x, int y, int z)
    {
        var far = FarPlace.Centimetres;
        World.TryGetMaterial(new VoxelCoord(far.X + x * 10, far.Y + y * 10, far.Z + z * 10), out var material);
        return material != VoxelConstants.Air;
    }

    public void Dispose() => World.Dispose();
}

/// <summary>Every test of <see cref="DensityGroundTests"/> on voxels 150 km from the map's middle.</summary>
public sealed class DensityMoverFarTests(DensityMoverFarFixture fixture, ITestOutputHelper output)
    : DensityGroundTests(fixture, output),
        IClassFixture<DensityMoverFarFixture>;

/// <summary>The triangles of <see cref="MeshDensityGroundFixture"/> placed 150 km out, read from an origin there.</summary>
public sealed class MeshDensityGroundFarFixture : IDensityGround, IDisposable
{
    private readonly MeshDensityGroundFixture _ground = new(FarPlace.Origin(), FarPlace.Point);

    public MeshColliders Colliders => _ground.Colliders;

    public VoxelCharacterMover Mover => _ground.Mover;

    public bool FirstReadsAllocate => true;

    public bool Solid(int x, int y, int z) => _ground.Solid(x, y, z);

    public void Dispose() => _ground.Dispose();
}

/// <summary>Every test of <see cref="DensityGroundTests"/> on mesh colliders 150 km from the map's middle.</summary>
public sealed class MeshDensityGroundFarTests(MeshDensityGroundFarFixture fixture, ITestOutputHelper output)
    : DensityGroundTests(fixture, output),
        IClassFixture<MeshDensityGroundFarFixture>;

/// <summary>
/// The same ground at the map's middle and 150 km out (<see cref="FarPlace"/>), each read from an origin at its own
/// place: rays meet the same voxels moved by the far place, at the same distances; a walk over slopes, stairs, walls,
/// and under a ceiling keeps to the middle's within 0.1 mm (the move's doubles round differently that far out, by about
/// 1e-10 m, so a float may now and then land on its neighbour); a ball rests at the same local place and settles into
/// the voxels the middle's does, moved by the far place; and rays meet mesh colliders placed there where they meet them
/// at the middle.
/// </summary>
public sealed class FarFromTheMiddleTests(ITestOutputHelper output)
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    [Fact]
    public void Rays_meet_the_same_voxels_moved_by_the_far_place_at_the_same_distances()
    {
        using var middle = FarPlace.DensityWorld(far: false, new Vector3(4.5f, 2, 13), radiusMetres: 16);
        using var far = FarPlace.DensityWorld(far: true, new Vector3(4.5f, 2, 13), radiusMetres: 16);
        var random = new Random(3);
        var hits = 0;
        for (var i = 0; i < 2000; i++)
        {
            // From over the strips, at least 10 cm from x = 0 and z = 0 wherever the rays go.
            var start = new Vector3(Between(random, 1.5f, 7.5f), Between(random, 1.6f, 4.5f), Between(random, 2, 26));
            var direction = new Vector3(
                Between(random, -0.35f, 0.35f),
                Between(random, -1, 0.3f),
                Between(random, -0.35f, 0.35f)
            );
            var met = middle.Raycast(start, direction, 4, out var near);
            Assert.Equal(met, far.Raycast(start, direction, 4, out var there));
            if (!met)
            {
                continue;
            }

            hits++;
            Assert.Equal(near.DistanceMetres, there.DistanceMetres);
            Assert.Equal(near.Voxel + FarPlace.Centimetres, there.Voxel);
            Assert.Equal(near.BoxMin + FarPlace.Centimetres, there.BoxMin);
            Assert.Equal(
                (near.Normal, near.Material, near.Level, near.BoxCentimetres),
                (there.Normal, there.Material, there.Level, there.BoxCentimetres)
            );
            Assert.Equal(
                near.Entry(middle.Origin, start, direction) + FarPlace.Centimetres,
                there.Entry(far.Origin, start, direction)
            );
        }

        Assert.True(hits > 1000, $"Only {hits} rays met the ground.");
    }

    [Fact]
    public void A_walk_over_slopes_stairs_and_walls_keeps_to_the_middles()
    {
        using var middle = FarPlace.DensityWorld(far: false, new Vector3(4.5f, 2, 13), radiusMetres: 16);
        using var far = FarPlace.DensityWorld(far: true, new Vector3(4.5f, 2, 13), radiusMetres: 16);
        var near = new VoxelCharacterMover(middle, new NothingPassable());
        var there = new VoxelCharacterMover(far, new NothingPassable());
        var random = new Random(5);
        Vector3 a = new(1.5f, 2, 2),
            b = a,
            velocityA = Vector3.Zero,
            velocityB = Vector3.Zero,
            target = a;
        float worst = 0;
        var same = 0;
        var climbed = 0f;
        for (var i = 0; i < 3000; i++)
        {
            // Towards a new point over the strips every 1.5 s, jumping now and then; both bodies are given the same.
            if (i % 90 == 0)
            {
                target = new Vector3(Between(random, 1.5f, 7.5f), 0, Between(random, 2, 26));
            }

            var towards = new Vector3(target.X - a.X, 0, target.Z - a.Z);
            towards = towards.Length() > 0.1f ? Vector3.Normalize(towards) * 4.5f : Vector3.Zero;
            var jump = i % 70 == 35 ? 6 : 0f;
            velocityA = new Vector3(towards.X, jump > 0 ? jump : velocityA.Y - Gravity * Step, towards.Z);
            velocityB = new Vector3(towards.X, jump > 0 ? jump : velocityB.Y - Gravity * Step, towards.Z);
            var resultA = near.Move(ref a, ref velocityA, in s_shape, Step);
            var resultB = there.Move(ref b, ref velocityB, in s_shape, Step);

            Assert.Equal(resultA.Status, resultB.Status);
            Assert.Equal(resultA.Grounded, resultB.Grounded);
            var apart = Vector3.Distance(a, b);
            Assert.True(apart < 1e-4f, $"Step {i}: the far body is {apart * 1000:F4} mm from the middle's ({a}, {b}).");
            worst = Math.Max(worst, apart);
            same += a == b ? 1 : 0;
            climbed = Math.Max(climbed, a.Y);
        }

        output.WriteLine(
            FormattableString.Invariant($"{same} of 3000 steps alike bit for bit; at most {worst * 1e6:F3} µm apart.")
        );
        // Higher than a jump from the floor (1.53 m and 0.9 m), so it went up a slope or the stairs.
        Assert.True(climbed > 2.6f, $"The walk never left the floor (highest {climbed} m).");
    }

    [Fact]
    public void A_ball_rests_at_the_same_place_and_settles_into_the_middles_voxels_moved_by_the_far_place()
    {
        var (nearPivot, nearEdit) = DropAndSettle(far: false);
        var (farPivot, farEdit) = DropAndSettle(far: true);

        Assert.Equal(nearPivot, farPivot);
        Assert.Equal(nearEdit.Origin + FarPlace.Centimetres, farEdit.Origin);
        Assert.Equal(
            (nearEdit.Shape, nearEdit.Operation, nearEdit.Radius, nearEdit.Material),
            (farEdit.Shape, farEdit.Operation, farEdit.Radius, farEdit.Material)
        );
    }

    [Fact]
    public void Rays_meet_mesh_colliders_placed_far_out_where_they_meet_them_at_the_middle()
    {
        using var middle = new MeshDensityGroundFixture();
        using var far = new MeshDensityGroundFarFixture();
        var random = new Random(9);
        var hits = 0;
        for (var i = 0; i < 2000; i++)
        {
            var start = new Vector3(Between(random, 1.5f, 7.5f), Between(random, 1.6f, 4.5f), Between(random, 2, 26));
            var direction = new Vector3(
                Between(random, -0.35f, 0.35f),
                Between(random, -1, 0.3f),
                Between(random, -0.35f, 0.35f)
            );
            var met = middle.Colliders.Raycast(start, direction, 4, out var near);
            Assert.Equal(met, far.Colliders.Raycast(start, direction, 4, out var there));
            if (!met)
            {
                continue;
            }

            hits++;
            Assert.Equal((near.Point, near.Normal, near.Distance), (there.Point, there.Normal, there.Distance));
            Assert.Equal(near.Triangle, there.Triangle);
        }

        Assert.True(hits > 1000, $"Only {hits} rays met the triangles.");
    }

    /// <summary>A ball thrown along the floor until it rests, then settled into the world; where it rested, and its edit.</summary>
    private static (Vector3 Pivot, VoxelEdit Edit) DropAndSettle(bool far)
    {
        using var world = FarPlace.DensityWorld(far, new Vector3(2, 2, 1.5f), radiusMetres: 6);
        var options = Options.Create(new EngineOptions { Physics = { Threads = 1 } });
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, options);
        physics.Initialize();
        var passability = new NothingPassable();
        using var colliders = new VoxelColliders(physics, world, passability, NullLogger<VoxelColliders>.Instance);
        colliders.Initialize();
        var bodies = new LooseBodies(
            physics,
            world,
            colliders,
            new VoxelModels(world),
            passability,
            options,
            NullLogger<LooseBodies>.Instance
        );
        Assert.True(
            bodies.TryCreateBall(
                12,
                DensityTerrainGenerator.Stone,
                new Vector3(0.7f, 2.4f, 1.3f),
                new Vector3(1.5f, 0, 0.4f),
                out var body
            )
        );
        var steps = 0;
        var resting = 0;
        while (resting < 60)
        {
            Assert.True(++steps < 1800, $"The ball did not rest; it is at {bodies.PivotOf(body)}.");
            colliders.Update(Step);
            physics.Step(Step);
            resting = bodies.IsAtRest(body) ? resting + 1 : 0;
        }

        var pivot = bodies.PivotOf(body);
        var edit = world.GetEdit(bodies.Settle(body));
        return (pivot, edit);
    }

    private static float Between(Random random, float low, float high) =>
        low + (float)random.NextDouble() * (high - low);
}
