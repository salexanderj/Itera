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
