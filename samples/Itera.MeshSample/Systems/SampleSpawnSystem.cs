using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;
using Itera.Engine.Systems;
using Itera.Modding;

namespace Itera.MeshSample.Systems;

/// <summary>
/// Makes the local player at the match's start (fixed group, after the yard; it does nothing after): a 1.7 m body on
/// the ground at <see cref="StartFeet"/>, looking along -z at the blocks, with the engine's player components and a
/// <see cref="Climber"/> (the engine's <see cref="ClimbingMode"/>).
/// </summary>
public sealed class SampleSpawnSystem(World world, WorldOrigin origin)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    /// <summary>Where the player's feet start, an absolute point.</summary>
    public static WorldPoint StartFeet { get; } = new(0, 0, 6);

    /// <summary>The start's pitch, in radians (its yaw is 0: looking along -z).</summary>
    public const float StartPitch = -0.1f;

    /// <summary>The start's eye, yaw, and pitch, for the cameras, local to <paramref name="origin"/>.</summary>
    public static CameraPose StartPoseIn(WorldOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return new()
        {
            Position = origin.ToLocal(StartFeet.Plus(0, CharacterSize.ForHeight(1.7f).EyeMetres, 0)),
            Yaw = 0,
            Pitch = StartPitch,
        };
    }

    public override void Initialize()
    {
        var size = CharacterSize.ForHeight(1.7f);
        var feet = origin.ToLocal(StartFeet);
        World.Create(
            new LocalPlayer(),
            new PlayerCommand { Yaw = 0, Pitch = StartPitch },
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
