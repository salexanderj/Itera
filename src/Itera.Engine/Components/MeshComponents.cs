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
/// Plays a clip of a skinned mesh asset's rig on a <see cref="DrawnMesh"/> (<c>Rig.Clips</c>, its index from
/// <c>Rig.FindClip</c>; -1 for the rest pose): the clip's time at the last two fixed steps, which the frame blends by its
/// <c>Alpha</c>, advanced by <c>MeshAnimatorSystem</c> at <see cref="Speed"/> times the step (0 holds it). A looping clip
/// wraps both times together at its end; one played <see cref="Once"/> holds its last pose. A change of clip may fade
/// (<see cref="FadeTo"/>): the clip it played goes on at its own speed, and each joint blends from it to the new one over
/// <see cref="FadeSeconds"/> of the match's time. Drawing only: the pose is never the same on every machine, so nothing
/// peers agree on reads it.
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

    /// <summary>
    /// Plays <paramref name="clip"/> from <paramref name="seconds"/> at <paramref name="speed"/>, faded in over
    /// <paramref name="fadeSeconds"/> from the clip it plays now, which goes on meanwhile (a fade already under way starts
    /// again from the clip it was fading to). A fade of 0 changes at once.
    /// </summary>
    public void FadeTo(int clip, float fadeSeconds, float seconds = 0, float speed = 1, bool once = false)
    {
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
