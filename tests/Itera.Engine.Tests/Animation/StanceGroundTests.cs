using System.Numerics;
using Itera.Engine.Animation;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Animation;

/// <summary>
/// <see cref="StanceGround"/>, on a test body of a trunk, four pads, and a head on a neck: on flat ground the lowest
/// support point rests on it, neither above nor below; on a real 17-degree slope, across and up it, the body tilts to the
/// slope with no point sinking into it and a support touching it; a head the caller holds clear stays out of the ground;
/// with no support left the caller's lift comes back; resting allocates nothing. Also <see cref="SupportPoints"/>, and that
/// <see cref="StanceGround.Lowest"/> finds the lowest point however a bone is turned.
/// </summary>
[RunsLong]
public sealed class StanceGroundTests(ITestOutputHelper output) : IDisposable
{
    private const int Trunk = 0;
    private const int FirstPad = 1;
    private const int Neck = 5;
    private const int Head = 6;

    // Flat (the ramp's foot) for x at or below 0; rising along +x by 0.3 a voxel (about 17 degrees) to a flat top 3 m up.
    private static readonly RampGenerator s_ramp = new(300, 300);

    private static readonly PadBody s_skeleton = new();

    private static readonly (double X, double Y, double Z)[][] s_points = PadBody.Points();

    private static readonly int[] s_pads = [1, 2, 3, 4];

    private static readonly int[] s_held = [Head];

    private VoxelWorld? _voxels;

    public void Dispose() => _voxels?.Dispose();

    [Theory]
    [InlineData(0u)]
    [InlineData(1u << 1)]
    public void On_flat_ground_the_lowest_support_rests_on_it(uint lost)
    {
        var mover = Ramp();
        // A body's feet 2 cm over the ground, on the ramp's flat foot.
        var feet = new Vector3(-2.2f, 0.02f, 0);
        var turns = BodyPose.Rest.Turns.ToArray();
        var bones = new RigidTransform[s_skeleton.BoneCount];
        var rest = new StanceRest();

        var lift = StanceGround.Apply(
            mover,
            s_skeleton,
            s_points,
            s_pads,
            [],
            lost,
            turns,
            bones,
            feet,
            0,
            ref rest,
            1
        );

        var (lowest, bone) = LowestPoint(turns, lift, lost, World(0, feet), s_pads);
        output.WriteLine($"lost {lost:x}: lowest support point {lowest:F2} cm (bone {bone})");
        Assert.InRange(lowest, -1, 1);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-MathF.PI / 2)]
    public void On_a_real_slope_it_tilts_to_it_with_nothing_sunk_and_a_support_touching(float yaw)
    {
        var mover = Ramp();
        Assert.True(mover.GroundTop(new Vector3(4, 1.5f, 0.3f), 0.3f, 2, 2, out var top));
        var feet = new Vector3(4, top + 0.02f, 0.3f);
        var turns = new Rotation[s_skeleton.BoneCount];
        var bones = new RigidTransform[s_skeleton.BoneCount];
        var rest = new StanceRest();
        var lift = 0.0;
        // Eased over a second at a fixed step's rate, resting in place.
        for (var step = 0; step < 60; step++)
        {
            BodyPose.Rest.Turns[..s_skeleton.BoneCount].CopyTo(turns);
            lift = StanceGround.Apply(
                mover,
                s_skeleton,
                s_points,
                s_pads,
                [Head],
                0,
                turns,
                bones,
                feet,
                yaw,
                ref rest,
                1 / 6.0
            );
        }

        var steepness = Math.Sqrt(rest.SlopeX * rest.SlopeX + rest.SlopeZ * rest.SlopeZ);
        output.WriteLine($"yaw {yaw:F2}: slope ({rest.SlopeX:F3}, {rest.SlopeZ:F3}), rise {rest.Rise:F2}");
        Assert.InRange(steepness, 0.25, 0.35);

        // Every point over the ground under it, the lowest of the supports on it.
        var world = World(yaw, feet);
        var (sunk, sunkBone) = LowestPoint(turns, lift, 0, world, [.. s_pads, Neck, Head]);
        var (touching, touchingBone) = LowestPoint(turns, lift, 0, world, s_pads);
        output.WriteLine(
            $"deepest {sunk:F2} cm (bone {sunkBone}), lowest support {touching:F2} cm (bone {touchingBone})"
        );
        Assert.True(sunk >= -1.5, $"Bone {sunkBone} sinks {-sunk:F2} cm into the slope.");
        Assert.True(
            touching <= 1.5,
            $"Its lowest support (bone {touchingBone}) floats {touching:F2} cm over the slope."
        );
    }

    [Fact]
    public void A_head_held_clear_keeps_out_of_the_ground_where_it_would_sink()
    {
        var mover = Ramp();
        var feet = new Vector3(-2.2f, 0.02f, 0);
        var bones = new RigidTransform[s_skeleton.BoneCount];
        var sunkTurns = BodyPose.Rest.Turns.ToArray();
        var clearTurns = BodyPose.Rest.Turns.ToArray();
        var rest = new StanceRest();
        var clearRest = new StanceRest();

        var sunkLift = StanceGround.Apply(
            mover,
            s_skeleton,
            s_points,
            s_pads,
            [],
            0,
            sunkTurns,
            bones,
            feet,
            0,
            ref rest,
            1
        );
        var clearLift = StanceGround.Apply(
            mover,
            s_skeleton,
            s_points,
            s_pads,
            [Head],
            0,
            clearTurns,
            bones,
            feet,
            0,
            ref clearRest,
            1
        );

        var (sunk, _) = LowestPoint(sunkTurns, sunkLift, 0, World(0, feet), [Head]);
        var (clear, _) = LowestPoint(clearTurns, clearLift, 0, World(0, feet), [Head]);
        output.WriteLine($"head without hold {sunk:F2} cm, held clear {clear:F2} cm");
        Assert.True(sunk < -5, $"The head should start {-sunk:F2} cm in the ground.");
        Assert.InRange(clear, -1, 3);
    }

    [Fact]
    public void With_no_support_left_the_callers_lift_comes_back_and_the_rest_clears()
    {
        var mover = Ramp();
        var turns = BodyPose.Rest.Turns.ToArray();
        var bones = new RigidTransform[s_skeleton.BoneCount];
        var rest = new StanceRest
        {
            SlopeX = 0.2f,
            SlopeZ = 0.1f,
            Rise = 3,
        };

        var lift = StanceGround.Apply(
            mover,
            s_skeleton,
            s_points,
            s_pads,
            [],
            0b11110,
            turns,
            bones,
            default,
            0,
            ref rest,
            1,
            42
        );
        var unset = StanceGround.Apply(
            mover,
            s_skeleton,
            s_points,
            s_pads,
            [],
            0b11110,
            turns,
            bones,
            default,
            0,
            ref rest,
            1
        );

        Assert.Equal(42, lift);
        Assert.True(double.IsNaN(unset));
        Assert.Equal(default, rest);
    }

    [Fact]
    public void The_lowest_point_is_the_lowest_of_the_points_however_the_bone_is_turned()
    {
        var points = s_points[FirstPad];
        var random = new Random(5);
        for (var i = 0; i < 40; i++)
        {
            var turn = Rotation
                .AboutX(random.NextDouble())
                .Times(Rotation.AboutY(random.NextDouble()))
                .Times(Rotation.AboutZ(random.NextDouble()));
            var transform = new RigidTransform(turn, random.Next(-50, 50), random.Next(-50, 50), random.Next(-50, 50));

            var (x, y, z) = StanceGround.Lowest(points, transform);

            var exact = points.Min(p => transform.Apply(p.X, p.Y, p.Z).Y);
            Assert.Equal(exact, y, 1e-9);
            // It is one of the points, placed.
            Assert.Contains(points, p => transform.Apply(p.X, p.Y, p.Z) == (x, y, z));
        }

        // A bone without points gives its joint.
        Assert.Equal((3.0, 4.0, 5.0), StanceGround.Lowest([], RigidTransform.Move(3, 4, 5)));
    }

    [Fact]
    public void The_support_points_of_a_box_are_its_corners_and_the_lowest_is_within_a_centimetre_however_it_is_turned()
    {
        // A box of 20 x 30 x 40 voxels at (3, 4, 5) in a chunk of voxels, as a body part's volume leaves it.
        const int edge = VoxelConstants.ChunkSize;
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < 40; z++)
        {
            for (var y = 0; y < 30; y++)
            {
                voxels.AsSpan((z * edge + y) * edge, 20).Fill(3);
            }
        }

        var origin = (X: 3.0, Y: 4.0, Z: 5.0);
        var points = SupportPoints.Of(voxels, new VoxelCoord(20, 30, 40), 0, origin);

        Assert.InRange(points.Length, 1, SupportPoints.Directions);
        Assert.Equal(points.Length, points.Distinct().Count());
        Assert.Equal([.. points.Order()], points);
        Assert.All(
            points,
            p =>
            {
                Assert.Contains(p.X, (double[])[3, 23]);
                Assert.Contains(p.Y, (double[])[4, 34]);
                Assert.Contains(p.Z, (double[])[5, 45]);
            }
        );
        var random = new Random(9);
        for (var i = 0; i < 40; i++)
        {
            var turn = Rotation
                .AboutX(random.NextDouble())
                .Times(Rotation.AboutY(random.NextDouble()))
                .Times(Rotation.AboutZ(random.NextDouble()));
            var transform = new RigidTransform(turn, 0, 0, 0);

            var (_, y, _) = StanceGround.Lowest(points, transform);

            var exact = double.PositiveInfinity;
            foreach (var x in (ReadOnlySpan<double>)[3, 23])
            {
                foreach (var cy in (ReadOnlySpan<double>)[4, 34])
                {
                    foreach (var cz in (ReadOnlySpan<double>)[5, 45])
                    {
                        exact = Math.Min(exact, transform.Apply(x, cy, cz).Y);
                    }
                }
            }

            Assert.InRange(y - exact, -1e-9, 1);
        }

        Assert.Empty(SupportPoints.Of(new byte[VoxelConstants.ChunkVoxels], new VoxelCoord(20, 30, 40), 0, origin));
    }

    [Fact]
    public void Resting_allocates_nothing()
    {
        var mover = Ramp();
        var turns = new Rotation[s_skeleton.BoneCount];
        var bones = new RigidTransform[s_skeleton.BoneCount];
        var feet = new Vector3(4, 1.3f, 0.3f);
        var rest = new StanceRest();
        void Rest()
        {
            BodyPose.Rest.Turns[..s_skeleton.BoneCount].CopyTo(turns);
            StanceGround.Apply(mover, s_skeleton, s_points, s_pads, s_held, 0, turns, bones, feet, 0.4f, ref rest, 0.2);
        }

        for (var i = 0; i < 100; i++)
        {
            Rest();
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            Rest();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private VoxelCharacterMover Ramp()
    {
        _voxels ??= TestWorlds.Load(s_ramp, new Vector3(1, 1, 0), radiusMetres: 8);
        return new VoxelCharacterMover(_voxels, new NothingPassable());
    }

    /// <summary>From the body's frame (level-0 voxels) to the world's, for a body at <paramref name="feet"/> turned by <paramref name="yaw"/>.</summary>
    private static RigidTransform World(float yaw, Vector3 feet) =>
        new(Rotation.AboutY(yaw / (2 * Math.PI)), feet.X * 100.0, feet.Y * 100.0, feet.Z * 100.0);

    /// <summary>
    /// The lowest support point's height over the ramp's ground under it, in level-0 voxels, over the points of
    /// <paramref name="only"/> less <paramref name="lost"/>, and its bone.
    /// </summary>
    private static (double Gap, int Bone) LowestPoint(
        ReadOnlySpan<Rotation> turns,
        double lift,
        uint lost,
        RigidTransform world,
        ReadOnlySpan<int> only
    )
    {
        var transforms = new RigidTransform[s_skeleton.BoneCount];
        s_skeleton.Pose(turns, transforms);
        var raised = world.Times(RigidTransform.Move(0, lift, 0));
        var lowest = double.PositiveInfinity;
        var lowestBone = -1;
        foreach (var bone in only)
        {
            if ((lost & (1u << bone)) != 0)
            {
                continue;
            }

            var placed = raised.Times(transforms[bone]);
            foreach (var (px, py, pz) in s_points[bone])
            {
                var (x, y, _) = placed.Apply(px, py, pz);
                var gap = y - s_ramp.Height((int)Math.Floor(x));
                if (gap < lowest)
                {
                    lowest = gap;
                    lowestBone = bone;
                }
            }
        }

        return (lowest, lowestBone);
    }

    /// <summary>
    /// A test body: a trunk (bone 0) with four pads under its corners (bones 1 to 4, 60 voxels across and 100 long), and a
    /// neck (bone 5) and head (bone 6) ahead of it. The pads' bottoms lie on the ground at rest; the head hangs 8 voxels
    /// lower.
    /// </summary>
    private sealed class PadBody : Skeleton
    {
        private static readonly (double X, double Z)[] s_corners = [(30, -50), (-30, -50), (30, 50), (-30, 50)];

        public PadBody()
            : base(7)
        {
            Set(Trunk, 0, (0, 0, 0), Rotation.Identity);
            for (var i = 0; i < 4; i++)
            {
                Set(FirstPad + i, Trunk, (s_corners[i].X, 6, s_corners[i].Z), Rotation.Identity);
            }

            Set(Neck, Trunk, (0, 12, -50), Rotation.Identity);
            Set(Head, Neck, (0, 0, -20), Rotation.Identity);
            StepVoxels = 20;
        }

        /// <summary>The corners of each bone's box in the bone's space, by bone.</summary>
        public static (double X, double Y, double Z)[][] Points()
        {
            var points = new (double X, double Y, double Z)[7][];
            points[Trunk] = [];
            for (var i = 0; i < 4; i++)
            {
                points[FirstPad + i] = Box(-5, 5, -6, 0, -5, 5);
            }

            points[Neck] = [];
            points[Head] = Box(-5, 5, -20, 0, -8, 8);
            return points;
        }

        private static (double X, double Y, double Z)[] Box(
            double minX,
            double maxX,
            double minY,
            double maxY,
            double minZ,
            double maxZ
        ) =>
            [
                (minX, minY, minZ),
                (maxX, minY, minZ),
                (minX, maxY, minZ),
                (maxX, maxY, minZ),
                (minX, minY, maxZ),
                (maxX, minY, maxZ),
                (minX, maxY, maxZ),
                (maxX, maxY, maxZ),
            ];
    }
}
