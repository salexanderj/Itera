using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The map's edge is a wall to the mover (a body that walked or dug off the
/// map fell without end): at the corner of the highest x and z, and of the lowest, a body walking out stops at its radius
/// from the edge and loses the velocity into it, and one walking along the edge slides along it.
/// </summary>
public sealed class MapEdgeMoverTests
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;
    private const float Walk = 4.5f;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_body_stops_at_the_maps_edge_and_slides_along_it(int side)
    {
        // The edge's coordinate on x and z, in metres, and a corner 3 m inside it on each.
        var edge = (side > 0 ? VoxelGrid.Unit.EndVoxelXZ : VoxelGrid.Unit.MinVoxelXZ) / 100f;
        var inside = edge - side * 3;
        using var world = TestWorlds.Load(new BoxesGenerator(200), new Vector3(inside, 2, inside), radiusMetres: 5);
        var mover = new VoxelCharacterMover(world, new MoverWorldFixture.LeavesPass());

        // Straight out on x, from 2 m inside the edge and 1 m from the corner's other edge: it stops at its radius.
        var feet = new Vector3(edge - side * 2, 2, edge - side * 1);
        var velocity = Vector3.Zero;
        var wall = false;
        for (var i = 0; i < 90; i++)
        {
            velocity = new Vector3(side * Walk, velocity.Y - Gravity * Step, 0);
            wall |= mover.Move(ref feet, ref velocity, in s_shape, Step).HitWall;
        }

        Assert.True(wall);
        Assert.Equal(edge - side * 0.26f, feet.X, 0.002f);
        Assert.Equal(2, feet.Y, 0.001f);
        Assert.Equal(0, velocity.X, 3);

        // Out on x and z at once: it slides along the x edge into the corner, stopped there by the z edge.
        for (var i = 0; i < 90; i++)
        {
            velocity = new Vector3(side * Walk, velocity.Y - Gravity * Step, side * Walk);
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.Equal(edge - side * 0.26f, feet.X, 0.002f);
        Assert.Equal(edge - side * 0.26f, feet.Z, 0.002f);
        Assert.Equal(2, feet.Y, 0.001f);
    }
}
