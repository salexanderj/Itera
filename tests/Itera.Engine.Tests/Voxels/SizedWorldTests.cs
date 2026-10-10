using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// A world on a grid a game sized (<see cref="VoxelGrid.Sized"/>): two 12.8 m chunks each side of the middle, one layer
/// under 0 and one over it. Streaming loads only its columns, an edit past its edge counts nothing, a ray leaves it at
/// its edge, a body dropped from over its top lands on its ground, a body walking out stops at its edge, and the play
/// area and the fall limit follow its bounds.
/// </summary>
public sealed class SizedWorldTests : IDisposable
{
    private static readonly VoxelGrid s_grid = VoxelGrid.Sized(10, 25.6, -12.8, 12.8);
    private readonly VoxelWorld _world;

    public SizedWorldTests() =>
        _world = TestWorlds.Load(new FlatTestGenerator(s_grid), new Vector3(0, 1, 0), radiusMetres: 40);

    public void Dispose() => _world.Dispose();

    [Fact]
    public void The_grid_holds_the_bounds_it_was_sized_to()
    {
        Assert.Equal((-2, 1, -1, 0), (s_grid.MinChunkXZ, s_grid.MaxChunkXZ, s_grid.MinChunkY, s_grid.MaxChunkY));
        Assert.Equal(2, s_grid.MaxLayers);
    }

    [Fact]
    public void Streaming_loads_only_the_columns_inside_the_world()
    {
        Assert.True(_world.IsResident(new ColumnKey(0, 1, 1)));
        Assert.True(_world.IsResident(new ColumnKey(0, -2, -2)));
        Assert.False(_world.IsResident(new ColumnKey(0, 2, 1)));
        Assert.False(_world.IsResident(new ColumnKey(0, 1, 2)));
        Assert.Equal(16, _world.ResidentColumns);
    }

    [Fact]
    public void An_edit_past_the_edge_counts_nothing_and_one_inside_counts_its_stone()
    {
        Span<long> removed = stackalloc long[256];

        var outside = _world.CountRemoved(
            VoxelEdit.Sphere(new VoxelCoord(3000, 0, 0), 100, VoxelEditOperation.Clear),
            removed
        );
        Assert.Equal(-1, outside);

        var inside = _world.CountRemoved(
            VoxelEdit.Sphere(new VoxelCoord(2000, 0, 2000), 100, VoxelEditOperation.Clear),
            removed
        );
        Assert.Equal(0, inside);
        Assert.True(removed[FlatTestGenerator.Stone] > 0);
    }

    [Fact]
    public void A_ray_meets_the_ground_inside_the_world_and_nothing_past_its_edge()
    {
        Assert.True(_world.Raycast(new Vector3(20, 5, 20), -Vector3.UnitY, 10, out var hit));
        Assert.Equal(5, hit.DistanceMetres, 0.01f);

        Assert.False(_world.Raycast(new Vector3(30, 5, 20), -Vector3.UnitY, 10, out _));
        Assert.False(_world.Raycast(new Vector3(20, 0.5f, 20), Vector3.UnitX, 40, out _));
    }

    [Fact]
    public void A_body_dropped_from_over_the_top_lands_on_the_ground()
    {
        var mover = new VoxelCharacterMover(_world, new MoverWorldFixture.LeavesPass());
        var shape = new CharacterShape(1.7f, 0.26f, 0.41f);
        var feet = new Vector3(20, 20, 20);
        var velocity = Vector3.Zero;
        for (var i = 0; i < 180; i++)
        {
            velocity = new Vector3(0, velocity.Y - 20f / 60, 0);
            mover.Move(ref feet, ref velocity, in shape, 1f / 60);
        }

        Assert.Equal(0, feet.Y, 0.001f);
    }

    [Fact]
    public void A_body_walking_out_stops_at_the_worlds_edge()
    {
        var mover = new VoxelCharacterMover(_world, new MoverWorldFixture.LeavesPass());
        var shape = new CharacterShape(1.7f, 0.26f, 0.41f);
        var feet = new Vector3(23, 0, 20);
        var velocity = Vector3.Zero;
        var wall = false;
        for (var i = 0; i < 90; i++)
        {
            velocity = new Vector3(4.5f, velocity.Y - 20f / 60, 0);
            wall |= mover.Move(ref feet, ref velocity, in shape, 1f / 60).HitWall;
        }

        Assert.True(wall);
        Assert.Equal(25.6f - shape.RadiusMetres, feet.X, 0.002f);
        Assert.Equal(0, feet.Y, 0.001f);
    }

    [Fact]
    public void The_play_area_and_the_fall_limit_follow_the_bounds()
    {
        var area = new PlayArea(s_grid);

        Assert.Equal(25.6f, area.WorldEdgeMetres, 1e-4f);
        Assert.Equal(-12.8f, area.WorldFloorMetres, 1e-4f);
        Assert.Equal(12.8f + PlayArea.DefaultMarginMetres, area.TopMetres, 1e-3f);
        Assert.Equal(-12.8f - LooseBodies.FallMarginMetres, LooseBodies.FallLimitMetres(s_grid), 1e-4f);
        Assert.Equal(LooseBodies.FallLimitMetres(s_grid), area.FallLimitMetres);
    }

    [Fact]
    public void A_world_of_more_layers_than_the_default_streams_and_meets_its_rays()
    {
        // 1 cm voxels from -80.64 m to 80.64 m: 126 layers of 1.28 m, more than the 96 of the default 1 cm grid.
        var tall = VoxelGrid.Sized(1, 2.56, -80, 80);
        using var world = TestWorlds.Load(new FlatTestGenerator(tall), new Vector3(0, 1, 0), radiusMetres: 2);

        Assert.Equal(126, tall.MaxLayers);
        Assert.True(world.Raycast(new Vector3(0.5f, 70, 0.5f), -Vector3.UnitY, 100, out var hit));
        Assert.Equal(70, hit.DistanceMetres, 0.01f);
    }
}
