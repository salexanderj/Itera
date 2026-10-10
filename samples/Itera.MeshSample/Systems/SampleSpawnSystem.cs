using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Systems;
using Itera.Modding;

namespace Itera.MeshSample.Systems;

/// <summary>
/// Makes the local player at the match's start (fixed group, after the yard; it does nothing after): a 1.7 m body on
/// the ground at <see cref="StartFeet"/>, looking along -z at the blocks, with the engine's player components and a
/// <see cref="Climber"/> (the engine's <see cref="ClimbingMode"/>).
/// </summary>
public sealed class SampleSpawnSystem(World world) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
{
    /// <summary>Where the player's feet start, in metres.</summary>
    public static Vector3 StartFeet { get; } = new(0, 0, 6);

    /// <summary>The start's eye, yaw, and pitch, for the cameras.</summary>
    public static CameraPose StartPose =>
        new()
        {
            Position = StartFeet + new Vector3(0, CharacterSize.ForHeight(1.7f).EyeMetres, 0),
            Yaw = 0,
            Pitch = -0.1f,
        };

    public override void Initialize()
    {
        var size = CharacterSize.ForHeight(1.7f);
        var feet = StartFeet;
        World.Create(
            new LocalPlayer(),
            new PlayerCommand { Yaw = StartPose.Yaw, Pitch = StartPose.Pitch },
            new PlayerMode(),
            size,
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            new CharacterVelocity(),
            new CharacterContact { Waiting = true },
            new EyeSmoothing(),
            new CharacterStandIn(),
            MovementStats.Default,
            new ThirdPersonView { Metres = ThirdPersonCamera.StartMetres },
            new Climber()
        );
    }

    public override void Update(in FixedStepTime step) { }
}
