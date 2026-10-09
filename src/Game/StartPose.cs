using System.Numerics;
using Game.WorldGeneration;
using Itera.Engine.Components;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;

namespace Game;

/// <summary>
/// Where the player's eye and the debug camera start, and where they look: <c>Game:World:CameraPose</c> when it is set,
/// else at the world's middle, a person's eye height over the ground (or a boulder) there, looking along -z. One per
/// match (its seed).
/// </summary>
public sealed record StartPose(Vector3 Eye, float Yaw, float Pitch)
{
    /// <summary>The start for <paramref name="options"/> in the world of <paramref name="seed"/>.</summary>
    public static StartPose For(WorldOptions options, ulong seed, float eyeMetres)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (
            !string.IsNullOrWhiteSpace(options.CameraPose)
            && CameraPoseText.TryParse(options.CameraPose, out var eye, out var yaw, out var pitch)
        )
        {
            return new StartPose(eye, yaw, pitch);
        }

        var ground = (float)GameGenerator.SurfaceHeight(0, 0, seed) / VoxelConstants.CentimetresPerMetre;
        return new StartPose(new Vector3(0, ground + eyeMetres, 0), 0, 0);
    }

    /// <summary>The start as a camera pose.</summary>
    public CameraPose Pose =>
        new()
        {
            Position = Eye,
            Yaw = Yaw,
            Pitch = Pitch,
        };
}
