using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The mover in 10 cm cells over 1 m voxels against the mover in the voxels' own cells, on the same smooth hills
/// (<see cref="SmoothHillsFixture"/>): walking, sprinting, and jumping each way from the same place, step for step, the
/// two keep the same path across and their feet within a few centimetres of each other's height; remembering the ground's
/// surface within a move changes nothing, in either cells; and a move allocates nothing.
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

    /// <summary>
    /// A mover remembering the ground's surface within a move, and one working every answer out again, standing,
    /// walking, and sprinting with jumps each way over the hills, in 10 cm cells and in the voxels' 1 m: every move's
    /// result, feet, and velocity the same bits, the first answering from what it remembered.
    /// </summary>
    [Fact]
    public void Remembering_the_surfaces_within_a_move_changes_nothing()
    {
        foreach (var (remembering, fine) in new[] { (hills.Fine, true), (hills.Coarse, false) })
        {
            var forgetting = fine
                ? new VoxelCharacterMover(hills.World, 10, new NothingPassable())
                : new VoxelCharacterMover(hills.World, new NothingPassable());
            forgetting.RemembersSurfaces = false;
            var answered = remembering.RememberedAnswers;
            foreach (var (speed, jump) in new[] { (0f, 0f), (4.5f, 0f), (7f, 6f) })
            {
                for (var heading = 0; heading < 8; heading++)
                {
                    var angle = heading * MathF.PI / 4;
                    var way = new Vector3(MathF.Sin(angle), 0, -MathF.Cos(angle)) * speed;
                    var (feet, again) = (Start(remembering), Start(forgetting));
                    Vector3 velocity = Vector3.Zero,
                        againVelocity = Vector3.Zero;
                    bool grounded = true,
                        againGrounded = true;
                    for (var tick = 0; tick < 120; tick++)
                    {
                        velocity = Next(velocity, way, grounded, jump, tick);
                        againVelocity = Next(againVelocity, way, againGrounded, jump, tick);
                        var result = remembering.Move(ref feet, ref velocity, in s_shape, Step);
                        var againResult = forgetting.Move(ref again, ref againVelocity, in s_shape, Step);
                        Assert.Equal(againResult, result);
                        Assert.Equal(again, feet);
                        Assert.Equal(againVelocity, velocity);
                        (grounded, againGrounded) = (result.Grounded, againResult.Grounded);
                    }
                }
            }

            Assert.True(remembering.RememberedAnswers > answered, "The mover never answered from what it remembered.");
            Assert.Equal(0, forgetting.RememberedAnswers);
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
