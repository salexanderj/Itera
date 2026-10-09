using System.Numerics;
using Itera.Engine.Maths;

namespace Itera.Engine.Animation;

/// <summary>
/// Looking at a point: the turn and tilt from a body's facing to the point, held within <see cref="MaxYaw"/> and
/// <see cref="MaxPitch"/>, spread over the bones of its skeleton's look (<see cref="Skeleton.Look"/>: for a humanoid, the
/// spine, chest, neck, and head, so the whole upper body takes part and the head the most). Allocates nothing.
/// </summary>
public static class Aim
{
    /// <summary>How far to either side a body looks at most, in radians: 75 degrees.</summary>
    public const float MaxYaw = 75 * MathF.PI / 180;

    /// <summary>
    /// Beyond this turn, in radians (120 degrees), a body looks ahead instead: a point behind it would swing the
    /// head from one limit to the other as it crossed the back.
    /// </summary>
    public const float BehindYaw = 120 * MathF.PI / 180;

    /// <summary>How far up or down a body looks at most, in radians: 50 degrees.</summary>
    public const float MaxPitch = 50 * MathF.PI / 180;

    /// <summary>The height of the eyes above the feet for a body of <paramref name="skeleton"/>, in metres.</summary>
    public static float EyeMetres(Skeleton skeleton) => (float)skeleton.EyeMetres;

    /// <summary>
    /// The turn (left positive) and tilt (up positive) in radians from the facing <paramref name="yaw"/> of eyes at
    /// <paramref name="eye"/> to <paramref name="target"/>, each held within its limit; none for a point more than
    /// <see cref="BehindYaw"/> to the side.
    /// </summary>
    public static (float Yaw, float Pitch) Angles(Vector3 eye, float yaw, Vector3 target)
    {
        var to = target - eye;
        var across = new Vector2(to.X, to.Z).Length();
        if (across < 1e-4f && MathF.Abs(to.Y) < 1e-4f)
        {
            return (0, 0);
        }

        // Forward at yaw y is (-sin y, 0, -cos y).
        var turn = MathF.Atan2(-to.X, -to.Z) - yaw;
        turn = MathF.IEEERemainder(turn, 2 * MathF.PI);
        if (MathF.Abs(turn) > BehindYaw)
        {
            return (0, 0);
        }

        var pitch = MathF.Atan2(to.Y, across);
        return (Math.Clamp(turn, -MaxYaw, MaxYaw), Math.Clamp(pitch, -MaxPitch, MaxPitch));
    }

    /// <summary>
    /// Adds a look of <paramref name="yaw"/> and <paramref name="pitch"/> radians to the pose in
    /// <paramref name="turns"/>: each bone of <paramref name="skeleton"/>'s look turns by its share, before its turn in the
    /// pose.
    /// </summary>
    public static void Apply(Skeleton skeleton, Span<Rotation> turns, double yaw, double pitch)
    {
        if (Math.Abs(yaw) < 1e-6 && Math.Abs(pitch) < 1e-6)
        {
            return;
        }

        foreach (var (bone, yawShare, pitchShare) in skeleton.Look)
        {
            // Turns about y look left, and about x (for a bone pointing up) tilt the face up.
            var look = Rotation
                .AboutY(yaw * yawShare / (2 * Math.PI))
                .Times(Rotation.AboutX(pitch * pitchShare / (2 * Math.PI)));
            turns[bone] = look.Times(turns[bone]);
        }
    }
}
