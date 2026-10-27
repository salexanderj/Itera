using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Game.Systems;

/// <summary>
/// Makes the local player at the match's start (fixed group, first; it does nothing after): an upright body of the
/// options' height, standing under the start pose's eye and looking where it looks, with every component the engine's
/// player systems read and a <see cref="Climber"/> (the engine's <see cref="ClimbingMode"/>). It waits for the voxels round it to load (<see cref="CharacterContact.Waiting"/>). A game adds
/// its own components here.
/// </summary>
public sealed partial class PlayerSpawnSystem(
    World world,
    PlayerOptions options,
    StartPose start,
    ILogger<PlayerSpawnSystem> logger,
    WorldOrigin? origin = null
) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
{
    public override void Initialize()
    {
        var size = CharacterSize.ForHeight((float)options.HeightMetres);
        // The start is absolute; the player's feet are local to the match's origin.
        var feet = (origin ?? new WorldOrigin()).ToLocal(start.Eye) - new Vector3(0, size.EyeMetres, 0);
        World.Create(
            new LocalPlayer(),
            new PlayerCommand { Yaw = start.Yaw, Pitch = start.Pitch },
            new PlayerMode(),
            size,
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            new CharacterVelocity(),
            new CharacterContact { Waiting = true },
            new EyeSmoothing(),
            new CharacterStandIn(),
            MovementStats.Default,
            new ThirdPersonView { On = options.ThirdPerson, Metres = ThirdPersonCamera.StartMetres },
            new Climber()
        );
        LogCreated(size.HeightMetres, feet.X, feet.Y, feet.Z);
    }

    public override void Update(in FixedStepTime step) { }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Player: {Height:F2} m tall, feet at ({X:F2}, {Y:F2}, {Z:F2}) m"
    )]
    private partial void LogCreated(float height, float x, float y, float z);
}
