using System.Numerics;
using Arch.Core;
using Arch.System;
using Game.WorldGeneration;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Game.Systems;

/// <summary>
/// Puts the fox (<see cref="GameModels.Fox"/>) at the match's start (fixed group, after <see cref="PlayerSpawnSystem"/>;
/// it does nothing after): standing on the ground (or a boulder) <see cref="AheadMetres"/> in front of the start pose,
/// side on to it, facing its right. The engine's <c>MeshDrawSystem</c> draws it. A game's own models start the same way.
/// </summary>
public sealed partial class FoxSpawnSystem(
    World world,
    MeshAssets assets,
    StartPose start,
    ulong seed,
    ILogger<FoxSpawnSystem> logger
) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
{
    /// <summary>How far in front of the start pose's eye the fox stands, in metres.</summary>
    public const float AheadMetres = 4;

    public override void Initialize()
    {
        var asset = assets.Find(GameModels.Fox);
        if (asset < 0)
        {
            throw new InvalidOperationException($"The model '{GameModels.Fox}' was not declared (GameModels.Declare).");
        }

        var at = start.Eye + ThirdPersonCamera.Forward(start.Yaw, 0) * AheadMetres;
        var ground = GameGenerator.SurfaceHeight(
            (int)MathF.Round(at.X * VoxelConstants.CentimetresPerMetre),
            (int)MathF.Round(at.Z * VoxelConstants.CentimetresPerMetre),
            seed
        );
        at.Y = (float)ground / VoxelConstants.CentimetresPerMetre;
        // Its head (+z) towards the start's right, so the start sees its side.
        var facing = Quaternion.CreateFromYawPitchRoll(start.Yaw + MathF.PI / 2, 0, 0);
        World.Create(MeshPlacement.At(at, facing), DrawnMesh.Of(asset));
        LogPlaced(at.X, at.Y, at.Z);
    }

    public override void Update(in FixedStepTime step) { }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Fox: standing at ({X:F2}, {Y:F2}, {Z:F2}) m")]
    private partial void LogPlaced(float x, float y, float z);
}
