using System.Numerics;
using Itera.Engine.Physics;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The mover in 10 cm cells over 1 m voxels against the mover in the voxels' own cells, on the same smooth hills
/// (<see cref="SmoothHillsFixture"/>): walking, sprinting, and jumping each way from the same place, step for step, the
/// two keep the same path across and their feet within a few centimetres of each other's height; and a move allocates
/// nothing.
/// </summary>
public sealed class FineMoverTests(SmoothHillsFixture hills, ITestOutputHelper output)
    : IClassFixture<SmoothHillsFixture>
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;

    // A person 1.75 m tall whose feet step onto a top in the air, as a game's jump asks.
    private static readonly CharacterShape s_shape = new(1.75f, 0.2625f, 0.42f, StepsInAir: true);

    [Theory]
    [InlineData(4.5f, 0f)]
    [InlineData(7f, 0f)]
    [InlineData(4.5f, 6f)]
    public void Walking_each_way_over_the_hills_keeps_the_coarse_movers_path_and_height(float speed, float jump)
    {
        var worstHeight = 0f;
        var worstAcross = 0f;
        for (var heading = 0; heading < 8; heading++)
        {
            var angle = heading * MathF.PI / 4;
            var way = new Vector3(MathF.Sin(angle), 0, -MathF.Cos(angle)) * speed;
            var coarse = Start(hills.Coarse);
            var fine = Start(hills.Fine);
            Vector3 coarseVelocity = Vector3.Zero,
                fineVelocity = Vector3.Zero;
            bool coarseGrounded = true,
                fineGrounded = true;
            for (var tick = 0; tick < 240; tick++)
            {
                coarseVelocity = Next(coarseVelocity, way, coarseGrounded, jump, tick);
                fineVelocity = Next(fineVelocity, way, fineGrounded, jump, tick);
                var coarseResult = hills.Coarse.Move(ref coarse, ref coarseVelocity, in s_shape, Step);
                var fineResult = hills.Fine.Move(ref fine, ref fineVelocity, in s_shape, Step);
                coarseGrounded = coarseResult.Grounded;
                fineGrounded = fineResult.Grounded;
                worstHeight = MathF.Max(worstHeight, MathF.Abs(coarse.Y - fine.Y));
                worstAcross = MathF.Max(
                    worstAcross,
                    Vector2.Distance(new Vector2(coarse.X, coarse.Z), new Vector2(fine.X, fine.Z))
                );
            }
        }

        output.WriteLine(
            $"At {speed} m/s, jumping {jump} m/s: height apart at most {worstHeight:F4} m, across {worstAcross:F4} m."
        );
        Assert.True(worstAcross < 0.05f, $"The paths went {worstAcross} m apart.");
        Assert.True(worstHeight < 0.05f, $"The feet went {worstHeight} m apart in height.");
    }

    /// <summary>What a walking move costs in each mover, logged (a Release build measures it).</summary>
    [Fact]
    public void A_walking_move_costs_are_logged()
    {
        foreach (var (name, mover) in new[] { ("1 m", hills.Coarse), ("10 cm", hills.Fine) })
        {
            var elapsed = 0.0;
            var moves = 0;
            for (var heading = 0; heading < 8; heading++)
            {
                var angle = heading * MathF.PI / 4;
                var way = new Vector3(MathF.Sin(angle), 0, -MathF.Cos(angle)) * 4.5f;
                var feet = Start(mover);
                var velocity = Vector3.Zero;
                var grounded = true;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                for (var tick = 0; tick < 240; tick++)
                {
                    velocity = Next(velocity, way, grounded, 0, tick);
                    grounded = mover.Move(ref feet, ref velocity, in s_shape, Step).Grounded;
                }

                elapsed += clock.Elapsed.TotalMilliseconds;
                moves += 240;
            }

            output.WriteLine($"{name} cells: {elapsed * 1000 / moves:F1} microseconds a move over {moves} moves.");
        }
    }

    [Fact]
    public void A_move_allocates_nothing()
    {
        var feet = Start(hills.Fine);
        var velocity = Vector3.Zero;
        hills.Fine.Move(ref feet, ref velocity, in s_shape, Step);
        var start = Itera.TestSupport.SystemAllocation.StartMeasurement();
        for (var tick = 0; tick < 120; tick++)
        {
            velocity = new Vector3(3, velocity.Y - Gravity * Step, -2);
            hills.Fine.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>A body standing on the ground at the hills' middle.</summary>
    private static Vector3 Start(VoxelCharacterMover mover)
    {
        var centre = SmoothHillsFixture.Centre;
        Assert.True(mover.GroundTop(centre with { Y = 30 }, 0.2f, 0, 60, out var ground));
        return centre with { Y = ground };
    }

    /// <summary>The next step's velocity: the walk, gravity, and a jump every second from the ground.</summary>
    private static Vector3 Next(Vector3 velocity, Vector3 way, bool grounded, float jump, int tick)
    {
        var up = velocity.Y - Gravity * Step;
        if (grounded)
        {
            up = jump > 0 && tick % 60 == 0 ? jump : Math.Min(up, 0);
        }

        return new Vector3(way.X, up, way.Z);
    }
}
