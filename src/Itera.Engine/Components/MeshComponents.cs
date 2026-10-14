using System.Numerics;
using Itera.Engine.Rendering;

namespace Itera.Engine.Components;

/// <summary>
/// Where a mesh asset is drawn (<see cref="DrawnMesh"/>): its pose at the last two fixed steps, which the frame blends by
/// its <c>Alpha</c> (<c>MeshDrawSystem</c>), and its uniform scale. A fixed system that moves it copies
/// <see cref="Position"/> and <see cref="Orientation"/> into the previous ones first; a placement that never moves keeps
/// them equal (<see cref="At"/>).
/// </summary>
public struct MeshPlacement
{
    public Vector3 Position;
    public Vector3 PreviousPosition;
    public Quaternion Orientation;
    public Quaternion PreviousOrientation;
    public float Scale;

    /// <summary>A placement that has not moved: at <paramref name="position"/>, turned by <paramref name="orientation"/>.</summary>
    public static MeshPlacement At(Vector3 position, Quaternion orientation, float scale = 1) =>
        new()
        {
            Position = position,
            PreviousPosition = position,
            Orientation = orientation,
            PreviousOrientation = orientation,
            Scale = scale,
        };
}

/// <summary>
/// The mesh asset an entity draws where its <see cref="MeshPlacement"/> is: the asset's index in <c>MeshAssets</c> (from
/// its <c>Find</c>, looked up once), a tint its colours are multiplied by (linear RGBA bytes, red in the low byte;
/// <see cref="MeshInstances.NoTint"/> for none), and how it meets the camera and the shadows.
/// </summary>
public struct DrawnMesh
{
    public int Asset;
    public uint Tint;
    public ModelShadow Shadow;

    public static DrawnMesh Of(int asset, uint tint = MeshInstances.NoTint, ModelShadow shadow = ModelShadow.Casts) =>
        new()
        {
            Asset = asset,
            Tint = tint,
            Shadow = shadow,
        };
}

/// <summary>A mesh the console's <c>meshes place</c> or <c>meshes test</c> made, which <c>meshes clear</c> removes.</summary>
public struct ConsoleMesh;

/// <summary>
/// A posed mesh whose pose the game's <c>IMeshPoseShaper</c> shapes before it is drawn (an upper body's own clip, a spine
/// bent to an aim, a hand on a rifle), after its <see cref="MeshAnimator"/>'s clip is sampled.
/// </summary>
public struct ShapedPose;

/// <summary>
/// A posed mesh drawn with its changes smoothed: each change its <see cref="MeshAnimator"/> marks
/// (<see cref="MeshAnimator.Changes"/>: a new clip, a fade, another clip blended in, or a game's own mark) starts from the
/// pose drawn the frame before, moving as it was, and blends into the new one over <see cref="Seconds"/>, its joints'
/// turns and places alike, as it is drawn, after the game's shaping (<c>PoseSmoothing</c>: dead blending, in place of a
/// cross-fade). Drawn only, from each game's own frames: a
/// pose posed for hit shapes is never smoothed. The draw system keeps the slot of its last poses (<see cref="Slot"/>).
/// </summary>
public struct SmoothedPose
{
    /// <summary>How long a change takes to blend into the new clip, in seconds; 0 or less: <see cref="DefaultSeconds"/>.</summary>
    public float Seconds;

    /// <summary>Where the draw system keeps this mesh's last poses; it checks the slot is still this entity's.</summary>
    public int Slot;

    /// <summary>How long a change takes unless another time is given, in seconds.</summary>
    public const float DefaultSeconds = 0.2f;

    /// <summary>Smoothing whose changes take <paramref name="seconds"/>.</summary>
    public static SmoothedPose Of(float seconds = DefaultSeconds) => new() { Seconds = seconds, Slot = -1 };
}

/// <summary>
/// A mesh drawn at a joint of another entity's posed mesh (a rifle in a hand), with the attached entity's
/// <see cref="DrawnMesh"/> and no placement of its own: <see cref="Holder"/>'s joint <see cref="Joint"/> (from
/// <c>Rig.FindJoint</c>) as its pose is drawn this frame, shaped included, then <see cref="Offset"/> and
/// <see cref="Turn"/> in the joint's space (the grip's place and turn in the hand). A holder that is gone or not drawn
/// draws nothing; a joint the holder's rig does not have draws it at the holder's placement.
/// </summary>
public struct MeshAttachment
{
    public Arch.Core.Entity Holder;
    public int Joint;
    public Vector3 Offset;
    public Quaternion Turn;

    public static MeshAttachment To(Arch.Core.Entity holder, int joint, Vector3 offset, Quaternion turn) =>
        new()
        {
            Holder = holder,
            Joint = joint,
            Offset = offset,
            Turn = turn,
        };
}

/// <summary>
/// Plays a clip of a skinned mesh asset's rig on a <see cref="DrawnMesh"/> (<c>Rig.Clips</c>, its index from
/// <c>Rig.FindClip</c>; -1 for the rest pose): the clip's time at the last two fixed steps, which the frame blends by its
/// <c>Alpha</c>, advanced by <c>MeshAnimatorSystem</c> at <see cref="Speed"/> times the step (0 holds it). A looping clip
/// wraps both times together at its end; one played <see cref="Once"/> holds its last pose. A change of clip may fade
/// (<see cref="FadeTo"/>): the clip it played goes on at its own speed, and each joint blends from it to the new one over
/// <see cref="FadeSeconds"/> of the match's time. A second clip may be blended in (<see cref="SetBlend"/>): posed at the
/// same share of its length as the clip is of its own (two walks in step), each joint moved towards it by
/// <see cref="BlendWeight"/> (directional movement: a forward clip and a sideways one). Each change of clip and of the
/// clip blended in counts in <see cref="Changes"/>, which a <see cref="SmoothedPose"/> smooths. Drawing only: the pose is
/// never the same on every machine, so nothing peers agree on reads it.
/// </summary>
public struct MeshAnimator
{
    public int Clip;
    public float Seconds;
    public float PreviousSeconds;
    public float Speed;
    public bool Once;

    /// <summary>The clip it fades from while <see cref="FadeSeconds"/> is more than 0, and that clip's time and speed.</summary>
    public int FromClip;

    public float FromSeconds;
    public float FromPreviousSeconds;
    public float FromSpeed;
    public bool FromOnce;

    /// <summary>The fade's length in seconds; 0 when it fades from nothing.</summary>
    public float FadeSeconds;

    /// <summary>How far the fade is at the last two steps, in seconds.</summary>
    public float Faded;

    public float PreviousFaded;

    /// <summary>The clip blended in while <see cref="BlendWeight"/> is more than 0 (-1 for the rest pose).</summary>
    public int BlendClip;

    /// <summary>How far each joint moves towards <see cref="BlendClip"/>'s pose at the last two steps, 0 to 1.</summary>
    public float BlendWeight;

    public float PreviousBlendWeight;

    /// <summary>
    /// How much further through its length than the clip the blended clip is posed, a share of its length (0: the same
    /// share): clips whose steps fall at different shares kept in step.
    /// </summary>
    public float BlendShift;

    /// <summary>
    /// How many times the pose has changed other than by playing on: each <see cref="FadeTo"/>, each
    /// <see cref="SetBlend"/> that blends in another clip, and each <see cref="MarkChange"/>; it wraps round. A
    /// <see cref="SmoothedPose"/> smooths each.
    /// </summary>
    public uint Changes;

    /// <summary>
    /// Blends <paramref name="clip"/> in by <paramref name="weight"/> from this step (0 for none), the weight before kept
    /// for the frames between (none to blend from when the clip changes), posed <paramref name="shift"/> of its length
    /// further through it than the clip is through its own.
    /// </summary>
    public void SetBlend(int clip, float weight, float shift = 0)
    {
        BlendShift = float.IsFinite(shift) ? shift - MathF.Floor(shift) : 0;
        var clamped = float.IsFinite(weight) ? Math.Clamp(weight, 0, 1) : 0;
        if (clip != BlendClip && (clamped > 0 || BlendWeight > 0))
        {
            Changes = unchecked(Changes + 1);
        }

        PreviousBlendWeight = clip == BlendClip ? BlendWeight : clamped;
        BlendClip = clip;
        BlendWeight = clamped;
    }

    /// <summary>How far a frame moves each joint towards the blended clip.</summary>
    public readonly float BlendShare(float alpha) =>
        Math.Clamp(PreviousBlendWeight + (BlendWeight - PreviousBlendWeight) * alpha, 0, 1);

    /// <summary>
    /// Plays <paramref name="clip"/> from <paramref name="seconds"/> at <paramref name="speed"/>, faded in over
    /// <paramref name="fadeSeconds"/> from the clip it plays now, which goes on meanwhile (a fade already under way starts
    /// again from the clip it was fading to). A fade of 0 changes at once.
    /// </summary>
    public void FadeTo(int clip, float fadeSeconds, float seconds = 0, float speed = 1, bool once = false)
    {
        Changes = unchecked(Changes + 1);
        if (fadeSeconds > 0)
        {
            FromClip = Clip;
            FromSeconds = Seconds;
            FromPreviousSeconds = PreviousSeconds;
            FromSpeed = Speed;
            FromOnce = Once;
            Faded = 0;
            PreviousFaded = 0;
        }

        FadeSeconds = fadeSeconds > 0 ? fadeSeconds : 0;
        Clip = clip;
        Seconds = seconds;
        PreviousSeconds = seconds;
        Speed = speed;
        Once = once;
    }

    /// <summary>
    /// Marks a change of the pose other than by the clips (a game's own layer on it switched at once, such as what the
    /// hands hold), which a <see cref="SmoothedPose"/> smooths as a change of clip.
    /// </summary>
    public void MarkChange() => Changes = unchecked(Changes + 1);

    /// <summary>How much of the new clip a frame draws: 1 when it fades from nothing.</summary>
    public readonly float FadeWeight(float alpha) =>
        FadeSeconds > 0 ? Math.Clamp((PreviousFaded + (Faded - PreviousFaded) * alpha) / FadeSeconds, 0, 1) : 1;

    /// <summary>Clip <paramref name="clip"/> from <paramref name="seconds"/>, at <paramref name="speed"/>, looping.</summary>
    public static MeshAnimator Playing(int clip, float seconds = 0, float speed = 1) =>
        new()
        {
            Clip = clip,
            Seconds = seconds,
            PreviousSeconds = seconds,
            Speed = speed,
        };
}
