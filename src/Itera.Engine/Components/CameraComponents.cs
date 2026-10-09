using System.Numerics;

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

/// <summary>How a camera projects: vertical field of view and near plane.</summary>
public struct CameraLens
{
    public float VerticalFovRadians;
    public float NearMetres;
}

/// <summary>A camera the debug fly-camera system moves, with its current flying speed.</summary>
public struct FlyCamera
{
    public float SpeedMetresPerSecond;
}

/// <summary>Marks the camera the frame is drawn from. Exactly one entity has it.</summary>
public struct ActiveCamera;
