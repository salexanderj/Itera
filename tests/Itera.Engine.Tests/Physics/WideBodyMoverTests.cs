using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A world for bodies wider than one strip of a slide's window (128 cells): a long wall, a 30 cm platform, and pillars,
/// floor top at 2 m. Loaded once for all the wide-body tests.
/// </summary>
public sealed class WideBodyWorldFixture : IDisposable
{
    public const int Floor = 200;

    /// <summary>The wall's face towards -x, from z = -700 to -200.</summary>
    public const int WallX = 300;

    /// <summary>The platform's near edge, from z = 200 to 700; its top is 30 cm above the floor.</summary>
    public const int PlatformX = 100;

    public WideBodyWorldFixture()
    {
        VoxelBox[] boxes =
        [
            new(WallX, Floor, -700, WallX + 29, 1399, -200, BoxesGenerator.Stone),
            new(PlatformX, Floor, 200, 700, Floor + 29, 700, BoxesGenerator.Stone),
            // Pillars of several sizes on the -x side, for the random moves.
            new(-420, Floor, -100, -401, 1399, -81, BoxesGenerator.Stone),
            new(-250, Floor, 60, -241, 1399, 69, BoxesGenerator.Stone),
            new(-600, Floor, 100, -560, 1399, 140, BoxesGenerator.Stone),
            new(-130, Floor, -300, -128, 1399, -128, BoxesGenerator.Stone),
            new(-700, Floor + 50, -400, -500, Floor + 79, -350, BoxesGenerator.Stone),
        ];
        World = TestWorlds.Load(new BoxesGenerator(Floor, boxes), new Vector3(0, 2, 0), radiusMetres: 9);
        Mover = new VoxelCharacterMover(World, new NothingPassable());
    }

    public VoxelWorld World { get; }

    public VoxelCharacterMover Mover { get; }

    public void Dispose() => World.Dispose();
}

/// <summary>
/// <see cref="VoxelCharacterMover"/> with bodies 1.5 m and 2 m in radius, whose slide windows span three and four strips
/// of 128 cells: walls stop them at their radius, they step onto a ledge, random moves never leave them inside a voxel,
/// and moving them allocates nothing.
/// </summary>
[RunsLong]
public sealed class WideBodyMoverTests : IClassFixture<WideBodyWorldFixture>
{
    private const float Step = 1f / 60;
    private const float Gravity = 20;
    private static readonly CharacterShape s_wide = new(10, 1.5f, 1);
    private static readonly CharacterShape s_widest = new(6, 2, 1);

    private readonly WideBodyWorldFixture _fixture;
    private readonly VoxelCharacterMover _mover;
    private readonly ITestOutputHelper _output;

    public WideBodyMoverTests(WideBodyWorldFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _mover = fixture.Mover;
        _output = output;
    }

    [Fact]
    public void A_body_wider_than_the_limit_is_refused()
    {
        var feet = new Vector3(0, 2, 0);
        var velocity = Vector3.Zero;
        var shape = new CharacterShape(6, VoxelCharacterMover.MaxRadiusMetres + 0.01f, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => _mover.Move(ref feet, ref velocity, in shape, Step));
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void A_wall_stops_a_wide_body_at_its_radius(float radius)
    {
        var shape = new CharacterShape(8, radius, 1);
        var feet = new Vector3(-1, 2, -5.5f);
        var velocity = Vector3.Zero;

        var result = Run(ref feet, ref velocity, in shape, new Vector2(4, 0), 120);

        Assert.True(result.HitWall);
        Assert.InRange(
            feet.X * 100,
            WideBodyWorldFixture.WallX - radius * 100 - 0.05f,
            WideBodyWorldFixture.WallX - radius * 100
        );
        Assert.Equal(-5.5f, feet.Z, 3);
        Assert.Equal(0, velocity.X, 3);
    }

    [Fact]
    public void A_wide_body_slides_along_a_wall_it_walks_into_at_an_angle()
    {
        var feet = new Vector3(0, 2, -6);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, in s_wide, new Vector2(3, 1.5f), 90);

        Assert.InRange(feet.X * 100, WideBodyWorldFixture.WallX - 150.05f, WideBodyWorldFixture.WallX - 150);
        Assert.True(feet.Z > -4, $"Stopped at z = {feet.Z:F2} instead of sliding along the wall.");
    }

    [Fact]
    public void A_wide_body_steps_onto_a_30_cm_platform()
    {
        var feet = new Vector3(-3.5f, 2, 4.5f);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, in s_wide, new Vector2(3, 0), 90);

        // Its front edge crossed the platform's edge 1.5 m back.
        Assert.True(feet.X > 0.9f, $"Only reached x = {feet.X:F2}.");
        Assert.Equal(2.3f, feet.Y, 3);
        Assert.False(_mover.Overlaps(feet, in s_wide));
    }

    [Theory]
    [InlineData(1.5f, 5)]
    [InlineData(2f, 11)]
    public void Random_moves_never_leave_a_wide_body_inside_a_blocking_voxel(float radius, int seed)
    {
        var shape = new CharacterShape(6, radius, 1);
        var random = new Random(seed);
        var feet = Vector3.Zero;
        var velocity = Vector3.Zero;
        var moves = 0;
        for (var i = 0; i < 2000; i++)
        {
            // Within 5.5 m of the middle, so the columns a move reads stay within the 9 m that are loaded.
            if (i % 200 == 0 || new Vector2(feet.X, feet.Z).Length() > 5.5f || feet.Y < 1)
            {
                do
                {
                    feet = new Vector3(random.Next(-500, 500) / 100f, 2.5f, random.Next(-500, 500) / 100f);
                } while (new Vector2(feet.X, feet.Z).Length() > 5 || _mover.Overlaps(feet, in shape));

                velocity = Vector3.Zero;
            }

            velocity.X = (float)(random.NextDouble() * 16 - 8);
            velocity.Z = (float)(random.NextDouble() * 16 - 8);
            velocity.Y -= Gravity * Step;
            var result = _mover.Move(ref feet, ref velocity, in shape, Step);
            if (result.Status is CharacterMoveStatus.Stuck or CharacterMoveStatus.Waiting)
            {
                continue;
            }

            moves++;
            Assert.False(_mover.Overlaps(feet, in shape), $"Move {i} left the body inside a voxel at {feet}.");
            if (i % 250 == 0)
            {
                AssertClearVoxelByVoxel(feet, in shape);
            }
        }

        Assert.True(moves > 1800, $"Only {moves} moves were not stuck or waiting.");
    }

    [Fact]
    public void Moving_a_wide_body_allocates_nothing()
    {
        // Clear of the pillars, the wall, and the platform.
        var feet = new Vector3(0.8f, 2, -0.3f);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, in s_widest, new Vector2(1, 0.5f), 20);

        // Back and forth, stepping onto the platform's corner and off it. The first step up of any body allocates 320
        // bytes once (it did before bodies grew wider than 50 cm), so the second pass, the same moves, is measured.
        for (var pass = 0; pass < 2; pass++)
        {
            var before = SystemAllocation.StartMeasurement();
            for (var i = 0; i < 200; i++)
            {
                var sign = i % 100 < 50 ? 1 : -1;
                velocity = new Vector3(sign, velocity.Y - Gravity * Step, 0.5f * sign);
                _mover.Move(ref feet, ref velocity, in s_widest, Step);
            }

            if (pass == 1)
            {
                Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }
    }

    [Fact]
    public void A_wide_body_walking_onto_a_platform_costs_little()
    {
        var feet = new Vector3(-3.5f, 2, 4.5f);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, in s_wide, Vector2.Zero, 5);
        var timer = Stopwatch.StartNew();
        const int Moves = 300;
        for (var i = 0; i < Moves; i++)
        {
            // Back and forth over the platform's edge, at a wide body's walk.
            var direction = i / 60 % 2 == 0 ? 2f : -2f;
            velocity = new Vector3(direction, velocity.Y - Gravity * Step, 0);
            _mover.Move(ref feet, ref velocity, in s_wide, Step);
        }

        var microseconds = timer.Elapsed.TotalMicroseconds / Moves;
        _output.WriteLine($"{microseconds:F1} us per move.");
        Assert.True(microseconds < 5000, $"{microseconds:F1} us per move.");
    }

    private CharacterMoveResult Run(
        ref Vector3 feet,
        ref Vector3 velocity,
        in CharacterShape shape,
        Vector2 wanted,
        int ticks
    )
    {
        var result = default(CharacterMoveResult);
        for (var i = 0; i < ticks; i++)
        {
            velocity.X = wanted.X;
            velocity.Z = wanted.Y;
            velocity.Y -= Gravity * Step;
            result = _mover.Move(ref feet, ref velocity, in shape, Step);
            Assert.Equal(CharacterMoveStatus.Moved, result.Status);
            Assert.False(_mover.Overlaps(feet, in shape));
        }

        return result;
    }

    /// <summary>Checks every voxel the body covers with <see cref="VoxelWorld.TryGetMaterial(VoxelCoord, out byte)"/>.</summary>
    private void AssertClearVoxelByVoxel(Vector3 feet, in CharacterShape shape)
    {
        double px = feet.X * 100.0,
            py = feet.Y * 100.0,
            pz = feet.Z * 100.0;
        var footprint = new CircleFootprint(px, pz, shape.RadiusMetres * 100.0);
        var y0 = (int)Math.Floor(py + 0.01);
        var y1 = (int)Math.Ceiling(py + shape.HeightMetres * 100.0 - 0.01) - 1;
        for (var z = footprint.MinZ; z <= footprint.MaxZ; z++)
        {
            if (!footprint.Row(z, out var x0, out var x1))
            {
                continue;
            }

            for (var x = x0; x <= x1; x++)
            {
                // The body's bottom row and a few rows up catch a body sunk into the floor or the platform.
                for (var y = y0; y <= Math.Min(y1, y0 + 40); y++)
                {
                    _fixture.World.TryGetMaterial(new VoxelCoord(x, y, z), out var material);
                    Assert.True(
                        material == VoxelConstants.Air,
                        $"Voxel ({x}, {y}, {z}) of material {material} is inside the body at {feet}."
                    );
                }
            }
        }
    }
}
