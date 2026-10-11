using Game.WorldGeneration;
using Itera.Engine.Components;
using Itera.Engine.Maths;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;

namespace Game;

/// <summary>
/// Where the player's eye and the debug camera start, and where they look: <c>Game:World:CameraPose</c> when it is set,
/// else at the world's middle, a person's eye height over the ground (or a boulder) there, looking along -z. One per
/// match (its seed).
/// </summary>
/// <param name="Eye">The eye, an absolute point.</param>
/// <param name="Yaw">The yaw, in radians.</param>
/// <param name="Pitch">The pitch, in radians.</param>
public sealed record StartPose(WorldPoint Eye, float Yaw, float Pitch) : IOriginStart
{
    /// <summary>Where the match starts: the eye, which the match's first origin is put on the whole metres nearest.</summary>
    public WorldPoint Start => Eye;

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

        var ground = GameGenerator.SurfaceHeight(0, 0, seed) / (double)VoxelConstants.CentimetresPerMetre;
        return new StartPose(new WorldPoint(0, ground + eyeMetres, 0), 0, 0);
    }

    /// <summary>The start as a camera pose local to <paramref name="origin"/>.</summary>
    public CameraPose PoseIn(WorldOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return new()
        {
            Position = origin.ToLocal(Eye),
            Yaw = Yaw,
            Pitch = Pitch,
        };
    }
}
