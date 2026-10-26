using System.Numerics;
using Arch.Core;
using Itera.Engine.Assets;
using Itera.Engine.Components;

namespace Itera.Engine.Systems;

/// <summary>
/// The smoothed changes of the posed meshes with a <see cref="SmoothedPose"/> (dead blending, in place of a cross-fade):
/// <see cref="MeshDrawSystem"/> keeps each one's last two drawn poses here, and when its animator marks a change
/// (<see cref="MeshAnimator.Changes"/>) each joint's last drawn turn and place become a source that goes on moving at the
/// speed it had, that speed fading by half every quarter of the change's time; the pose is drawn blended from that source
/// into the new clip's, eased in and out over the change's time (<see cref="SmoothedPose.Seconds"/>). So a change starts
/// where the last pose was and as it was moving, and the new clip comes in smoothly however fast it moves; a change during
/// another starts from what was drawn then, as it was moving. Drawn only; a pose posed for hit shapes never comes here. At
/// most <see cref="MostPoses"/> meshes at once; one past them is drawn as sampled. Each slot keeps the entity it is for, so
/// one whose entity is gone and was taken by another starts afresh; a slot neither drawn nor held in a frame is let go at
/// its end. A mesh no list would draw this frame is held (<see cref="Hold"/>): its slot stays, a change marked meanwhile
/// starts from the pose last drawn, and a change under way goes on, so it is drawn again as far through the change as
/// had it been drawn throughout, starting still. Main thread; allocates a slot's storage at its first use alone.
/// </summary>
public sealed class PoseSmoothing
{
    /// <summary>The most meshes smoothed at once.</summary>
    public const int MostPoses = 256;

    /// <summary>The fastest a source joint goes on turning, in radians a second, and moving, in metres a second.</summary>
    public const float MostSpin = 40,
        MostDrift = 20;

    private readonly Pose?[] _poses = new Pose?[MostPoses];

    /// <summary>How many slots are taken.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// <paramref name="locals"/>, the pose <paramref name="entity"/>'s clip gives this frame, smoothed in place over a
    /// frame of <paramref name="seconds"/>, and kept as its last drawn pose for frame <paramref name="frame"/>: a new mark
    /// (<paramref name="changes"/>) starts a change of <paramref name="changeSeconds"/> from the pose drawn before. Returns
    /// the slot kept for it (<paramref name="slot"/> when it is still the entity's), or -1 when every slot is taken.
    /// </summary>
    public int Smooth(
        Entity entity,
        int slot,
        uint changes,
        float changeSeconds,
        Span<JointTransform> locals,
        float seconds,
        ulong frame
    )
    {
        var step = float.IsFinite(seconds) ? MathF.Max(seconds, 0) : 0;
        var pose = Of(entity, slot);
        if (pose is null)
        {
            slot = Take(entity, locals.Length);
            if (slot < 0)
            {
                return -1;
            }

            Start(_poses[slot]!, entity, changes, locals, frame);
            return slot;
        }

        if (pose.Joints != locals.Length)
        {
            // Another rig: its first pose again, in storage large enough.
            if (pose.Drawn.Length < locals.Length)
            {
                pose = _poses[slot] = new Pose(locals.Length) { Live = true, Owner = entity };
            }

            Start(pose, entity, changes, locals, frame);
            return slot;
        }

        var returning = pose.Held;
        pose.Held = false;
        if (pose.Changes != changes)
        {
            Mark(pose);
            pose.Changes = changes;
        }

        if (pose.Blending)
        {
            Blend(pose, changeSeconds > 0 ? changeSeconds : SmoothedPose.DefaultSeconds, step, locals);
        }

        if (returning)
        {
            // Drawn again after it was held: the poses drawn before are too old to give a speed, so it starts still.
            locals.CopyTo(pose.Drawn.AsSpan(0, pose.Joints));
            Keep(pose, locals, 0, frame);
            return slot;
        }

        Keep(pose, locals, step, frame);
        return slot;
    }

    /// <summary>
    /// Keeps <paramref name="entity"/>'s slot through frame <paramref name="frame"/> without a pose, when no list would
    /// draw its mesh this frame: a new mark (<paramref name="changes"/>) starts a change from the pose last drawn, and a
    /// change under way goes on over the frame's <paramref name="seconds"/> as if drawn (its time
    /// <paramref name="changeSeconds"/>), so it is as far on when the mesh is drawn again. Nothing for an entity without a
    /// slot.
    /// </summary>
    public void Hold(Entity entity, int slot, uint changes, float changeSeconds, float seconds, ulong frame)
    {
        var pose = Of(entity, slot);
        if (pose is null)
        {
            return;
        }

        if (pose.Changes != changes)
        {
            Mark(pose);
            pose.Changes = changes;
        }

        if (pose.Blending)
        {
            var step = float.IsFinite(seconds) ? MathF.Max(seconds, 0) : 0;
            Advance(pose, changeSeconds > 0 ? changeSeconds : SmoothedPose.DefaultSeconds, step);
        }

        pose.Held = true;
        pose.Frame = frame;
    }

    /// <summary>
    /// Writes into <paramref name="locals"/> the pose kept for <paramref name="entity"/> in frame <paramref name="frame"/>
    /// (a mesh held at its joint drawn where it is drawn): false when none was kept for that frame.
    /// </summary>
    public bool TryDrawn(Entity entity, int slot, ulong frame, Span<JointTransform> locals)
    {
        var pose = Of(entity, slot);
        if (pose is null || pose.Held || pose.Frame != frame || pose.Joints != locals.Length)
        {
            return false;
        }

        pose.Drawn.AsSpan(0, pose.Joints).CopyTo(locals);
        return true;
    }

    /// <summary>
    /// Lets go every slot neither drawn nor held in frame <paramref name="frame"/>: its mesh is gone or no longer smoothed.
    /// </summary>
    public void Sweep(ulong frame)
    {
        for (var slot = 0; slot < _poses.Length; slot++)
        {
            if (_poses[slot] is { Live: true } pose && pose.Frame != frame)
            {
                pose.Live = false;
                pose.Owner = Entity.Null;
                Count--;
            }
        }
    }

    /// <summary>The slot's pose when it is <paramref name="entity"/>'s, else null.</summary>
    private Pose? Of(Entity entity, int slot) =>
        (uint)slot < (uint)_poses.Length && _poses[slot] is { Live: true } pose && pose.Owner == entity ? pose : null;

    /// <summary>A free slot for <paramref name="entity"/>, its storage made for <paramref name="joints"/>; -1 for none.</summary>
    private int Take(Entity entity, int joints)
    {
        for (var slot = 0; slot < _poses.Length; slot++)
        {
            var pose = _poses[slot];
            if (pose is { Live: true })
            {
                continue;
            }

            if (pose is null || pose.Drawn.Length < joints)
            {
                pose = _poses[slot] = new Pose(joints);
            }

            pose.Live = true;
            pose.Held = false;
            pose.Owner = entity;
            Count++;
            return slot;
        }

        return -1;
    }

    /// <summary>A slot's first pose: drawn as sampled, still, with nothing to blend from.</summary>
    private static void Start(Pose pose, Entity entity, uint changes, ReadOnlySpan<JointTransform> locals, ulong frame)
    {
        pose.Owner = entity;
        pose.Joints = locals.Length;
        pose.Changes = changes;
        pose.Blending = false;
        pose.Held = false;
        locals.CopyTo(pose.Drawn.AsSpan(0, pose.Joints));
        Keep(pose, locals, 0, frame);
    }

    /// <summary>
    /// A change: each joint's last drawn turn and place become the source, going on at the speed it had between the last
    /// two frames drawn.
    /// </summary>
    private static void Mark(Pose pose)
    {
        var seconds = pose.Seconds;
        for (var j = 0; j < pose.Joints; j++)
        {
            var drawn = pose.Drawn[j];
            var before = pose.Previous[j];
            pose.Source[j] = drawn;
            pose.Spin[j] =
                seconds > 0
                    ? Limit(Log(drawn.Rotation * Quaternion.Conjugate(before.Rotation)) / seconds, MostSpin)
                    : Vector3.Zero;
            pose.Drift[j] =
                seconds > 0 ? Limit((drawn.Translation - before.Translation) / seconds, MostDrift) : Vector3.Zero;
        }

        pose.Clock = 0;
        pose.Blending = true;
    }

    /// <summary>
    /// The source moved on over <paramref name="seconds"/>, its speed fading with a half-life of a quarter of
    /// <paramref name="changeSeconds"/>, and <paramref name="locals"/> drawn blended from it into the clip's pose, eased
    /// in and out over <paramref name="changeSeconds"/>.
    /// </summary>
    private static void Blend(Pose pose, float changeSeconds, float seconds, Span<JointTransform> locals)
    {
        var weight = Advance(pose, changeSeconds, seconds);
        for (var j = 0; j < pose.Joints; j++)
        {
            var source = pose.Source[j];
            var target = locals[j];
            locals[j] = target with
            {
                Rotation = Quaternion.Slerp(source.Rotation, target.Rotation, weight),
                Translation = Vector3.Lerp(source.Translation, target.Translation, weight),
            };
        }
    }

    /// <summary>
    /// A change's clock and source moved on over <paramref name="seconds"/>, the source's speed fading with a half-life of
    /// a quarter of <paramref name="changeSeconds"/>; returns the new clip's weight, eased in and out.
    /// </summary>
    private static float Advance(Pose pose, float changeSeconds, float seconds)
    {
        pose.Clock += seconds;
        var share = Math.Clamp(pose.Clock / changeSeconds, 0, 1);
        var fade = MathF.Exp(-MathF.Log(2) * seconds / (changeSeconds / 4));
        for (var j = 0; j < pose.Joints; j++)
        {
            pose.Spin[j] *= fade;
            pose.Drift[j] *= fade;
            var source = pose.Source[j];
            pose.Source[j] = source with
            {
                Rotation = Quaternion.Normalize(Exp(pose.Spin[j] * seconds) * source.Rotation),
                Translation = source.Translation + pose.Drift[j] * seconds,
            };
        }

        pose.Blending = share < 1;
        return share * share * (3 - 2 * share);
    }

    /// <summary>The pose drawn this frame, kept with the one before it and the frame's length.</summary>
    private static void Keep(Pose pose, ReadOnlySpan<JointTransform> locals, float seconds, ulong frame)
    {
        pose.Drawn.AsSpan(0, pose.Joints).CopyTo(pose.Previous);
        locals.CopyTo(pose.Drawn.AsSpan(0, pose.Joints));
        pose.Seconds = seconds;
        pose.Frame = frame;
    }

    private static Vector3 Limit(Vector3 value, float most)
    {
        var length = value.Length();
        return !float.IsFinite(length) ? Vector3.Zero
            : length > most ? value * (most / length)
            : value;
    }

    /// <summary>A turn as its axis times its angle in radians, the shorter way round.</summary>
    private static Vector3 Log(Quaternion q)
    {
        if (q.W < 0)
        {
            q = -q;
        }

        var axis = new Vector3(q.X, q.Y, q.Z);
        var sine = axis.Length();
        if (!(sine > 1e-8f) || !float.IsFinite(sine))
        {
            return axis * 2;
        }

        return axis * (2 * MathF.Atan2(sine, q.W) / sine);
    }

    /// <summary>The turn of an axis times its angle in radians.</summary>
    private static Quaternion Exp(Vector3 turn)
    {
        var angle = turn.Length();
        if (!(angle > 1e-8f) || !float.IsFinite(angle))
        {
            return Quaternion.Normalize(new Quaternion(turn * 0.5f, 1));
        }

        var (sine, cosine) = MathF.SinCos(angle / 2);
        return new Quaternion(turn * (sine / angle), cosine);
    }

    /// <summary>One mesh's last two drawn poses and the source of a change, a joint each.</summary>
    private sealed class Pose(int joints)
    {
        public Entity Owner = Entity.Null;
        public bool Live;
        public bool Blending;

        /// <summary>True while its mesh is not drawn (<see cref="Hold"/>): its drawn poses are those of before.</summary>
        public bool Held;
        public int Joints;
        public uint Changes;
        public ulong Frame;

        /// <summary>The last frame's length, in seconds.</summary>
        public float Seconds;

        /// <summary>How long the change has gone on, in seconds.</summary>
        public float Clock;

        public readonly JointTransform[] Drawn = new JointTransform[joints];
        public readonly JointTransform[] Previous = new JointTransform[joints];
        public readonly JointTransform[] Source = new JointTransform[joints];
        public readonly Vector3[] Spin = new Vector3[joints];
        public readonly Vector3[] Drift = new Vector3[joints];
    }
}
