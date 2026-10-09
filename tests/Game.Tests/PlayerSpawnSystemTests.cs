using System.Numerics;
using Arch.Core;
using Game.Systems;
using Itera.Engine.Components;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Tests;

/// <summary>
/// The starter's player: made once at the start with every component the engine's player systems read, standing under
/// the start pose's eye and looking where it looks, waiting for the voxels round it; a step afterwards does nothing and
/// allocates nothing.
/// </summary>
public sealed class PlayerSpawnSystemTests
{
    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        LocalPlayer,
        PlayerCommand,
        PlayerMode,
        CharacterSize,
        CharacterPosition,
        CharacterVelocity,
        CharacterContact,
        EyeSmoothing,
        CharacterStandIn,
        MovementStats,
        ThirdPersonView
    >();

    [Fact]
    public void The_start_makes_one_player_under_the_start_eye_with_every_component_the_engine_reads()
    {
        using var world = World.Create();
        var start = new StartPose(new Vector3(3, 20, -4), 0.5f, -0.2f);
        using var system = new PlayerSpawnSystem(
            world,
            new PlayerOptions { HeightMetres = 2, ThirdPerson = true },
            start,
            NullLogger<PlayerSpawnSystem>.Instance
        );

        system.Initialize();

        Assert.Equal(1, world.CountEntities(in s_player));
        world.Query(
            in s_player,
            (
                ref PlayerCommand command,
                ref PlayerMode mode,
                ref CharacterSize size,
                ref CharacterPosition position,
                ref CharacterContact contact,
                ref ThirdPersonView view
            ) =>
            {
                Assert.Equal(CharacterSize.ForHeight(2).EyeMetres, size.EyeMetres, 5);
                Assert.Equal(start.Eye - new Vector3(0, size.EyeMetres, 0), position.Feet);
                Assert.Equal(position.Feet, position.PreviousFeet);
                Assert.Equal((0.5f, -0.2f), (command.Yaw, command.Pitch));
                Assert.False(mode.Flying);
                Assert.True(contact.Waiting);
                Assert.True(view.On);
                Assert.Equal(ThirdPersonCamera.StartMetres, view.Metres);
            }
        );
    }

    [Fact]
    public void A_step_does_nothing_and_allocates_nothing()
    {
        using var world = World.Create();
        using var system = new PlayerSpawnSystem(
            world,
            new PlayerOptions(),
            new StartPose(new Vector3(0, 8, 0), 0, 0),
            NullLogger<PlayerSpawnSystem>.Instance
        );
        system.Initialize();

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FixedStepTime(1f / 60, 0)));
        Assert.Equal(1, world.CountEntities(in s_player));
    }
}
