using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A mover that moves bodies wider than 1.5 m in radius in 50 cm cells (<see cref="VoxelCharacterMover.Large"/>): a
/// person moves in its own 10 cm cells, a body 40 m tall in the coarser ones, where it walks, stops at a wall at its
/// radius, and steps onto a ledge, at a small share of the cost; a worker's large moves are its owner's, bit for bit;
/// and its widest body is the coarser mover's.
/// </summary>
public sealed class LargeBodyMoverTests(GiantWorldFixture fixture) : IClassFixture<GiantWorldFixture>
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;

    private static readonly CharacterShape s_person = new(1.75f, 0.2625f, 0.42f);
    private static readonly CharacterShape s_colossus = new(40, 6, 3);

    private readonly VoxelCharacterMover _mover = fixture.WithLarge;

    [Fact]
    public void A_person_moves_in_the_movers_cells_and_a_colossus_in_the_large_ones()
    {
        long own = _mover.Moves,
            large = fixture.Large.Moves;
        var feet = new Vector3(-20, 0, 0);
        var velocity = Vector3.Zero;
        _mover.Move(ref feet, ref velocity, in s_person, Step);
        Assert.Equal((own + 1, large), (_mover.Moves, fixture.Large.Moves));

        _mover.Move(ref feet, ref velocity, in s_colossus, Step);
        Assert.Equal((own + 1, large + 1), (_mover.Moves, fixture.Large.Moves));
        Assert.Equal(100f, _mover.MaxRadiusMetres, 3);
    }

    [Fact]
    public void A_colossus_stops_at_a_wall_at_its_radius_and_steps_onto_a_ledge()
    {
        var feet = new Vector3(0, 0, -20);
        var velocity = Vector3.Zero;
        var hit = false;
        for (var tick = 0; tick < 300; tick++)
        {
            velocity = new Vector3(4, velocity.Y - Gravity * Step, 0);
            hit |= _mover.Move(ref feet, ref velocity, in s_colossus, Step).HitWall;
        }

        Assert.True(hit);
        Assert.InRange(feet.X, GiantWorldFixture.WallX - 6 - 0.01f, GiantWorldFixture.WallX - 6);

        feet = new Vector3(0, 0, 25);
        velocity = Vector3.Zero;
        CharacterMoveResult result = default;
        for (var tick = 0; tick < 300; tick++)
        {
            velocity = new Vector3(4, velocity.Y - Gravity * Step, 0);
            result = _mover.Move(ref feet, ref velocity, in s_colossus, Step);
        }

        Assert.True(result.Grounded);
        Assert.True(feet.X > GiantWorldFixture.LedgeX + 6, $"The colossus stopped at x = {feet.X}.");
        Assert.InRange(feet.Y, GiantWorldFixture.LedgeTop - 0.05f, GiantWorldFixture.LedgeTop + 0.05f);
    }

    [Fact]
    public void A_workers_large_moves_are_its_owners()
    {
        var worker = _mover.CreateWorker();
        Assert.NotNull(worker.Large);
        Vector3 feet = new(-25, 0, -5),
            again = feet,
            velocity = Vector3.Zero,
            againVelocity = Vector3.Zero;
        for (var tick = 0; tick < 120; tick++)
        {
            velocity = new Vector3(3, velocity.Y - Gravity * Step, 2);
            againVelocity = new Vector3(3, againVelocity.Y - Gravity * Step, 2);
            var result = _mover.Move(ref feet, ref velocity, in s_colossus, Step);
            var againResult = worker.Move(ref again, ref againVelocity, in s_colossus, Step);
            Assert.Equal(result, againResult);
            Assert.Equal(feet, again);
        }
    }

    [Fact]
    public void A_large_mover_has_none_of_its_own()
    {
        Assert.Throws<ArgumentException>(() =>
            new VoxelCharacterMover(fixture.World, 10, new NothingPassable()) { Large = fixture.WithLarge }
        );
    }
}
