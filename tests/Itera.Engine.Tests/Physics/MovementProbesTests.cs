using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// <see cref="MovementProbes"/> over mesh colliders with no voxel world: a wall ahead within reach, with its normal, tags,
/// distance, and the rays that met it; none past the reach or behind; a low wall meets fewer rays; a wall's top within
/// reach is a ledge, and a taller one has none; room to stand on the top but not inside the wall; and the probes
/// allocate nothing.
/// </summary>
public sealed class MovementProbesTests : IDisposable
{
    private const uint Climbable = 4;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);
    private readonly MeshColliders _colliders;
    private readonly MovementProbes _probes;

    public MovementProbesTests()
    {
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
        new TestTriangles().Ground(-5, -5, 5, 5, 0).AddTo(_colliders);
        var mover = new VoxelCharacterMover(new NothingPassable(), _colliders, cellCentimetres: 10);
        _probes = new MovementProbes(new CollisionSources(_colliders, new NoVoxelSurfaceTags()), mover);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
    }

    [Fact]
    public void A_wall_ahead_within_reach_is_found_with_its_normal_tags_and_distance()
    {
        // A 2 m wall whose face lies at x = 1 m; the body's side is 0.24 m from it.
        Wall(height: 2);
        var feet = new Vector3(0.5f, 0, 0.1f);

        Assert.True(_probes.TryWallAhead(feet, in s_shape, Vector2.UnitX, 0.3f, out var wall));
        Assert.False(_probes.TryWallAhead(feet, in s_shape, Vector2.UnitX, 0.2f, out _));
        Assert.False(_probes.TryWallAhead(feet, in s_shape, -Vector2.UnitX, 3, out _));
        Assert.False(_probes.TryWallAhead(feet, in s_shape, Vector2.Zero, 3, out _));
        Assert.Equal((Climbable, 3), (wall.Tags, wall.Rays));
        Assert.True(Vector2.Distance(-Vector2.UnitX, wall.Normal) < 1e-4f, $"Normal {wall.Normal}.");
        Assert.Equal(0.24f, wall.DistanceMetres, 1e-3f);
        Assert.Equal(1, wall.Point.X, 1e-3f);
    }

    [Fact]
    public void A_low_wall_meets_only_the_lowest_ray()
    {
        Wall(height: 0.5f);

        Assert.True(_probes.TryWallAhead(new Vector3(0.5f, 0, 0.1f), in s_shape, Vector2.UnitX, 0.3f, out var wall));

        Assert.Equal(1, wall.Rays);
    }

    [Fact]
    public void A_walls_top_within_reach_is_a_ledge_with_room_to_stand_and_a_taller_one_has_none()
    {
        var low = Wall(height: 2);
        var feet = new Vector3(0.5f, 0, 0.1f);
        Assert.True(_probes.TryWallAhead(feet, in s_shape, Vector2.UnitX, 0.3f, out var wall));

        Assert.True(_probes.TryLedgeTop(feet, in s_shape, in wall, 0.5f, 0.3f, out var top));
        Assert.Equal(2, top.Y, 1e-3f);
        Assert.True(_probes.HasRoom(new Vector3(1.5f, 2, 0.1f), in s_shape));
        Assert.False(_probes.HasRoom(new Vector3(1.5f, 1, 0.1f), in s_shape));

        // A 4 m wall: the ray down starts inside it, and meets nothing above the wall's point.
        _colliders.Remove(low);
        Wall(height: 4);
        Assert.True(_probes.TryWallAhead(feet, in s_shape, Vector2.UnitX, 0.3f, out var tall));
        Assert.False(_probes.TryLedgeTop(feet, in s_shape, in tall, 0.5f, 0.3f, out _));
    }

    [Fact]
    public void The_probes_allocate_nothing()
    {
        Wall(height: 2);
        var feet = new Vector3(0.5f, 0, 0.1f);
        _probes.TryWallAhead(feet, in s_shape, Vector2.UnitX, 0.3f, out var wall);
        _probes.TryLedgeTop(feet, in s_shape, in wall, 0.5f, 0.3f, out _);
        _probes.HasRoom(new Vector3(1.5f, 2, 0.1f), in s_shape);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            _probes.TryWallAhead(feet, in s_shape, Vector2.UnitX, 0.3f, out wall);
            _probes.TryLedgeTop(feet, in s_shape, in wall, 0.5f, 0.3f, out _);
            _probes.HasRoom(new Vector3(1.5f, 2, 0.1f), in s_shape);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>A box from x = 1 to 2 m and z = -1 to 1 m, <paramref name="height"/> metres tall, tagged climbable.</summary>
    private int Wall(float height) =>
        new TestTriangles().Box(new Vector3(1, 0, -1), new Vector3(2, height, 1)).AddTo(_colliders, Climbable);
}

/// <summary>
/// <see cref="MovementProbes"/> over voxels (the 1 cm wall of <see cref="MoverWorldFixture"/>, 4 m tall, its stone tagged
/// through <see cref="IVoxelSurfaceTags"/>): a wall of voxel faces is found with its face's normal and its material's
/// tags, and has no ledge within reach.
/// </summary>
public sealed class MovementProbesVoxelTests(MoverWorldFixture fixture) : IClassFixture<MoverWorldFixture>, IDisposable
{
    private const uint Climbable = 2;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    private readonly PhysicsWorld _physics = CreatePhysics();

    public void Dispose() => _physics.Dispose();

    [Fact]
    public void A_voxel_wall_is_found_with_its_faces_normal_and_its_materials_tags()
    {
        using var colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
        var probes = new MovementProbes(new CollisionSources(colliders, new StoneTags(), fixture.World), fixture.Mover);
        // The wall's face at x = -2 m; the body's side 0.54 m from it.
        var feet = new Vector3(-2.8f, 2, -2.4f);

        Assert.True(probes.TryWallAhead(feet, in s_shape, Vector2.UnitX, 0.6f, out var wall));
        Assert.False(probes.TryLedgeTop(feet, in s_shape, in wall, 0.5f, 0.1f, out _));
        Assert.Equal((Climbable, 3), (wall.Tags, wall.Rays));
        Assert.Equal(-Vector2.UnitX, wall.Normal);
        Assert.Equal(0.54f, wall.DistanceMetres, 0.011f);
    }

    private static PhysicsWorld CreatePhysics()
    {
        var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        physics.Initialize();
        return physics;
    }

    private sealed class StoneTags : IVoxelSurfaceTags
    {
        public uint TagsOf(byte material) => material == BoxesGenerator.Stone ? Climbable : 0;
    }
}
