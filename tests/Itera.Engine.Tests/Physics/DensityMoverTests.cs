using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>A world of 10 cm voxels with densities (<see cref="DensityTerrainGenerator"/>), loaded once for the tests.</summary>
public sealed class DensityMoverFixture : IDisposable
{
    public DensityMoverFixture()
    {
        World = TestWorlds.Load(
            new DensityTerrainGenerator(new VoxelGrid(10)),
            new Vector3(3, 2, 13),
            radiusMetres: 16
        );
        Mover = new VoxelCharacterMover(World, new NothingPassable());
    }

    public VoxelWorld World { get; }

    public VoxelCharacterMover Mover { get; }

    public void Dispose() => World.Dispose();
}

/// <summary>
/// The mover on 10 cm voxels, reading their densities (phase 5 step 4): a body stands on the surface the mesher draws,
/// walks up slopes under the limit as ramps (the feet rise as the slope does, not a voxel at a time) and not up steeper
/// ones, climbs stairs and steps as before, and random moves never leave it in the ground.
/// </summary>
public sealed class DensityMoverTests(DensityMoverFixture fixture, ITestOutputHelper output)
    : IClassFixture<DensityMoverFixture>
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;
    private const float Walk = 4.5f;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    private readonly VoxelCharacterMover _mover = fixture.Mover;

    [Fact]
    public void A_body_stands_on_the_floor_inside_its_voxel()
    {
        var feet = new Vector3(1, 2, 1);
        var velocity = Vector3.Zero;

        var result = Run(ref feet, ref velocity, Vector2.Zero, 60);

        Assert.True(result.Grounded);
        // The floor at 153 cm lies inside voxel 15: where the density crosses one half, within a centimetre.
        Assert.Equal(1.53f, feet.Y, 0.01f);
    }

    [Theory]
    [InlineData(0, 30.0)]
    [InlineData(1, 40.0)]
    public void A_slope_under_the_limit_is_walked_up_as_a_ramp(int strip, double degrees)
    {
        var feet = new Vector3(1.2f, 1.6f, DensityTerrainGenerator.Middle(strip) / 100f);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, Vector2.Zero, 30);
        var rise = Math.Tan(degrees * Math.PI / 180);
        var alwaysGrounded = true;
        var everSteep = false;
        var largestRise = 0f;
        var last = feet.Y;

        Run(
            ref feet,
            ref velocity,
            new Vector2(Walk * 0.5f, 0),
            140,
            r =>
            {
                alwaysGrounded &= r.Grounded;
                everSteep |= r.Steep;
            },
            f =>
            {
                largestRise = Math.Max(largestRise, f.Y - last);
                last = f.Y;
            }
        );

        Assert.True(alwaysGrounded);
        Assert.False(everSteep);
        // On the flat at the top, 3.47 m up for 30 degrees.
        Assert.True(feet.X > 5.1f, $"Stopped at x = {feet.X:F2} m.");
        Assert.Equal(
            (float)(DensityTerrainGenerator.Height(510, DensityTerrainGenerator.Middle(strip)) / 100),
            feet.Y,
            0.02f
        );
        // A tick's walk (3.75 cm) rises by the slope's share of it, and a centimetre: not a voxel at a time.
        var perTick = Walk * 0.5f * Step * (float)rise;
        output.WriteLine($"Largest rise in a tick {largestRise * 100:F2} cm, the slope's {perTick * 100:F2} cm.");
        Assert.True(largestRise < perTick + 0.01f, $"The feet rose {largestRise * 100:F2} cm in a tick.");
    }

    /// <summary>
    /// Walking and sprinting up 30 and 40 degrees, a body keeps its pace: no tick meets a wall made of the ramp (the
    /// columns ahead within the slope's rise over the tick's walk are ground), and the feet stay on the slope.
    /// </summary>
    [Theory]
    [InlineData(0, 4.5f)]
    [InlineData(0, 7f)]
    [InlineData(1, 4.5f)]
    [InlineData(1, 7f)]
    public void A_ramp_is_walked_and_run_up_at_an_even_pace(int strip, float speed)
    {
        var feet = new Vector3(1.0f, 1.6f, DensityTerrainGenerator.Middle(strip) / 100f);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, Vector2.Zero, 30);
        var walls = 0;
        var slowest = float.MaxValue;
        var last = feet;

        Run(
            ref feet,
            ref velocity,
            new Vector2(speed, 0),
            40,
            r => walls += r.HitWall ? 1 : 0,
            f =>
            {
                if (f.X < 4.8f)
                {
                    slowest = Math.Min(slowest, (f.X - last.X) / Step);
                }

                last = f;
            }
        );

        output.WriteLine($"Slowest {slowest:F2} m/s of {speed} m/s; {walls} walls.");
        Assert.Equal(0, walls);
        Assert.True(slowest > speed * 0.95f, $"Slowed to {slowest:F2} m/s.");
    }

    /// <summary>
    /// Walking up a ramp towards a flat ceiling, a body stops at the ceiling's face, where its head meets the ceiling:
    /// it never walks under it with its head in the ceiling (the feet rise onto the ramp only where there is room over the
    /// head), and no voxel of the ceiling is ever inside it.
    /// </summary>
    [Fact]
    public void A_ramp_under_a_ceiling_stops_the_body_at_it()
    {
        var feet = new Vector3(1.2f, 1.6f, DensityTerrainGenerator.Middle(6) / 100f);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, Vector2.Zero, 30);

        for (var tick = 0; tick < 120; tick++)
        {
            Run(ref feet, ref velocity, new Vector2(Walk, 0), 1);
            Assert.False(_mover.Overlaps(feet, in s_shape), $"Tick {tick}: in the ground at {feet}.");
            AssertClearAboveTheGround(feet, tick);
        }

        Assert.InRange(feet.X, 2.6f, 3 - s_shape.RadiusMetres + 0.001f);
    }

    [Fact]
    public void A_60_degree_slope_cannot_be_walked_up()
    {
        var feet = new Vector3(1.2f, 1.6f, DensityTerrainGenerator.Middle(2) / 100f);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 60);

        Assert.True(feet.Y < 1.53f + 0.2f, $"Climbed to {feet.Y:F2} m.");
    }

    [Fact]
    public void Stairs_of_20_cm_risers_and_30_cm_treads_are_walked_up()
    {
        var feet = new Vector3(1.2f, 1.6f, DensityTerrainGenerator.Middle(3) / 100f);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 60);

        Assert.Equal(2.30f, feet.Y, 0.001f);
        Assert.True(feet.X > 3.0f, $"Stopped at x = {feet.X:F2} m.");
    }

    [Fact]
    public void A_50_cm_step_is_too_high()
    {
        var feet = new Vector3(2.4f, 1.6f, DensityTerrainGenerator.Middle(4) / 100f);
        var velocity = Vector3.Zero;

        var result = Run(ref feet, ref velocity, new Vector2(Walk, 0), 40);

        Assert.True(result.HitWall);
        Assert.Equal(1.53f, feet.Y, 0.01f);
        Assert.True(feet.X < 3 - 0.26f + 0.01f, $"Walked to x = {feet.X:F3} m.");
    }

    [Fact]
    public void A_wall_stops_the_body_at_its_radius()
    {
        var feet = new Vector3(2.4f, 1.6f, DensityTerrainGenerator.Middle(5) / 100f);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 40);

        Assert.Equal(3 - 0.26f, feet.X, 0.002f);
    }

    /// <summary>
    /// Random moves on the ramp under the ceiling, where landings, steps up, and the ramp's follow all meet the ceiling:
    /// the body is never in the ground or the ceiling.
    /// </summary>
    [Fact]
    public void Random_moves_under_a_ceiling_never_leave_the_body_in_it()
    {
        var random = new Random(9);
        var feet = new Vector3(2, 2.5f, 25.5f);
        var velocity = Vector3.Zero;
        var moves = 0;
        for (var i = 0; i < 4000; i++)
        {
            if (i % 200 == 0 || feet.X < 1 || feet.X > 6 || feet.Z < 24.2f || feet.Z > 26.8f || feet.Y < 1)
            {
                feet = new Vector3(
                    (float)(1.5 + random.NextDouble() * 4),
                    (float)(1.6 + random.NextDouble() * 1),
                    (float)(24.4 + random.NextDouble() * 2.2)
                );
                velocity = Vector3.Zero;
            }

            velocity.X = (float)(random.NextDouble() * 12 - 4);
            velocity.Z = (float)(random.NextDouble() * 8 - 4);
            if (random.Next(15) == 0)
            {
                velocity.Y = (float)(random.NextDouble() * 6);
            }

            velocity.Y -= Gravity * Step;
            var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            if (result.Status == CharacterMoveStatus.Stuck)
            {
                continue;
            }

            moves++;
            Assert.False(_mover.Overlaps(feet, in s_shape), $"Move {i} left the body in the ground at {feet}.");
            AssertClearAboveTheGround(feet, i);
        }

        Assert.True(moves > 3000, $"Only {moves} moves were not stuck.");
    }

    [Fact]
    public void Random_moves_never_leave_the_body_in_the_ground()
    {
        var random = new Random(5);
        var feet = new Vector3(1, 2.5f, 1);
        var velocity = Vector3.Zero;
        var moves = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (i % 400 == 0 || feet.X < 0.4f || feet.X > 7 || feet.Z < 0.4f || feet.Z > 26.6f || feet.Y < 1)
            {
                feet = new Vector3(
                    (float)(0.5 + random.NextDouble() * 6),
                    (float)(1.6 + random.NextDouble() * 3),
                    (float)(0.5 + random.NextDouble() * 26)
                );
                velocity = Vector3.Zero;
            }

            velocity.X = (float)(random.NextDouble() * 20 - 10);
            velocity.Z = (float)(random.NextDouble() * 20 - 10);
            if (random.Next(20) == 0)
            {
                velocity.Y = (float)(random.NextDouble() * 8);
            }

            velocity.Y -= Gravity * Step;
            var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            if (result.Status == CharacterMoveStatus.Stuck)
            {
                continue;
            }

            moves++;
            Assert.False(_mover.Overlaps(feet, in s_shape), $"Move {i} left the body in the ground at {feet}.");
            AssertClearAboveTheGround(feet, i);
        }

        Assert.True(moves > 9000, $"Only {moves} moves were not stuck.");
    }

    [Fact]
    public void Moving_allocates_nothing()
    {
        var feet = new Vector3(4, 2.5f, DensityTerrainGenerator.Middle(0) / 100f);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, new Vector2(1, 0.5f), 50);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 500; i++)
        {
            velocity = new Vector3(i % 100 < 50 ? 1 : -1, velocity.Y - Gravity * Step, 0.5f * (i % 100 < 50 ? 1 : -1));
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Walking_up_and_down_a_slope_costs_little()
    {
        var feet = new Vector3(1.5f, 2, DensityTerrainGenerator.Middle(0) / 100f);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, Vector2.Zero, 5);
        var timer = Stopwatch.StartNew();
        const int Moves = 1000;
        for (var i = 0; i < Moves; i++)
        {
            var direction = i / 60 % 2 == 0 ? Walk : -Walk;
            velocity = new Vector3(direction, velocity.Y - Gravity * Step, 0);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        var microseconds = timer.Elapsed.TotalMicroseconds / Moves;
        output.WriteLine($"{microseconds:F1} us per move.");
        Assert.True(microseconds < 2000, $"{microseconds:F1} us per move.");
    }

    /// <summary>
    /// No solid voxel lies in the body above the half-voxel band its feet stand in (the ground's surface lies anywhere in
    /// its voxel), read voxel by voxel; and the ground's surface (<see cref="DensityTerrainGenerator.Height"/>) lies under
    /// the feet at the body's centre.
    /// </summary>
    private void AssertClearAboveTheGround(Vector3 feet, int move)
    {
        double px = feet.X * 10.0,
            py = feet.Y * 10.0,
            pz = feet.Z * 10.0;
        var footprint = new CircleFootprint(px, pz, s_shape.RadiusMetres * 10.0);
        var y0 = (int)Math.Ceiling(py + 0.5);
        var y1 = (int)Math.Ceiling(py + s_shape.HeightMetres * 10.0 - 0.01) - 1;
        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    fixture.World.TryGetMaterial(new VoxelCoord(x * 10, y * 10, z * 10), out var material);
                    if (material != VoxelConstants.Air)
                    {
                        Assert.Fail(
                            $"Move {move}: voxel ({x}, {y}, {z}) of material {material} is in the body at {feet}."
                        );
                    }
                }
            }
        }

        var ground = DensityTerrainGenerator.Height(feet.X * 100, feet.Z * 100) / 100;
        Assert.True(feet.Y > ground - 0.02f, $"Move {move}: the feet at {feet} lie under the ground at {ground:F3} m.");
    }

    private CharacterMoveResult Run(
        ref Vector3 feet,
        ref Vector3 velocity,
        Vector2 wanted,
        int ticks,
        Action<CharacterMoveResult>? each = null,
        Action<Vector3>? where = null
    )
    {
        var result = default(CharacterMoveResult);
        for (var i = 0; i < ticks; i++)
        {
            velocity.X = wanted.X;
            velocity.Z = wanted.Y;
            velocity.Y -= Gravity * Step;
            result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.NotEqual(CharacterMoveStatus.Waiting, result.Status);
            Assert.NotEqual(CharacterMoveStatus.Stuck, result.Status);
            each?.Invoke(result);
            where?.Invoke(feet);
        }

        return result;
    }
}
