using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Animation;

/// <summary>
/// Puts a body's feet on the voxel ground, for any number of legs
/// (<see cref="Skeleton.Legs"/>, at most <see cref="MaxLegs"/>). <see cref="Probe"/> finds the ground under each foot's
/// heel and toe; <see cref="Apply(Skeleton, Span{Rotation}, double, ReadOnlySpan{LegGround}, Span{RigidTransform}, double, ref double, double, uint)"/> then, for a skeleton that tilts its trunk (four legs or more), turns the root to
/// the slope under the feet, moves each ankle up or down by the ground under it, lowers the root by what a planted foot
/// must reach down (at most the step height), bends each leg to reach its ankle (two bones, the knee bent the leg's
/// way), and tilts each foot to the slope between heel and toe (a leg's third bone, its foot, keeps its turn in the body
/// otherwise). On flat ground it leaves the pose as it is. Lengths in level-0 voxels in the body's frame (origin on the
/// ground between the feet, facing -z); turns in turns. Allocates nothing.
/// </summary>
public static class FootPlacement
{
    /// <summary>The most legs a skeleton may have.</summary>
    public const int MaxLegs = 8;

    /// <summary>
    /// A foot whose sole is on the ground in the gait is planted, and one coming down is planted by degrees over the
    /// last of its fall: over this height at least, in level-0 voxels, or <see cref="PlantedLegShare"/> of the leg.
    /// </summary>
    public const double PlantedVoxels = 6;

    /// <summary>The share of the leg's length over which a foot coming down becomes planted, when more than
    /// <see cref="PlantedVoxels"/>.</summary>
    public const double PlantedLegShare = 0.1;

    /// <summary>How far a foot may tilt to the slope, in turns: 25 degrees.</summary>
    public const double MaxPitch = 25.0 / 360;

    /// <summary>How far a trunk may tilt to the slope under its feet, forward or to the side, in turns: 20 degrees.</summary>
    public const double MaxTilt = 20.0 / 360;

    /// <summary>The radius of ground looked at under a heel or a toe, in metres.</summary>
    public const float ProbeRadiusMetres = 0.02f;

    /// <summary>
    /// The ground under one foot: how far above the feet (up positive) it stands, in level-0 voxels, and the slope under
    /// it along the body, in turns (toes up positive).
    /// </summary>
    public readonly record struct LegGround(double Height, double Pitch)
    {
        /// <summary>True when both are next to nothing.</summary>
        public bool IsFlat => Math.Abs(Height) < 0.05 && Math.Abs(Pitch) < 1e-4;
    }

    /// <summary>How far a body of <paramref name="skeleton"/> steps up or down at most, in voxels.</summary>
    public static double StepVoxels(Skeleton skeleton) => skeleton.StepVoxels;

    /// <summary>
    /// Writes the ground under each foot (one per leg, into <paramref name="ground"/>) of a body standing at
    /// <paramref name="feet"/> (metres) turned by <paramref name="yaw"/> radians, whose bones stand as
    /// <paramref name="bones"/> (each bone's space to the body's frame, unraised): the highest ground under heel and toe
    /// within <see cref="StepVoxels"/> above or below the feet; where there is none, the foot reaches the whole step
    /// down. Where the level-0 voxels under a foot are not loaded (far from the camera), its ground is not known and is
    /// taken as level with the feet, so the pose stays as the gait makes it. Main thread (it reads the voxels).
    /// </summary>
    public static void Probe(
        VoxelCharacterMover mover,
        Skeleton skeleton,
        ReadOnlySpan<RigidTransform> bones,
        Vector3 feet,
        float yaw,
        Span<LegGround> ground
    )
    {
        var legs = skeleton.Legs;
        for (var i = 0; i < legs.Length; i++)
        {
            ground[i] = ProbeFoot(mover, skeleton, legs[i], bones[legs[i].Foot], feet, yaw);
        }
    }

    /// <summary>
    /// Places the feet of the pose in <paramref name="turns"/> (changed in place: the root when it tilts, and each leg's
    /// bones) on <paramref name="ground"/> (one per leg), for a skeleton raised by <paramref name="lift"/> (its standing
    /// lift); returns the new lift, lower by as much as the root drops. <paramref name="press"/> presses the root down
    /// further, in level-0 voxels (a landing), with the feet kept where they stand. <paramref name="bones"/> (one per
    /// bone) is scratch.
    /// </summary>
    public static double Apply(
        Skeleton skeleton,
        Span<Rotation> turns,
        double lift,
        ReadOnlySpan<LegGround> ground,
        Span<RigidTransform> bones,
        double press = 0
    )
    {
        var drop = double.NaN;
        return Apply(skeleton, turns, lift, ground, bones, press, ref drop, 1);
    }

    /// <summary>
    /// As <see cref="Apply(Skeleton, Span{Rotation}, double, ReadOnlySpan{LegGround}, Span{RigidTransform}, double)"/>,
    /// with how far the root comes down eased from one step to the next: <paramref name="drop"/> holds it (level-0
    /// voxels; NaN for none yet), and moves <paramref name="ease"/> of the way (0 to 1) to where the ground puts it, so
    /// the root never starts or stops coming down all at once (as one planted foot takes over from another). The legs in
    /// <paramref name="skipLegs"/> (one bit per leg: those that cannot bear weight) are left as
    /// the pose has them and nothing stands on them.
    /// </summary>
    public static double Apply(
        Skeleton skeleton,
        Span<Rotation> turns,
        double lift,
        ReadOnlySpan<LegGround> ground,
        Span<RigidTransform> bones,
        double press,
        ref double drop,
        double ease,
        uint skipLegs = 0
    )
    {
        var legs = skeleton.Legs;
        var flat = true;
        for (var i = 0; i < legs.Length; i++)
        {
            flat &= ground[i].IsFlat || Skips(skipLegs, i);
        }

        if (flat && press <= 0 && (double.IsNaN(drop) || Math.Abs(drop) < 1e-3))
        {
            drop = 0;
            return lift;
        }

        skeleton.PoseLegs(turns, bones);
        Span<Vector3D> targets = stackalloc Vector3D[MaxLegs];
        Span<Rotation> feet = stackalloc Rotation[MaxLegs];
        Span<double> planted = stackalloc double[MaxLegs];
        var plantedOver = Math.Max(PlantedVoxels, PlantedLegShare * skeleton.LegVoxels);
        for (var i = 0; i < legs.Length; i++)
        {
            if (Skips(skipLegs, i))
            {
                continue;
            }

            // Each ankle moves by the ground under it; each foot keeps its turn in the body, tilted to the slope. A foot
            // is planted by degrees as its sole comes down to the ground, so the root follows the ground under it
            // gradually rather than all at once (on a slope a large body's knees snapped 30 degrees in one step).
            // The sole's height is taken from the foot as it is turned: a folded foot's tip is not below its ankle.
            var ankle = Position(bones[legs[i].Foot], lift);
            var sole = bones[legs[i].Foot].Apply(0, legs[i].Sole, 0).Y + lift;
            var down = Math.Clamp(1 - sole / plantedOver, 0, 1);
            planted[i] = down * down * (3 - 2 * down);
            targets[i] = ankle + new Vector3D(0, ground[i].Height, 0);
            feet[i] = Rotation.AboutX(ground[i].Pitch).Times(bones[legs[i].Foot].Rotation);
        }

        var tilts = skeleton.TiltsTrunk;
        if (tilts)
        {
            // The trunk turns to the slope; the feet stay where the ground puts them.
            Tilt(skeleton, turns, ground, bones, skipLegs);
            skeleton.PoseLegs(turns, bones);
        }

        // The root comes down as far as a planted foot must reach below where its ankle now is, each foot's reach
        // weighed by how planted it is; with no foot planted, as far as the lowest must.
        var plantedDrop = double.NegativeInfinity;
        var lowest = double.PositiveInfinity;
        for (var i = 0; i < legs.Length; i++)
        {
            if (Skips(skipLegs, i))
            {
                continue;
            }

            var reach = tilts ? targets[i].Y - Position(bones[legs[i].Foot], lift).Y : ground[i].Height;
            if (planted[i] > 0)
            {
                plantedDrop = Math.Max(plantedDrop, -reach * planted[i]);
            }

            lowest = Math.Min(lowest, reach);
        }

        var wanted = Math.Clamp(
            double.IsNegativeInfinity(plantedDrop) ? -lowest : plantedDrop,
            0,
            StepVoxels(skeleton)
        );
        drop = double.IsNaN(drop) ? wanted : drop + (wanted - drop) * Math.Clamp(ease, 0, 1);
        var placed = lift - drop - Math.Max(0, press);

        // The knees bend to make each hip-to-ankle distance, then each upper bone turns the ankle onto its target.
        for (var i = 0; i < legs.Length; i++)
        {
            if (!Skips(skipLegs, i))
            {
                Knee(skeleton, turns, bones, legs[i], targets[i], placed);
            }
        }

        skeleton.PoseLegs(turns, bones);
        for (var i = 0; i < legs.Length; i++)
        {
            if (!Skips(skipLegs, i))
            {
                Upper(skeleton, turns, bones, legs[i], targets[i], placed);
            }
        }

        skeleton.PoseLegs(turns, bones);
        for (var i = 0; i < legs.Length; i++)
        {
            if (!Skips(skipLegs, i))
            {
                Foot(skeleton, turns, bones, legs[i].Foot, feet[i]);
            }
        }

        return placed;
    }

    /// <summary>
    /// Moves each leg's ankle to its target in <paramref name="targets"/> (one per leg, in the body's frame, unraised),
    /// each foot keeping its turn in the body: a gait whose feet move in straight lines uses it. The pose in
    /// <paramref name="turns"/> is changed in place; <paramref name="bones"/> (one per bone) is scratch, left holding the
    /// root's and legs' transforms.
    /// </summary>
    public static void Reach(
        Skeleton skeleton,
        Span<Rotation> turns,
        ReadOnlySpan<(double X, double Y, double Z)> targets,
        Span<RigidTransform> bones
    )
    {
        var legs = skeleton.Legs;
        skeleton.PoseLegs(turns, bones);
        Span<Rotation> feet = stackalloc Rotation[MaxLegs];
        for (var i = 0; i < legs.Length; i++)
        {
            feet[i] = bones[legs[i].Foot].Rotation;
        }

        ReachWithFeet(skeleton, turns, targets, feet[..legs.Length], bones);
    }

    /// <summary>
    /// As <see cref="Reach"/>, with each foot given its turn in the body (<paramref name="feet"/>, one per leg): a gait that rolls and folds its
    /// feet uses it. The legs in <paramref name="skipLegs"/> (one bit per leg) are left as the
    /// pose has them.
    /// </summary>
    public static void ReachWithFeet(
        Skeleton skeleton,
        Span<Rotation> turns,
        ReadOnlySpan<(double X, double Y, double Z)> targets,
        ReadOnlySpan<Rotation> feet,
        Span<RigidTransform> bones,
        uint skipLegs = 0
    )
    {
        var legs = skeleton.Legs;
        skeleton.PoseLegs(turns, bones);
        for (var i = 0; i < legs.Length; i++)
        {
            if (!Skips(skipLegs, i))
            {
                Knee(skeleton, turns, bones, legs[i], new Vector3D(targets[i].X, targets[i].Y, targets[i].Z), 0);
            }
        }

        skeleton.PoseLegs(turns, bones);
        for (var i = 0; i < legs.Length; i++)
        {
            if (!Skips(skipLegs, i))
            {
                Upper(skeleton, turns, bones, legs[i], new Vector3D(targets[i].X, targets[i].Y, targets[i].Z), 0);
            }
        }

        skeleton.PoseLegs(turns, bones);
        for (var i = 0; i < legs.Length; i++)
        {
            if (!Skips(skipLegs, i))
            {
                Foot(skeleton, turns, bones, legs[i].Foot, feet[i]);
            }
        }

        skeleton.PoseLegs(turns, bones);
    }

    /// <summary>
    /// Turns the root, before its turn in the pose, to the plane through the ground under the feet (least squares over
    /// where the feet stand across the body), held within <see cref="MaxTilt"/>: nose up where the ground rises ahead,
    /// the right side up where it rises to the right.
    /// </summary>
    private static void Tilt(
        Skeleton skeleton,
        Span<Rotation> turns,
        ReadOnlySpan<LegGround> ground,
        ReadOnlySpan<RigidTransform> bones,
        uint skipLegs
    )
    {
        var legs = skeleton.Legs;
        double n = 0,
            sx = 0,
            sz = 0,
            sh = 0,
            sxx = 0,
            szz = 0,
            sxz = 0,
            sxh = 0,
            szh = 0;
        for (var i = 0; i < legs.Length; i++)
        {
            if (Skips(skipLegs, i))
            {
                continue;
            }

            n++;
            var foot = bones[legs[i].Foot];
            var h = ground[i].Height;
            sx += foot.X;
            sz += foot.Z;
            sh += h;
            sxx += foot.X * foot.X;
            szz += foot.Z * foot.Z;
            sxz += foot.X * foot.Z;
            sxh += foot.X * h;
            szh += foot.Z * h;
        }

        if (n < 3)
        {
            return;
        }

        // The slopes b (across x) and c (along z) of h = a + b x + c z, about the feet's middle.
        double mx = sx / n,
            mz = sz / n,
            mh = sh / n;
        double cxx = sxx / n - mx * mx,
            czz = szz / n - mz * mz,
            cxz = sxz / n - mx * mz,
            cxh = sxh / n - mx * mh,
            czh = szh / n - mz * mh;
        var det = cxx * czz - cxz * cxz;
        if (Math.Abs(det) < 1e-6)
        {
            return;
        }

        var b = (cxh * czz - czh * cxz) / det;
        var c = (czh * cxx - cxh * cxz) / det;
        var pitch = Math.Clamp(Math.Atan(-c) / (2 * Math.PI), -MaxTilt, MaxTilt);
        var roll = Math.Clamp(Math.Atan(b) / (2 * Math.PI), -MaxTilt, MaxTilt);
        var rest = skeleton.Rest(0);
        var tilt = Rotation.AboutX(pitch).Times(Rotation.AboutZ(roll));
        turns[0] = rest.Transposed().Times(tilt.Times(rest.Times(turns[0])));
    }

    private static bool Skips(uint skipLegs, int leg) => (skipLegs & (1u << leg)) != 0;

    private static LegGround ProbeFoot(
        VoxelCharacterMover mover,
        Skeleton skeleton,
        in LegChain leg,
        in RigidTransform foot,
        Vector3 feet,
        float yaw
    )
    {
        // The heel a little behind the ankle and the toes well ahead of it (the body faces -z), on the sole.
        var heel = GroundAt(mover, skeleton, foot, leg.Heel, leg.Sole, feet, yaw);
        if (leg.Span <= 0)
        {
            // A foot that ends in a point: the ground under it alone.
            return double.IsNaN(heel) ? default : new LegGround(heel, 0);
        }

        var toe = GroundAt(mover, skeleton, foot, leg.Toe, leg.Sole, feet, yaw);
        if (double.IsNaN(heel) || double.IsNaN(toe))
        {
            return default;
        }

        var pitch = Math.Clamp(Math.Atan2(toe - heel, leg.Span) / (2 * Math.PI), -MaxPitch, MaxPitch);
        // Tilted by the pitch, the heel drops and the toes rise about the ankle: the ankle stands as high as the higher
        // of the two needs, so on an even slope both touch.
        var sin = Math.Sin(pitch * 2 * Math.PI);
        var height = Math.Max(heel + leg.Heel * sin, toe + leg.Toe * sin);
        return new LegGround(height, pitch);
    }

    /// <summary>
    /// The ground under the point (0, <paramref name="y"/>, <paramref name="z"/>) of the foot's space, relative to the
    /// feet, in voxels: within a step above or below them, else a whole step down; NaN where the voxels are not loaded.
    /// </summary>
    private static double GroundAt(
        VoxelCharacterMover mover,
        Skeleton skeleton,
        in RigidTransform foot,
        double z,
        double y,
        Vector3 feet,
        float yaw
    )
    {
        var (bx, _, bz) = foot.Apply(0, y, z);
        return GroundUnder(mover, skeleton, bx, bz, feet, yaw);
    }

    /// <summary>
    /// The ground under the point (<paramref name="bx"/>, <paramref name="bz"/>) of the body's frame (level-0 voxels) of
    /// a body standing at <paramref name="feet"/> (metres) turned by <paramref name="yaw"/> radians, relative to the
    /// feet, in voxels: within a step of <paramref name="skeleton"/> above or below them, else a whole step down; NaN
    /// where the voxels are not loaded. Main thread (it reads the voxels).
    /// </summary>
    public static double GroundUnder(
        VoxelCharacterMover mover,
        Skeleton skeleton,
        double bx,
        double bz,
        Vector3 feet,
        float yaw
    )
    {
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        var (cos, sin) = (Math.Cos(yaw), Math.Sin(yaw));
        // Turned about y as the body is (Rotation.AboutY: +z towards +x).
        var at = new Vector3(
            feet.X + (float)((cos * bx + sin * bz) / perMetre),
            feet.Y,
            feet.Z + (float)((-sin * bx + cos * bz) / perMetre)
        );
        var step = (float)(StepVoxels(skeleton) / perMetre);
        return mover.GroundTop(at, ProbeRadiusMetres, step, step, out var top)
                ? Math.Min((top - feet.Y) * perMetre, step * perMetre)
            : float.IsNaN(top) ? double.NaN
            : -step * perMetre;
    }

    /// <summary>
    /// The highest ground under the point (<paramref name="bx"/>, <paramref name="bz"/>) of the body's frame within
    /// <paramref name="reach"/> above or below the height <paramref name="around"/> (level-0 voxels, relative to the
    /// feet), relative to the feet, in voxels: for a long body whose far parts lie well above or below its feet. The
    /// lowest height searched where there is none, and NaN where the voxels are not loaded. Main thread (it reads the
    /// voxels).
    /// </summary>
    public static double GroundNear(
        VoxelCharacterMover mover,
        double bx,
        double bz,
        double around,
        double reach,
        Vector3 feet,
        float yaw
    )
    {
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        var (cos, sin) = (Math.Cos(yaw), Math.Sin(yaw));
        var at = new Vector3(
            feet.X + (float)((cos * bx + sin * bz) / perMetre),
            feet.Y + (float)(around / perMetre),
            feet.Z + (float)((-sin * bx + cos * bz) / perMetre)
        );
        var span = (float)(reach / perMetre);
        return mover.GroundTop(at, ProbeRadiusMetres, span, span, out var top) ? (top - feet.Y) * perMetre
            : float.IsNaN(top) ? double.NaN
            : around - reach;
    }

    /// <summary>
    /// Bends the knee of <paramref name="leg"/> so its hip and ankle lie as far apart as the hip and
    /// <paramref name="target"/>: the lower bone's turn from the upper's, its rest turn and its own together.
    /// </summary>
    private static void Knee(
        Skeleton skeleton,
        Span<Rotation> turns,
        ReadOnlySpan<RigidTransform> bones,
        in LegChain leg,
        Vector3D target,
        double lift
    )
    {
        double a = leg.UpperLength,
            b = leg.LowerLength;
        var hip = Position(bones[leg.Upper], lift);
        var reach = (target - hip).Length;
        var soft = skeleton.SoftReach * (a + b);
        if (soft > 0 && reach > a + b - soft)
        {
            // Past the soft start, the reach comes ever closer to straight without getting there.
            reach = a + b - soft * Math.Exp(-(reach - (a + b - soft)) / soft);
        }

        // Never past full stretch (with a soft reach it stays short of that already), and half a voxel short of folding
        // flat, where a foot would fold into its thigh.
        reach = Math.Clamp(reach, Math.Abs(a - b) + 0.5, a + b);
        var inside = Math.Acos(Math.Clamp((a * a + b * b - reach * reach) / (2 * a * b), -1, 1));
        // A straight leg has an inside angle of half a turn; the lower bone turns (its bend's way) by what is missing,
        // its rest turn included.
        var bend = Rotation.AboutX(leg.Bend * (Math.PI - inside) / (2 * Math.PI));
        turns[leg.Lower] = skeleton.Rest(leg.Lower).Transposed().Times(bend);
    }

    /// <summary>Turns the upper bone so the ankle (as the bent knee now puts it) lands on <paramref name="target"/>.</summary>
    private static void Upper(
        Skeleton skeleton,
        Span<Rotation> turns,
        ReadOnlySpan<RigidTransform> bones,
        in LegChain leg,
        Vector3D target,
        double lift
    )
    {
        var hip = Position(bones[leg.Upper], lift);
        var ankle = Position(bones[leg.Foot], lift);
        var turn = Between(ankle - hip, target - hip);
        // A bone's turn in the body is its parent's, then its rest turn, then its own: undo the first two.
        var above = bones[skeleton.Parent(leg.Upper)].Rotation.Times(skeleton.Rest(leg.Upper));
        turns[leg.Upper] = above.Transposed().Times(turn.Times(bones[leg.Upper].Rotation));
    }

    /// <summary>Turns the foot so it lies as <paramref name="inBody"/> (its turn in the body's frame) says.</summary>
    private static void Foot(
        Skeleton skeleton,
        Span<Rotation> turns,
        ReadOnlySpan<RigidTransform> bones,
        int foot,
        in Rotation inBody
    )
    {
        var above = bones[skeleton.Parent(foot)].Rotation.Times(skeleton.Rest(foot));
        turns[foot] = above.Transposed().Times(inBody);
    }

    /// <summary>The smallest turn taking the direction of <paramref name="from"/> onto that of <paramref name="to"/>.</summary>
    private static Rotation Between(Vector3D from, Vector3D to)
    {
        var lengths = from.Length * to.Length;
        if (lengths < 1e-9)
        {
            return Rotation.Identity;
        }

        var axis = Vector3D.Cross(from, to);
        var sin = axis.Length / lengths;
        var cos = Vector3D.Dot(from, to) / lengths;
        if (sin < 1e-9)
        {
            return Rotation.Identity;
        }

        var (x, y, z) = (axis.X / (sin * lengths), axis.Y / (sin * lengths), axis.Z / (sin * lengths));
        var t = 1 - cos;
        return new Rotation(
            cos + t * x * x,
            t * x * y - sin * z,
            t * x * z + sin * y,
            t * x * y + sin * z,
            cos + t * y * y,
            t * y * z - sin * x,
            t * x * z - sin * y,
            t * y * z + sin * x,
            cos + t * z * z
        );
    }

    private static Vector3D Position(in RigidTransform bone, double lift) => new(bone.X, bone.Y + lift, bone.Z);

    /// <summary>A point or direction in doubles.</summary>
    private readonly record struct Vector3D(double X, double Y, double Z)
    {
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vector3D operator -(Vector3D a, Vector3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static double Dot(Vector3D a, Vector3D b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector3D Cross(Vector3D a, Vector3D b) =>
            new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }
}
