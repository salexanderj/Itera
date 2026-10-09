using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Physics;

namespace Itera.Engine.Animation;

/// <summary>
/// What a body off its legs keeps between steps as it rests on the ground under its supports (<see cref="StanceGround"/>):
/// the slope it tilts to and the height it adds, eased.
/// </summary>
public struct StanceRest
{
    /// <summary>The slope it tilts to along its right (+x): the ground's rise a voxel across.</summary>
    public float SlopeX;

    /// <summary>The slope it tilts to along its back (+z): the ground's rise a voxel back.</summary>
    public float SlopeZ;

    /// <summary>What it is raised by beyond standing its lowest support at its feet' height, in level-0 voxels.</summary>
    public float Rise;
}

/// <summary>
/// Rests a body off its legs on the voxel ground under the bones it supports itself
/// on: finds each support's lowest point (<see cref="Lowest"/>, from the support points the caller gives for each bone,
/// <see cref="SupportPoints"/>) and the ground under it, tilts the whole body about its root to the slope that ground lies
/// on (at most <see cref="MaxTiltDegrees"/>), and raises or lowers it so that no support sinks and the highest contact
/// touches. The tilt and the height are eased over steps, as foot placement eases its drop. A bone the caller holds clear
/// that would reach into the ground is lifted by its parent's joint. Where the level-0 voxels are not loaded the ground
/// is taken as level with the feet. Level-0 voxels in the body's frame; main thread (it reads the voxels); allocates
/// nothing.
/// </summary>
public static class StanceGround
{
    /// <summary>The steepest ground a body tilts to follow.</summary>
    public const double MaxTiltDegrees = 25;

    /// <summary>The most a held-clear bone's parent turns to lift it.</summary>
    public const double MaxLiftDegrees = 45;

    /// <summary>The most times a held-clear bone's parent turns in a step to lift it.</summary>
    public const int HoldTurns = 4;

    /// <summary>The most supports a body rests on: a skeleton's most bones.</summary>
    private const int MostSupports = 32;

    private static readonly double s_maxSlope = Math.Tan(MaxTiltDegrees * Math.PI / 180);

    /// <summary>
    /// Rests the pose in <paramref name="turns"/> (its root and any held-clear bone's parent turned in place) on the
    /// ground under a body of <paramref name="skeleton"/> at <paramref name="feet"/> turned by <paramref name="yaw"/>, on
    /// <paramref name="supports"/> (bones) less <paramref name="lostBones"/>, moving <paramref name="eased"/> on by
    /// <paramref name="ease"/> (the share of the way a step moves), and returns the skeleton's lift.
    /// <paramref name="supportPoints"/> holds each bone's support points in the bone's space (an empty array for a bone
    /// with none). <paramref name="bones"/> (one per bone) is scratch, left holding every bone's transform, raised by
    /// nothing. When no support is left, it returns <paramref name="standingLift"/> (NaN by default, which stands the body
    /// on its lowest foot) and clears <paramref name="eased"/>.
    /// </summary>
    public static double Apply(
        VoxelCharacterMover mover,
        Skeleton skeleton,
        ReadOnlySpan<(double X, double Y, double Z)[]> supportPoints,
        ReadOnlySpan<int> supports,
        ReadOnlySpan<int> heldClear,
        uint lostBones,
        Span<Rotation> turns,
        Span<RigidTransform> bones,
        Vector3 feet,
        float yaw,
        ref StanceRest eased,
        double ease,
        double standingLift = double.NaN
    )
    {
        Span<int> resting = stackalloc int[MostSupports];
        var count = 0;
        foreach (var bone in supports)
        {
            if ((lostBones & (1u << bone)) == 0 && count < MostSupports)
            {
                resting[count++] = bone;
            }
        }

        if (count == 0)
        {
            // Nothing to rest on: it stands as the caller says.
            eased = default;
            return standingLift;
        }

        // Where each support's lowest corner lies as posed, and the ground under it (relative to the feet). The ground is
        // looked for as far above and below as the steepest slope followed rises over the support's distance from the feet.
        skeleton.Pose(turns, bones);
        Span<double> xs = stackalloc double[count];
        Span<double> zs = stackalloc double[count];
        Span<double> grounds = stackalloc double[count];
        for (var i = 0; i < count; i++)
        {
            var (x, _, z) = Lowest(supportPoints[resting[i]], bones[resting[i]]);
            xs[i] = x;
            zs[i] = z;
            var reach = skeleton.StepVoxels + Math.Sqrt(x * x + z * z) * s_maxSlope;
            var ground = FootPlacement.GroundNear(mover, x, z, 0, reach, feet, yaw);
            grounds[i] = double.IsNaN(ground) ? 0 : ground;
        }

        // The slope the ground lies on under them, and the body tilted to it about its root, eased.
        var (slopeX, slopeZ) = Slope(xs, zs, grounds);
        eased.SlopeX += (float)((slopeX - eased.SlopeX) * ease);
        eased.SlopeZ += (float)((slopeZ - eased.SlopeZ) * ease);
        // The root (bone 0) turns in the body's frame: after its rest turn, before its own.
        var rest = skeleton.Rest(0);
        var tilt = UpTo(-eased.SlopeX, 1, -eased.SlopeZ);
        turns[0] = rest.Transposed().Times(tilt).Times(rest).Times(turns[0]);

        // Raised so that no support sinks into the ground under it and the highest touches, and the lowest support
        // standing at the feet' height, eased. The ground under each is carried along the slope from where it was found
        // to where the tilt moved the support.
        skeleton.Pose(turns, bones);
        var lowest = double.PositiveInfinity;
        var needed = double.NegativeInfinity;
        for (var i = 0; i < count; i++)
        {
            var (x, y, z) = Lowest(supportPoints[resting[i]], bones[resting[i]]);
            var ground = grounds[i] + slopeX * (x - xs[i]) + slopeZ * (z - zs[i]);
            lowest = Math.Min(lowest, y);
            needed = Math.Max(needed, ground - y);
        }

        eased.Rise += (float)((needed + lowest - eased.Rise) * ease);
        var lift = eased.Rise - lowest;
        foreach (var bone in heldClear)
        {
            if ((lostBones & (1u << bone)) == 0)
            {
                HoldClear(mover, skeleton, supportPoints[bone], bone, turns, bones, lift, feet, yaw);
            }
        }

        return lift;
    }

    /// <summary>
    /// The lowest of <paramref name="points"/> (support points in a bone's space) placed by the bone's
    /// <paramref name="transform"/>, from where it stands now: on the part's voxels however it is turned, unlike the
    /// corners of its box. Level-0 voxels; allocates nothing. A bone with no points gives its joint.
    /// </summary>
    public static (double X, double Y, double Z) Lowest(
        ReadOnlySpan<(double X, double Y, double Z)> points,
        in RigidTransform transform
    )
    {
        var r = transform.Rotation;
        var lowest = double.PositiveInfinity;
        var at = (0.0, 0.0, 0.0);
        foreach (var (x, y, z) in points)
        {
            var height = r.Myx * x + r.Myy * y + r.Myz * z;
            if (height < lowest)
            {
                lowest = height;
                at = (x, y, z);
            }
        }

        return double.IsFinite(lowest)
            ? transform.Apply(at.Item1, at.Item2, at.Item3)
            : (transform.X, transform.Y, transform.Z);
    }

    /// <summary>
    /// The slope of the plane that fits the ground heights <paramref name="grounds"/> at (<paramref name="xs"/>,
    /// <paramref name="zs"/>) best (least squares), along x and z, held within <see cref="MaxTiltDegrees"/>. Along a
    /// direction the supports do not spread over (a line of them, or one) it is level.
    /// </summary>
    private static (double X, double Z) Slope(
        ReadOnlySpan<double> xs,
        ReadOnlySpan<double> zs,
        ReadOnlySpan<double> grounds
    )
    {
        var n = xs.Length;
        double mx = 0,
            mz = 0,
            mg = 0;
        for (var i = 0; i < n; i++)
        {
            mx += xs[i];
            mz += zs[i];
            mg += grounds[i];
        }

        (mx, mz, mg) = (mx / n, mz / n, mg / n);
        double sxx = 0,
            szz = 0,
            sxz = 0,
            sxg = 0,
            szg = 0;
        for (var i = 0; i < n; i++)
        {
            double dx = xs[i] - mx,
                dz = zs[i] - mz,
                dg = grounds[i] - mg;
            sxx += dx * dx;
            szz += dz * dz;
            sxz += dx * dz;
            sxg += dx * dg;
            szg += dz * dg;
        }

        // Spread by less than a voxel's square on average along an axis is no spread.
        var spread = n;
        double slopeX = 0,
            slopeZ = 0;
        var determinant = sxx * szz - sxz * sxz;
        if (sxx > spread && szz > spread && determinant > 1e-3 * sxx * szz)
        {
            slopeX = (szz * sxg - sxz * szg) / determinant;
            slopeZ = (sxx * szg - sxz * sxg) / determinant;
        }
        else if (szz > spread && szz >= sxx)
        {
            slopeZ = szg / szz;
        }
        else if (sxx > spread)
        {
            slopeX = sxg / sxx;
        }

        var steepness = Math.Sqrt(slopeX * slopeX + slopeZ * slopeZ);
        return steepness > s_maxSlope
            ? (slopeX * s_maxSlope / steepness, slopeZ * s_maxSlope / steepness)
            : (slopeX, slopeZ);
    }

    /// <summary>
    /// Lifts <paramref name="bone"/> clear of the ground under its lowest corner, when it reaches into it, by turning its
    /// parent at the parent's joint: up at most <see cref="MaxLiftDegrees"/> in all. Each turn brings another corner
    /// lowest, so it turns again, <see cref="HoldTurns"/> times at most.
    /// </summary>
    private static void HoldClear(
        VoxelCharacterMover mover,
        Skeleton skeleton,
        ReadOnlySpan<(double X, double Y, double Z)> points,
        int bone,
        Span<Rotation> turns,
        Span<RigidTransform> bones,
        double lift,
        Vector3 feet,
        float yaw
    )
    {
        if (bone == 0)
        {
            return;
        }

        var parent = skeleton.Parent(bone);
        var left = MaxLiftDegrees * Math.PI / 180;
        for (var turn = 0; turn < HoldTurns && left > 0; turn++)
        {
            var (x, y, z) = Lowest(points, bones[bone]);
            var ground = FootPlacement.GroundNear(mover, x, z, y + lift, skeleton.StepVoxels, feet, yaw);
            var depth = (double.IsNaN(ground) ? 0 : ground) - (y + lift);
            if (depth <= 0)
            {
                return;
            }

            // From the parent's joint to the corner, turned up about the level axis across it until the corner is as
            // high as the ground.
            var joint = bones[parent];
            double vx = x - joint.X,
                vy = y - joint.Y,
                vz = z - joint.Z;
            var across = Math.Sqrt(vx * vx + vz * vz);
            var length = Math.Sqrt(across * across + vy * vy);
            if (across < 1e-6)
            {
                return;
            }

            var now = Math.Atan2(vy, across);
            var then = Math.Asin(Math.Clamp((vy + depth) / length, -1, 1));
            var angle = Math.Min(then - now, left);
            left -= angle;
            // About v x up = (-vz, 0, vx), a turn by a positive angle takes v towards up.
            var raise = About(-vz / across, 0, vx / across, angle);

            // The parent's turn in its own parent's frame: raised in the body's frame, where the joint turns.
            var above = (parent == 0 ? Rotation.Identity : bones[skeleton.Parent(parent)].Rotation).Times(
                skeleton.Rest(parent)
            );
            turns[parent] = above.Transposed().Times(raise).Times(above).Times(turns[parent]);
            skeleton.Pose(turns, bones);
        }
    }

    /// <summary>The shortest rotation that takes up (+y) to the direction (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>).</summary>
    private static Rotation UpTo(double x, double y, double z)
    {
        var length = Math.Sqrt(x * x + y * y + z * z);
        (x, y, z) = (x / length, y / length, z / length);
        // Rodrigues' rotation from up to n: I + [v] + [v]^2 / (1 + c), with v = up x n = (z, 0, -x) and c = y.
        double vx = z,
            vz = -x,
            k = 1 / (1 + y);
        return new Rotation(
            1 - vz * vz * k,
            -vz,
            vx * vz * k,
            vz,
            1 - (vx * vx + vz * vz) * k,
            -vx,
            vx * vz * k,
            vx,
            1 - vx * vx * k
        );
    }

    /// <summary>The rotation by <paramref name="angle"/> radians about the unit axis (<paramref name="ax"/>, <paramref name="ay"/>, <paramref name="az"/>).</summary>
    private static Rotation About(double ax, double ay, double az, double angle)
    {
        var (sin, cos) = Math.SinCos(angle);
        var t = 1 - cos;
        return new Rotation(
            t * ax * ax + cos,
            t * ax * ay - sin * az,
            t * ax * az + sin * ay,
            t * ax * ay + sin * az,
            t * ay * ay + cos,
            t * ay * az - sin * ax,
            t * ax * az - sin * ay,
            t * ay * az + sin * ax,
            t * az * az + cos
        );
    }
}
