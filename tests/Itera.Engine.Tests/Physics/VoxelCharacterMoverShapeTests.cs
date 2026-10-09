using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Physics;

/// <summary>The mover on curved ground and around a round trunk, each built of 1 cm steps.</summary>
public sealed class VoxelCharacterMoverShapeTests
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;
    private const float Walk = 4.5f;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    [Fact]
    public void The_body_slides_around_a_round_trunk()
    {
        using var world = TestWorlds.Load(new TrunkGenerator(), new Vector3(0.64f, 2, 0.64f));
        var mover = new VoxelCharacterMover(world, new NothingPassable());
        // Walking +x, 10 cm to the +z side of the trunk's centre. The velocity turns towards the wanted one at
        // 50 m/s^2, as the player's movement does, so the sideways speed the trunk gives is kept.
        var feet = new Vector3(
            (TrunkGenerator.TrunkX - 120) / 100f,
            TrunkGenerator.Ground / 100f,
            (TrunkGenerator.TrunkZ + 10) / 100f
        );
        var velocity = Vector3.Zero;

        for (var i = 0; i < 60; i++)
        {
            var horizontal = Accelerate(new Vector2(velocity.X, velocity.Z), new Vector2(Walk, 0));
            velocity = new Vector3(horizontal.X, velocity.Y - Gravity * Step, horizontal.Y);
            var result = mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.Equal(CharacterMoveStatus.Moved, result.Status);
            Assert.False(mover.Overlaps(feet, in s_shape));
        }

        Assert.True(
            feet.X * 100 > TrunkGenerator.TrunkX + TrunkGenerator.TrunkRadius + 26,
            $"Caught on the trunk at x = {feet.X * 100:F1}, z = {feet.Z * 100:F1}."
        );
        Assert.Equal(TrunkGenerator.Ground / 100f, feet.Y, 4);
    }

    [Fact]
    public void The_body_walks_over_a_dome_without_leaving_the_ground()
    {
        using var world = TestWorlds.Load(new DomeGenerator(), new Vector3(0, 4, 0), radiusMetres: 4);
        var mover = new VoxelCharacterMover(world, new NothingPassable());
        var feet = new Vector3(-3, DomeGenerator.Height(-300, 0) / 100f + 0.2f, 0);
        var velocity = Vector3.Zero;
        Settle(mover, ref feet, ref velocity);

        for (var i = 0; i < 80; i++)
        {
            velocity = new Vector3(Walk, velocity.Y - Gravity * Step, 0);
            var result = mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.True(result.Grounded, $"Left the ground at x = {feet.X * 100:F1}.");
            Assert.False(mover.Overlaps(feet, in s_shape));
        }

        Assert.True(feet.X > 2.9f, $"Only reached x = {feet.X * 100:F1}.");
        // The flat bottom stands on the highest voxel under it, on the side towards the top of the dome.
        var x = (int)MathF.Round(feet.X * 100);
        Assert.InRange(feet.Y * 100, DomeGenerator.Height(x, 0) - 0.01f, DomeGenerator.Height(x - 26, 0) + 0.01f);
    }

    [Fact]
    public void The_body_walks_up_and_down_a_slope()
    {
        const ulong Seed = 17;
        using var world = TestWorlds.Load(new SlopeGenerator(), new Vector3(0, 2, 0), seed: Seed);
        var mover = new VoxelCharacterMover(world, new NothingPassable());
        var feet = new Vector3(-2, SlopeGenerator.Height(-200, 0, Seed) / 100f + 0.2f, 0);
        var velocity = Vector3.Zero;
        Settle(mover, ref feet, ref velocity);
        var start = feet;

        for (var i = 0; i < 100; i++)
        {
            var direction = i < 50 ? Walk : -Walk;
            velocity = new Vector3(direction, velocity.Y - Gravity * Step, 0);
            var result = mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.True(result.Grounded, $"Left the ground at x = {feet.X * 100:F1}.");
            Assert.False(mover.Overlaps(feet, in s_shape));
        }

        Assert.Equal(start.X, feet.X, 3);
        Assert.Equal(start.Y, feet.Y, 3);
    }

    /// <summary>Turns the horizontal velocity towards the wanted one by at most 50 m/s^2 over one step.</summary>
    private static Vector2 Accelerate(Vector2 current, Vector2 wanted)
    {
        var change = wanted - current;
        var limit = 50 * Step;
        var length = change.Length();
        return length <= limit ? wanted : current + change * (limit / length);
    }

    private static void Settle(VoxelCharacterMover mover, ref Vector3 feet, ref Vector3 velocity)
    {
        for (var i = 0; i < 30; i++)
        {
            velocity.Y -= Gravity * Step;
            mover.Move(ref feet, ref velocity, in s_shape, Step);
        }
    }
}
