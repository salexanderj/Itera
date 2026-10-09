using Arch.Core;
using Itera.Engine.Components;

namespace Itera.Engine.Systems;

/// <summary>
/// The cameras' shared numbers: how far the look pitches, how far the mouse turns it, and the near plane; and the
/// match's one camera entity, which <c>PlayerCameraSystem</c> makes.
/// </summary>
public static class CameraRules
{
    /// <summary>The look's pitch stops short of straight up and down, where yaw would lose its meaning.</summary>
    public const float MaxPitch = 89 * MathF.PI / 180;

    /// <summary>The camera's near plane, in metres.</summary>
    public const float NearMetres = 0.05f;

    /// <summary>How far one pixel of mouse movement turns the look, in radians, at the options' sensitivity.</summary>
    public static float RadiansPerPixel(EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return (float)(options.Camera.MouseSensitivity * Math.PI / 180);
    }

    /// <summary>
    /// Makes the match's camera at <paramref name="pose"/>: the active camera, with the options' field of view and the
    /// fly camera's starting speed.
    /// </summary>
    public static Entity CreateCamera(World world, in CameraPose pose, EngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(options);
        return world.Create(
            pose,
            new CameraLens
            {
                VerticalFovRadians = (float)options.Camera.VerticalFieldOfViewRadians(),
                NearMetres = NearMetres,
            },
            new FlyCamera { SpeedMetresPerSecond = (float)options.Camera.FlySpeed },
            new ActiveCamera()
        );
    }
}
