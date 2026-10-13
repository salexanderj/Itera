using System.Numerics;
using Itera.Engine.Rendering;

namespace Itera.Engine.Components;

/// <summary>
/// Where a camera is and where it looks, in world metres and radians. Yaw turns about +Y: 0 looks along -Z and a
/// positive yaw turns left. Pitch tilts the view: positive looks up.
/// </summary>
public struct CameraPose
{
    public Vector3 Position;
    public float Yaw;
    public float Pitch;
}

/// <summary>
/// A kick of the local player's view, in radians (a weapon's recoil): <c>PlayerCameraSystem</c> adds it to the look the
/// camera sits behind and points along, so the whole picture moves with it and the screen's middle stays where a shot
/// that aims through the camera goes. The game sets it each frame; the command's own look is left as it was.
/// Presentation only: what aims through the camera adds the same kick to the command's look itself.
/// </summary>
public struct LookKick
{
    /// <summary>Radians up.</summary>
    public float Pitch;

    /// <summary>Radians to the left.</summary>
    public float Yaw;
}

/// <summary>
/// How a camera projects (<see cref="CameraView"/> has each value's meaning): perspective, by its vertical field of view
/// from its near plane on; or orthographic, an image <see cref="HeightMetres"/> high round the camera's position, between
/// <see cref="NearMetres"/> and <see cref="FarMetres"/> along the view (<see cref="Orthographic"/> makes one).
/// </summary>
public struct CameraLens
{
    public float VerticalFovRadians;
    public float NearMetres;
    public CameraProjection Projection;
    public float HeightMetres;
    public float FarMetres;

    /// <summary>
    /// How many times closer a perspective lens draws than its field of view (aiming down sights): 2 halves the field's
    /// tangent; 0 or 1, none. It applies on top of the player's field of view (<c>CameraExtractSystem</c>).
    /// </summary>
    public float Zoom;

    /// <summary>
    /// An orthographic lens: an image <paramref name="heightMetres"/> high, round the camera's position, from
    /// <paramref name="nearMetres"/> to <paramref name="farMetres"/> along the view (negative: behind the position; NaN
    /// for the view distance behind it, and past it).
    /// </summary>
    public static CameraLens Orthographic(float heightMetres, float nearMetres, float farMetres) =>
        new()
        {
            Projection = CameraProjection.Orthographic,
            HeightMetres = heightMetres,
            NearMetres = nearMetres,
            FarMetres = farMetres,
        };
}

/// <summary>A camera the debug fly-camera system moves, with its current flying speed.</summary>
public struct FlyCamera
{
    public float SpeedMetresPerSecond;
}

/// <summary>Marks the camera the frame is drawn from. Exactly one entity has it.</summary>
public struct ActiveCamera;
