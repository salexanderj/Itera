using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>The mover over the test features of <see cref="MoverWorldFixture"/>, of voxels or of triangles.</summary>
public interface IMoverFeatures
{
    VoxelCharacterMover Mover { get; }

    /// <summary>True when the 1 cm cell (x, y, z) of the features blocks a body.</summary>
    bool Blocks(int x, int y, int z);

    /// <summary>True when a move's first read of a place may allocate (mesh colliders' columns are made then).</summary>
    bool FirstReadsAllocate { get; }
}

/// <summary>
/// A world of test features laid out on lots 1.6 m apart (lot centres at -2.4, -0.8, 0.8, and 2.4 m on each axis), one
/// feature per lot, floor top at 2 m. Loaded once for all the mover tests.
/// </summary>
public sealed class MoverWorldFixture : IMoverFeatures, IDisposable
{
    public const int Floor = 200;

    public MoverWorldFixture()
    {
        World = TestWorlds.Load(new BoxesGenerator(Floor, Boxes()), new Vector3(0, 2, 0), radiusMetres: 5);
        Mover = new VoxelCharacterMover(World, new LeavesPass());
    }

    public VoxelWorld World { get; }

    /// <summary>The features, laid over the dirt below <see cref="Floor"/> in order.</summary>
    internal static VoxelBox[] Boxes()
    {
        var boxes = new List<VoxelBox>
        {
            // A wall (lot -240, -240).
            new(-200, 200, -310, -170, 599, -170, BoxesGenerator.Stone),
            // A 1 cm bump (lot 80, -240).
            new(110, 200, -310, 140, 200, -170, BoxesGenerator.Stone),
            // A 30 cm step up onto a platform (lot 240, -240).
            new(260, 200, -310, 319, 229, -170, BoxesGenerator.Stone),
            // A 50 cm step (lot -240, -80).
            new(-220, 200, -150, -180, 249, -10, BoxesGenerator.Stone),
            // A slab with its bottom 1.8 m above the floor (lot -80, -80).
            new(-90, 380, -150, -50, 399, -10, BoxesGenerator.Stone),
            // A slab 2 m above the floor, a ceiling for a jump (lot 80, -80).
            new(30, 400, -130, 130, 420, -30, BoxesGenerator.Stone),
            // A block of leaves (lot 240, -80).
            new(220, 200, -150, 260, 499, -10, BoxesGenerator.Leaves),
            // A mushroom stem (lot -240, 80).
            new(-220, 200, 75, -210, 299, 85, BoxesGenerator.Mushroom),
            // A pit 1 m deep (lot -80, 80).
            new(-120, 100, 40, -40, 199, 120, VoxelConstants.Air),
            // A platform 1 m high (lot 240, 80).
            new(220, 200, 10, 319, 299, 150, BoxesGenerator.Stone),
        };
        // A wall at 45 degrees built of 1 cm steps, 10 cm thick (lot -80, -240): x - z from 180 to 189.
        for (var z = -310; z <= -170; z++)
        {
            boxes.Add(new VoxelBox(z + 180, 200, z, z + 189, 599, z, BoxesGenerator.Stone));
        }

        // Stairs of 20 cm risers and 30 cm treads rising along +x, to 80 cm (lot -80, 240).
        for (var k = 0; k < 4; k++)
        {
            boxes.Add(new VoxelBox(-130 + 30 * k, 200, 170, -1, 219 + 20 * k, 310, BoxesGenerator.Stone));
        }

        // A 60-degree slope rising along +x from x = 80, to a top at 3.03 m (lot 80, 240), and a 30-degree slope
        // from x = 200 to a top at 2.46 m (lot 240, 240), each of 1 cm steps.
        AddSlope(boxes, 80, 60, 1.7320508);
        AddSlope(boxes, 200, 80, 0.57735027);
        return [.. boxes];
    }

    /// <summary>The top of a slope rising along +x from <paramref name="x0"/>, <paramref name="rise"/> per voxel.</summary>
    public static int SlopeTop(int x0, double rise, int x) => Floor + (int)Math.Floor((x - x0) * rise);

    private static void AddSlope(List<VoxelBox> boxes, int x0, int length, double rise)
    {
        for (var i = 1; i <= length; i++)
        {
            var top = SlopeTop(x0, rise, x0 + i);
            if (top > Floor)
            {
                boxes.Add(new VoxelBox(x0 + i, Floor, 170, x0 + i, top - 1, 310, BoxesGenerator.Stone));
            }
        }

        // A flat top 5 cm long.
        var last = SlopeTop(x0, rise, x0 + length);
        boxes.Add(new VoxelBox(x0 + length + 1, Floor, 170, x0 + length + 5, last - 1, 310, BoxesGenerator.Stone));
    }

    public VoxelCharacterMover Mover { get; }

    public bool FirstReadsAllocate => false;

    public bool Blocks(int x, int y, int z)
    {
        World.TryGetMaterial(new VoxelCoord(x, y, z), out var material);
        return material != VoxelConstants.Air && material != BoxesGenerator.Leaves;
    }

    public void Dispose() => World.Dispose();

    /// <summary>Characters pass through leaves only.</summary>
    internal sealed class LeavesPass : IVoxelPassability
    {
        public bool IsPassable(byte material) => material == VoxelConstants.Air || material == BoxesGenerator.Leaves;
    }
}

/// <summary>The mover through the test features of voxels (<see cref="MoverWorldFixture"/>).</summary>
public sealed class VoxelCharacterMoverTests(MoverWorldFixture fixture, ITestOutputHelper output)
    : MoverFeatureTests(fixture, output),
        IClassFixture<MoverWorldFixture>
{
    [Fact]
    public void Leaves_let_the_body_through()
    {
        var feet = Metres(180, 200, -80);
        var velocity = Vector3.Zero;

        var hitWall = false;
        Run(ref feet, ref velocity, new Vector2(Walk, 0), 16, r => hitWall |= r.HitWall);

        Assert.False(hitWall);
        Assert.True(feet.X * 100 > 270, $"Only reached x = {feet.X * 100:F1}.");
    }

    [Fact]
    public void A_body_inside_placed_stone_is_lifted_onto_it()
    {
        var feet = Metres(-240, 200, 240);
        fixture.World.Apply(
            VoxelEdit.Sphere(new VoxelCoord(-240, 220, 240), 30, VoxelEditOperation.Set, BoxesGenerator.Stone)
        );
        Assert.True(Mover.Overlaps(feet, in Shape));
        var velocity = Vector3.Zero;

        var result = Mover.Move(ref feet, ref velocity, in Shape, Step);

        Assert.Equal(CharacterMoveStatus.Lifted, result.Status);
        Assert.True(feet.Y * 100 >= 250, $"Feet at {feet.Y * 100:F1}.");
        Assert.False(Mover.Overlaps(feet, in Shape));
        Assert.True(result.Grounded);
    }
}

/// <summary>
/// The mover through the test features (walls, 1 cm steps, a pit, ceilings, slopes, and stairs), whatever they are made
/// of: each rule must answer alike for voxels and for triangles.
/// </summary>
public abstract class MoverFeatureTests
{
    protected const float Step = 1f / 60;
    protected const float Gravity = 20;
    protected const float Walk = 4.5f;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.41f);

    private readonly IMoverFeatures _features;
    private readonly VoxelCharacterMover _mover;
    private readonly ITestOutputHelper _output;

    protected MoverFeatureTests(IMoverFeatures features, ITestOutputHelper output)
    {
        _features = features;
        _mover = features.Mover;
        _output = output;
    }

    protected static ref readonly CharacterShape Shape => ref s_shape;

    protected VoxelCharacterMover Mover => _mover;

    [Fact]
    public void A_velocity_that_is_not_a_number_is_refused()
    {
        var feet = Metres(-200, 300, 80);
        var velocity = new Vector3(float.NaN, 0, 0);

        Assert.Throws<ArgumentException>(() => _mover.Move(ref feet, ref velocity, in s_shape, Step));
        velocity = new Vector3(0, float.PositiveInfinity, 0);
        Assert.Throws<ArgumentException>(() => _mover.Move(ref feet, ref velocity, in s_shape, Step));
        feet = new Vector3(0, float.NaN, 0);
        velocity = Vector3.Zero;
        Assert.Throws<ArgumentException>(() => _mover.Move(ref feet, ref velocity, in s_shape, Step));
    }

    [Fact]
    public void A_huge_velocity_moves_at_most_the_move_limit_and_returns_at_once()
    {
        // 1e30 m/s would have the columns check walk a box of 10^28 columns; the move is scaled down to the limit.
        var feet = Metres(-200, 300, 80);
        var start = feet;
        var velocity = new Vector3(1e30f, 0, 0);
        var watch = Stopwatch.StartNew();

        var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.True(watch.ElapsedMilliseconds < 1000, $"The move took {watch.ElapsedMilliseconds} ms.");
        Assert.True(float.IsFinite(feet.X) && float.IsFinite(velocity.X));
        Assert.True(velocity.Length() * Step <= VoxelCharacterMover.MaxMoveMetres * 1.001f);
        Assert.True(Vector3.Distance(start, feet) <= VoxelCharacterMover.MaxMoveMetres * 1.001f);
        Assert.NotEqual(CharacterMoveStatus.Stuck, result.Status);
    }

    /// <summary>
    /// A huge velocity is scaled down to the move limit in its own direction (its float
    /// length was infinite, which scaled it to nothing).
    /// </summary>
    [Fact]
    public void A_huge_velocity_keeps_its_direction()
    {
        // Straight up from 4.5 m, clear of every feature.
        var feet = new Vector3(0, 4.5f, 0);
        var velocity = new Vector3(0, 1e30f, 0);

        _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.Equal(VoxelCharacterMover.MaxMoveMetres / Step, velocity.Y, 0.01f);
        Assert.Equal(4.5f + VoxelCharacterMover.MaxMoveMetres, feet.Y, 0.001f);
    }

    /// <summary>
    /// A move longer than a part (24 cm) is split, and each part slides against the walls: 67 cm in one step into a wall
    /// 54 cm ahead stops at the radius (no test ran a move split into parts).
    /// </summary>
    [Fact]
    public void A_move_split_into_parts_stops_at_a_wall()
    {
        var feet = Metres(-280, 200, -240);
        var velocity = new Vector3(40, 0, 0);

        var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.True(result.HitWall);
        Assert.InRange(feet.X * 100, -200 - 26 - 0.05f, -200 - 26);
        Assert.Equal(-240, feet.Z * 100, 2);
        Assert.Equal(0, velocity.X, 3);
    }

    [Fact]
    public void A_fall_lands_exactly_on_the_floor()
    {
        var feet = Metres(80, 300, 80);
        var velocity = Vector3.Zero;

        var result = Run(ref feet, ref velocity, Vector2.Zero, 60);

        Assert.Equal(2.00f, feet.Y, 4);
        Assert.True(result.Grounded);
        Assert.Equal(0, velocity.Y);
        Assert.Equal(Vector2.Zero, result.WallNormal);
    }

    [Fact]
    public void A_wall_stops_the_body_at_its_radius_and_takes_away_the_velocity_into_it()
    {
        var feet = Metres(-280, 200, -240);
        var velocity = Vector3.Zero;

        var result = Run(ref feet, ref velocity, new Vector2(Walk, 0), 30);

        Assert.True(result.HitWall);
        Assert.InRange(feet.X * 100, -200 - 26 - 0.05f, -200 - 26);
        Assert.Equal(-240, feet.Z * 100, 2);
        Assert.Equal(0, velocity.X, 3);
        // The wall's normal points back out of it, along -x.
        Assert.True(Vector2.Distance(-Vector2.UnitX, result.WallNormal) < 1e-3f, $"Normal {result.WallNormal}.");
    }

    [Fact]
    public void The_body_slides_along_a_wall_of_1_cm_steps_at_45_degrees()
    {
        // The wall runs along x - z = 180; walking +x from x - z = 140 meets it after about 43 voxels.
        var feet = Metres(-100, 200, -240);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, new Vector2(Walk, 0), 10);
        var before = feet;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 10);

        // Along the wall, (1, 1) / sqrt 2, a wall that did not catch would give 4.5 / sqrt 2 m/s.
        var along = ((feet.X - before.X) + (feet.Z - before.Z)) / MathF.Sqrt(2);
        var ideal = Walk / MathF.Sqrt(2) * 10 * Step;
        _output.WriteLine($"Slid {along * 100:F1} voxels of an ideal {ideal * 100:F1}.");
        Assert.True(along >= 0.6f * ideal, $"Slid {along * 100:F1} voxels of an ideal {ideal * 100:F1}.");
        Assert.False(_mover.Overlaps(feet, in s_shape));
    }

    [Fact]
    public void The_body_steps_over_a_1_cm_bump_and_back_down()
    {
        var feet = Metres(40, 200, -240);
        var velocity = Vector3.Zero;

        var result = Run(ref feet, ref velocity, new Vector2(Walk, 0), 18);

        Assert.True(feet.X * 100 > 140 + 26, $"Only reached x = {feet.X * 100:F1}.");
        Assert.Equal(2.00f, feet.Y, 4);
        Assert.True(result.Grounded);
    }

    [Fact]
    public void The_body_steps_up_30_cm_and_walks_back_down_without_leaving_the_ground()
    {
        var feet = Metres(200, 200, -240);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 14);
        Assert.Equal(2.30f, feet.Y, 4);

        var alwaysGrounded = true;
        var stepped = 0f;
        Run(
            ref feet,
            ref velocity,
            new Vector2(-Walk, 0),
            14,
            r =>
            {
                alwaysGrounded &= r.Grounded;
                stepped += r.SteppedMetres;
            }
        );

        Assert.Equal(2.00f, feet.Y, 4);
        Assert.True(alwaysGrounded);
        Assert.Equal(-0.30f, stepped, 3);
    }

    [Fact]
    public void The_ground_under_a_foot_is_the_top_of_the_highest_blocking_voxel_in_range()
    {
        // On the 30 cm platform, on the floor beside it, and half on each: the higher wins.
        Assert.True(_mover.GroundTop(Metres(290, 210, -240), 0.03f, 0.4f, 0.4f, out var platform));
        Assert.True(_mover.GroundTop(Metres(200, 210, -240), 0.03f, 0.4f, 0.4f, out var floor));
        Assert.True(_mover.GroundTop(Metres(260, 210, -240), 0.03f, 0.4f, 0.4f, out var edge));
        // Leaves are not ground: the floor under them is (triangles have no leaves: the floor alone is there).
        Assert.True(_mover.GroundTop(Metres(240, 240, -80), 0.03f, 0.1f, 0.5f, out var underLeaves));
        // Nothing from 10 cm below to 10 cm above a point 50 cm over the floor.
        var none = _mover.GroundTop(Metres(0, 250, 0), 0.03f, 0.1f, 0.1f, out _);

        Assert.Equal(2.30f, platform, 4);
        Assert.Equal(2.00f, floor, 4);
        Assert.Equal(2.30f, edge, 4);
        Assert.Equal(2.00f, underLeaves, 4);
        Assert.False(none);
    }

    /// <summary>
    /// Far outside the world every query answers at once, with nothing there to stand on or overlap (past about -21,475
    /// km a circle's bounds wrapped round an int, and the column walks looped over about 10^15 keys; one far axis alone
    /// cost about 33 million lookups a query.
    /// </summary>
    [Theory]
    [InlineData(-3e7f, -3e7f)]
    [InlineData(-3e7f, 0)]
    [InlineData(0, -3e7f)]
    [InlineData(3e7f, 3e7f)]
    [InlineData(-2.2e7f, 2.2e7f)]
    [InlineData(-1e5f, 0)]
    [InlineData(float.MaxValue, float.MinValue)]
    public void Every_query_far_outside_the_world_answers_at_once(float x, float z)
    {
        var feet = new Vector3(x, 10, z);
        var watch = Stopwatch.StartNew();

        var found = _mover.GroundTop(feet, 0.3f, 1, 1, out var top);
        var overlaps = _mover.Overlaps(feet, in s_shape);
        var overlapsAnything = _mover.OverlapsAnything(feet, in s_shape);
        var resident = _mover.IsResident(feet, 1);
        var moved = feet;
        var velocity = new Vector3(Walk, 0, Walk);
        _mover.Move(ref moved, ref velocity, in s_shape, Step);

        Assert.True(watch.ElapsedMilliseconds < 1000, $"The queries took {watch.ElapsedMilliseconds} ms.");
        Assert.False(found);
        Assert.Equal(0, top);
        Assert.False(overlaps);
        Assert.False(overlapsAnything);
        Assert.True(resident);
    }

    [Fact]
    public void The_ground_where_the_voxels_are_not_loaded_is_not_known()
    {
        // The fixture loads 5 m around the origin.
        var found = _mover.GroundTop(new Vector3(30, 2, 0), 0.03f, 0.4f, 0.4f, out var top);

        Assert.False(found);
        Assert.True(float.IsNaN(top));
    }

    [Fact]
    public void A_50_cm_step_is_too_high()
    {
        var feet = Metres(-280, 200, -80);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 20);

        Assert.InRange(feet.X * 100, -220 - 26 - 0.05f, -220 - 26);
        Assert.Equal(2.00f, feet.Y, 4);
    }

    [Fact]
    public void The_body_walks_under_a_slab_1_8_m_up()
    {
        var feet = Metres(-150, 200, -80);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 16);

        Assert.True(feet.X * 100 > -50, $"Only reached x = {feet.X * 100:F1}.");
        Assert.Equal(2.00f, feet.Y, 4);
    }

    [Fact]
    public void A_ceiling_stops_a_jump()
    {
        var feet = Metres(80, 200, -80);
        var velocity = new Vector3(0, JumpSpeed(1.1f), 0);
        var highest = 0f;
        var hitCeiling = false;

        Run(
            ref feet,
            ref velocity,
            Vector2.Zero,
            60,
            r =>
            {
                highest = Math.Max(highest, feet.Y);
                hitCeiling |= r.HitCeiling;
            },
            jumpFirst: true
        );

        Assert.True(hitCeiling);
        Assert.InRange(highest * 100, 229.9f, 230);
        Assert.Equal(2.00f, feet.Y, 4);
    }

    [Fact]
    public void A_mushroom_stem_blocks()
    {
        var feet = Metres(-280, 200, 80);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 20);

        Assert.True(feet.X * 100 < -220 - 20, $"Walked through the stem to x = {feet.X * 100:F1}.");
        Assert.False(_mover.Overlaps(feet, in s_shape));
    }

    [Fact]
    public void A_pit_1_m_deep_holds_the_body_until_it_jumps_out()
    {
        var feet = Metres(-80, 100, 80);
        var velocity = Vector3.Zero;

        // The pit's air ends at x = -40, so its wall starts at -39.
        Run(ref feet, ref velocity, new Vector2(Walk, 0), 20);
        Assert.InRange(feet.X * 100, -39 - 26 - 0.05f, -39 - 26);
        Assert.Equal(1.00f, feet.Y, 4);

        velocity.Y = JumpSpeed(1.1f);
        Run(ref feet, ref velocity, new Vector2(2, 0), 50, jumpFirst: true);

        Assert.Equal(2.00f, feet.Y, 4);
        Assert.True(feet.X * 100 > -40, $"Still in the pit at x = {feet.X * 100:F1}.");
    }

    [Fact]
    public void Walking_off_a_1_m_ledge_falls_to_the_floor()
    {
        var feet = Metres(280, 300, 80);
        var velocity = Vector3.Zero;
        var leftGround = false;

        Run(ref feet, ref velocity, new Vector2(-Walk, 0), 30, r => leftGround |= !r.Grounded);

        Assert.True(leftGround);
        Assert.Equal(2.00f, feet.Y, 4);
        Assert.True(feet.X * 100 < 220 - 26);
    }

    [Fact]
    public void The_body_waits_where_columns_are_not_loaded()
    {
        var feet = new Vector3(20, 2, 0);
        var velocity = new Vector3(Walk, -3, 0);

        var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.Equal(CharacterMoveStatus.Waiting, result.Status);
        Assert.Equal(new Vector3(20, 2, 0), feet);
        Assert.Equal(Vector3.Zero, velocity);
    }

    [Fact]
    public void Random_moves_never_leave_the_body_inside_a_blocking_voxel()
    {
        var random = new Random(3);
        var feet = Metres(0, 250, 0);
        var velocity = Vector3.Zero;
        var moves = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (i % 400 == 0 || Math.Abs(feet.X) > 3.2f || Math.Abs(feet.Z) > 3.2f || feet.Y < 0.5f)
            {
                feet = Metres(random.Next(-300, 300), random.Next(150, 450), random.Next(-300, 300));
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
            Assert.False(_mover.Overlaps(feet, in s_shape), $"Move {i} left the body inside a voxel at {feet}.");
            if (i % 500 == 0)
            {
                AssertClearVoxelByVoxel(feet);
            }
        }

        Assert.True(moves > 9000, $"Only {moves} moves were not stuck.");
    }

    [Fact]
    public void A_60_degree_slope_cannot_be_walked_up()
    {
        var feet = Metres(40, 200, 240);
        var velocity = Vector3.Zero;

        Run(ref feet, ref velocity, new Vector2(Walk, 0), 40);

        Assert.Equal(2.00f, feet.Y, 4);
        Assert.True(feet.X * 100 < 81 - 26 + 0.1f, $"Walked up to x = {feet.X * 100:F1}.");
    }

    [Fact]
    public void Walking_across_a_60_degree_slope_at_an_angle_does_not_climb_it()
    {
        // 60 degrees off the slope's fall line, where the rise along the walk is about 0.87 per voxel: under 45
        // degrees along the walk, but the ground under the body is still too steep.
        var feet = Metres(40, 200, 180);
        var velocity = Vector3.Zero;
        var highest = 0f;

        Run(
            ref feet,
            ref velocity,
            new Vector2(Walk * 0.5f, Walk * 0.866f),
            16,
            r => highest = Math.Max(highest, feet.Y)
        );

        Assert.True(highest < 2.15f, $"Climbed to {highest:F2} m.");
    }

    [Fact]
    public void A_body_on_a_steep_slope_is_steep_and_its_downhill_points_down_the_slope()
    {
        // Low on the 60-degree slope, so it rises beyond the body: the feet stand on the highest voxel under it.
        var x = 90;
        // The footprint's last cell uphill is x + 25: the cell at x + 26 only touches the circle.
        var feet = Metres(x, MoverWorldFixture.SlopeTop(80, 1.7320508, x + 25), 240);
        var velocity = Vector3.Zero;

        var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.True(result.Grounded);
        Assert.True(result.Steep);
        Assert.True(result.Downhill.X < -0.95f, $"Downhill {result.Downhill}.");
    }

    [Fact]
    public void A_30_degree_slope_is_walked_up_without_leaving_the_ground()
    {
        var feet = Metres(175, 200, 240);
        var velocity = Vector3.Zero;
        var alwaysGrounded = true;
        var everSteep = false;

        Run(
            ref feet,
            ref velocity,
            new Vector2(Walk, 0),
            16,
            r =>
            {
                alwaysGrounded &= r.Grounded;
                everSteep |= r.Steep;
            }
        );

        Assert.True(alwaysGrounded);
        Assert.False(everSteep);
        Assert.Equal(MoverWorldFixture.SlopeTop(200, 0.57735027, 280) / 100f, feet.Y, 4);
    }

    [Fact]
    public void Stairs_of_20_cm_risers_and_30_cm_treads_are_walked_up()
    {
        var feet = Metres(-160, 200, 240);
        var velocity = Vector3.Zero;

        for (var i = 0; i < 40 && feet.X < -0.25f; i++)
        {
            Run(ref feet, ref velocity, new Vector2(Walk, 0), 1);
        }

        Assert.Equal(2.80f, feet.Y, 4);
        Assert.True(feet.X > -0.25f, $"Stopped at x = {feet.X * 100:F1}.");
    }

    [Fact]
    public void A_body_on_the_rim_of_a_ledge_or_a_stone_is_not_steep()
    {
        // The 1 m platform starts at x = 220; a centre 23 cm out leaves a 3 cm rim on it, but nothing rises beside it.
        var feet = Metres(197, 300, 80);
        var velocity = Vector3.Zero;

        var result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.True(result.Grounded);
        Assert.False(result.Steep);
        Assert.Equal(3.00f, feet.Y, 4);

        // The 1 cm bump's edge under the rim of a body beside it.
        feet = Metres(84 + 1, 201, -240);
        result = _mover.Move(ref feet, ref velocity, in s_shape, Step);

        Assert.True(result.Grounded);
        Assert.False(result.Steep);
    }

    [Fact]
    public void Moving_allocates_nothing()
    {
        var feet = Metres(80, 200, 80);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, new Vector2(1, 0.5f), 50);
        var start = feet;
        long first = 0,
            second = 0;

        // The same moves twice, each measured: the first reads every column they reach, where mesh colliders' columns
        // are made (FirstReadsAllocate).
        for (var pass = 0; pass < 2; pass++)
        {
            feet = start;
            velocity = Vector3.Zero;
            var before = SystemAllocation.StartMeasurement();
            for (var i = 0; i < 500; i++)
            {
                var way = i % 100 < 50 ? 1 : -1;
                velocity = new Vector3(way, velocity.Y - Gravity * Step, 0.5f * way);
                _mover.Move(ref feet, ref velocity, in s_shape, Step);
            }

            (pass == 0 ? ref first : ref second) = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.Equal(0, second);
        Assert.True(_features.FirstReadsAllocate || first == 0, $"The first pass allocated {first} bytes.");
        Assert.True(Vector3.Distance(start, feet) < 1);
    }

    [Fact]
    public void Walking_over_bumps_and_up_a_step_costs_little()
    {
        var feet = Metres(200, 200, -240);
        var velocity = Vector3.Zero;
        Run(ref feet, ref velocity, Vector2.Zero, 5);
        var timer = Stopwatch.StartNew();
        const int Moves = 1000;
        for (var i = 0; i < Moves; i++)
        {
            // Back and forth over the 30 cm step.
            var direction = i / 20 % 2 == 0 ? Walk : -Walk;
            velocity = new Vector3(direction, velocity.Y - Gravity * Step, 0);
            _mover.Move(ref feet, ref velocity, in s_shape, Step);
        }

        var microseconds = timer.Elapsed.TotalMicroseconds / Moves;
        _output.WriteLine($"{microseconds:F1} us per move.");
        Assert.True(microseconds < 2000, $"{microseconds:F1} us per move.");
    }

    /// <summary>A body's feet at level-0 voxel (x, y, z), in metres.</summary>
    protected static Vector3 Metres(float x, float y, float z) => new(x / 100, y / 100, z / 100);

    private static float JumpSpeed(float height) => MathF.Sqrt(2 * Gravity * height);

    /// <summary>
    /// Runs ticks that set the horizontal velocity to <paramref name="wanted"/> and add gravity, as a movement system
    /// would. With <paramref name="jumpFirst"/>, the first tick keeps the vertical velocity it was given.
    /// </summary>
    protected CharacterMoveResult Run(
        ref Vector3 feet,
        ref Vector3 velocity,
        Vector2 wanted,
        int ticks,
        Action<CharacterMoveResult>? each = null,
        bool jumpFirst = false
    )
    {
        var result = default(CharacterMoveResult);
        for (var i = 0; i < ticks; i++)
        {
            velocity.X = wanted.X;
            velocity.Z = wanted.Y;
            if (!(jumpFirst && i == 0))
            {
                velocity.Y -= Gravity * Step;
            }

            result = _mover.Move(ref feet, ref velocity, in s_shape, Step);
            Assert.NotEqual(CharacterMoveStatus.Waiting, result.Status);
            Assert.NotEqual(CharacterMoveStatus.Stuck, result.Status);
            each?.Invoke(result);
        }

        return result;
    }

    /// <summary>Checks every 1 cm cell the body covers with <see cref="IMoverFeatures.Blocks"/>.</summary>
    private void AssertClearVoxelByVoxel(Vector3 feet)
    {
        double px = feet.X * 100.0,
            py = feet.Y * 100.0,
            pz = feet.Z * 100.0;
        var footprint = new CircleFootprint(px, pz, s_shape.RadiusMetres * 100.0);
        var y0 = (int)Math.Floor(py + 0.01);
        var y1 = (int)Math.Ceiling(py + s_shape.HeightMetres * 100.0 - 0.01) - 1;
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
                    Assert.False(_features.Blocks(x, y, z), $"Cell ({x}, {y}, {z}) is inside the body at {feet}.");
                }
            }
        }
    }
}
