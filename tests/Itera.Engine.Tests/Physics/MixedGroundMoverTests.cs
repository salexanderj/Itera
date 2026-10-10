using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A world of 10 cm voxels whose floor lies at 1.53 m (<see cref="DensityTerrainGenerator"/>, flat for x under 2 m),
/// loaded 3 m round the middle, shared by the tests.
/// </summary>
public sealed class MixedGroundFixture : IDisposable
{
    public MixedGroundFixture() =>
        World = TestWorlds.Load(new DensityTerrainGenerator(new VoxelGrid(10)), new Vector3(0, 1.6f, 0), 3);

    public VoxelWorld World { get; }

    public void Dispose() => World.Dispose();
}

/// <summary>
/// The mover over voxels and mesh colliders in the same cells: a mesh box on the voxel floor is stepped onto and off; a
/// mesh ceiling over it stops a jump; where both tops are the same cell the higher surface holds the body; and a steep
/// mesh triangle half in the ground, whose top is over the voxels' but whose surface lies inside them, never puts the
/// ground under the voxels' surface.
/// </summary>
public sealed class MixedGroundMoverTests : IClassFixture<MixedGroundFixture>, IDisposable
{
    private const float Floor = 1.53f;
    private const float Step = 1f / 60;
    private const float Gravity = 20;
    private const float Walk = 4.5f;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);
    private readonly MeshColliders _colliders;
    private readonly VoxelCharacterMover _mover;

    public MixedGroundMoverTests(MixedGroundFixture fixture)
    {
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
        _mover = new VoxelCharacterMover(fixture.World, new NothingPassable(), meshes: _colliders);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
    }

    [Fact]
    public void A_mesh_box_on_the_voxel_floor_is_stepped_onto_and_off()
    {
        // 25 cm over the floor, from x = -0.5 to 0.5 m.
        new TestTriangles()
            .Box(new(-0.5f, 1.4f, -0.5f), new(0.5f, Floor + 0.25f, 0.5f))
            .AddTo(_colliders);
        var feet = new Vector3(-1.5f, Floor, 0);
        var velocity = Vector3.Zero;
        var highest = 0f;

        for (var tick = 0; tick < 40; tick++)
        {
            velocity = new Vector3(Walk, velocity.Y - Gravity * Step, 0);
            var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.Equal(CharacterMoveStatus.Moved, result.Status);
            highest = Math.Max(highest, feet.Y);
        }

        Assert.Equal(Floor + 0.25f, highest, 0.005f);
        Assert.True(feet.X > 1, $"Stopped at x = {feet.X:F2} m.");
        Assert.Equal(Floor, feet.Y, 0.01f);
    }

    [Fact]
    public void A_mesh_ceiling_over_the_voxel_floor_stops_a_jump()
    {
        // Its front down at 3.5 m: it blocks the cell over it, so a 1.7 m body's feet rise to 1.8 m.
        new TestTriangles()
            .Quad(-Vector3.UnitY, new(-1, 3.5f, -1), new(-1, 3.5f, 1), new(1, 3.5f, 1), new(1, 3.5f, -1))
            .AddTo(_colliders);
        var feet = new Vector3(0, Floor, 0);
        var velocity = new Vector3(0, MathF.Sqrt(2 * Gravity * 1.1f), 0);
        var highest = 0f;
        var hitCeiling = false;

        for (var tick = 0; tick < 60; tick++)
        {
            var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            hitCeiling |= result.HitCeiling;
            highest = Math.Max(highest, feet.Y);
            velocity.Y -= Gravity * Step;
        }

        Assert.True(hitCeiling);
        Assert.Equal(3.5f - s_shape.HeightMetres, highest, 0.005f);
        Assert.Equal(Floor, feet.Y, 0.01f);
    }

    [Theory]
    [InlineData(1.48f, Floor)]
    [InlineData(1.58f, 1.58f)]
    public void Where_both_tops_are_one_cell_the_higher_surface_holds_the_body(float mesh, float ground)
    {
        // Mesh ground at 1.48 m: its top is the voxels' (1.5 m), and the voxels' surface (1.53 m) is the higher. At
        // 1.58 m its top is a cell over theirs.
        new TestTriangles()
            .Ground(-1, -1, 1, 1, mesh)
            .AddTo(_colliders);

        Assert.True(_mover.GroundTop(new Vector3(0.05f, 1.6f, 0.05f), 0.03f, 0.4f, 0.4f, out var top));

        // The voxels' surface lies where their densities cross one half, within a centimetre of the floor.
        Assert.Equal(ground, top, 0.01f);
    }

    [Fact]
    public void A_steep_mesh_half_in_the_ground_never_puts_the_ground_under_the_voxels_surface()
    {
        // A plane rising 8 m a metre along x across the column from x = 0.5 to 0.6 m: from 0.8 m to 1.6 m over it, 1.2 m
        // at its centre. Its top (1.6 m) is a cell over the voxels' (1.5 m); its surface lies 33 cm inside them.
        new TestTriangles()
            .Quad(Vector3.UnitY, new(0.5f, 0.8f, -1), new(0.5f, 0.8f, 1), new(0.6f, 1.6f, 1), new(0.6f, 1.6f, -1))
            .AddTo(_colliders);

        Assert.True(_mover.GroundTop(new Vector3(0.55f, 1.6f, 0.05f), 0.03f, 0.4f, 1, out var top));

        Assert.Equal(Floor, top, 0.01f);
    }
}
