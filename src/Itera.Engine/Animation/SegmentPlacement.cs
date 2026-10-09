using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Physics;

namespace Itera.Engine.Animation;

/// <summary>
/// Lays a legless body along the voxel ground (its segments; <see cref="Skeleton.Drape"/>): its root's centre
/// line stands its radius above the ground under it, and each draped bone, parents first, turns about its own x so that
/// its far end's centre line stands its radius above the ground there. Where the level-0 voxels are not loaded the
/// ground is taken as level with the feet. Turns are added after the pose's (the gait's side to side, a look), so a body
/// crossing a step or a slope lies along it. Level-0 voxels in the body's frame; allocates nothing.
/// </summary>
public static class SegmentPlacement
{
    /// <summary>The most a bone turns up or down to follow the ground, in turns: 30 degrees.</summary>
    public const double MaxPitch = 30.0 / 360;

    /// <summary>
    /// Lays the draped bones of the pose in <paramref name="turns"/> (changed in place) on the ground under a body at
    /// <paramref name="feet"/> turned by <paramref name="yaw"/>, less so by <paramref name="weight"/> below 1, and returns
    /// the skeleton's lift, for a body moved <paramref name="sway"/> level-0 voxels to its right of its feet.
    /// <paramref name="bones"/> (one per bone) is scratch, left holding every bone's transform, raised by nothing and
    /// swayed by nothing. Draped bones in <paramref name="skipBones"/> (a rearing neck and head) are left as the
    /// pose has them.
    /// </summary>
    public static double Apply(
        VoxelCharacterMover mover,
        Skeleton skeleton,
        Span<Rotation> turns,
        Span<RigidTransform> bones,
        Vector3 feet,
        float yaw,
        double weight,
        double sway = 0,
        uint skipBones = 0
    )
    {
        var root = Ground(mover, sway, 0, 0, skeleton.StepVoxels, feet, yaw) * weight;
        skeleton.Pose(turns, bones);
        foreach (var drape in skeleton.Drape)
        {
            var bone = drape.Bone;
            if ((skipBones & (1u << bone)) != 0)
            {
                continue;
            }

            var parent = skeleton.Parent(bone);
            var (x, y, z) = skeleton.Offset(bone);
            var local = new RigidTransform(skeleton.Rest(bone).Times(turns[bone]), x, y, z);
            var placed = bone == 0 ? local : bones[parent].Times(local);

            // The far end as it lies now, and where the ground puts it (in the frame the lift raises).
            var (tx, ty, tz) = placed.Apply(0, 0, drape.Length);
            var (jx, jy, jz) = (placed.X, placed.Y, placed.Z);
            // The ground within the steepest the bone may lie of its joint's height, above the ground there.
            var reach = Math.Abs(drape.Length) * 0.6 + drape.TipRadius + skeleton.StepVoxels;
            var around = jy + root - drape.TipRadius;
            var wanted = Ground(mover, tx + sway, tz, around, reach, feet, yaw) * weight + drape.TipRadius - root;
            var across = Math.Sqrt((tx - jx) * (tx - jx) + (tz - jz) * (tz - jz));
            var now = Math.Atan2(ty - jy, across);
            var then = Math.Atan2(wanted - jy, across);
            var pitch = Math.Clamp((then - now) / (2 * Math.PI), -MaxPitch, MaxPitch);

            // A turn about x lowers a far end behind the joint (+z) and raises one ahead of it.
            turns[bone] = turns[bone].Times(Rotation.AboutX(drape.Length > 0 ? -pitch : pitch));
            bones[bone] =
                bone == 0
                    ? new RigidTransform(skeleton.Rest(bone).Times(turns[bone]), x, y, z)
                    : bones[parent].Times(new RigidTransform(skeleton.Rest(bone).Times(turns[bone]), x, y, z));
        }

        skeleton.Pose(turns, bones);
        return root;
    }

    /// <summary>The ground under (x, z) of the body's frame, searched about a height; level with the feet where unknown.</summary>
    private static double Ground(
        VoxelCharacterMover mover,
        double x,
        double z,
        double around,
        double reach,
        Vector3 feet,
        float yaw
    )
    {
        var ground = FootPlacement.GroundNear(mover, x, z, around, reach, feet, yaw);
        return double.IsNaN(ground) ? 0 : ground;
    }
}
