using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="CharacterStepper"/>: one character's step is the step <see cref="PlayerMovementSystem"/> gives it, a step
/// run again from the same state and command gives the same state (what a client's replay needs), it refuses an
/// entity it cannot move, and it allocates nothing; and through the system, a character that is not the local player
/// crouches and is held back moving backwards as the local player is.
/// </summary>
public sealed class CharacterStepperTests : IDisposable
{
    private const float Seconds = 1f / 60;

    private readonly VoxelWorld _voxels;
    private readonly VoxelCharacterMover _mover;
    private readonly PlayArea _area = new(VoxelGrid.Unit);

    public CharacterStepperTests()
    {
        _voxels = TestWorlds.Load(new FlatGenerator(), Vector3.Zero, radiusMetres: 6);
        _mover = new VoxelCharacterMover(_voxels, new NothingPassable());
    }

    public void Dispose() => _voxels.Dispose();

    /// <summary>A walk, a sprint, a jump, a turn, a crouch held and let go, and a walk backwards, a step each.</summary>
    private static PlayerCommand CommandAt(int tick) =>
        tick switch
        {
            < 20 => new PlayerCommand { Move = new Vector2(0, 1) },
            < 40 => new PlayerCommand
            {
                Move = new Vector2(0.6f, 0.8f),
                Buttons = PlayerButtons.Sprint,
                Yaw = 0.3f,
            },
            40 => new PlayerCommand
            {
                Move = new Vector2(0, 1),
                Buttons = PlayerButtons.Jump,
                Yaw = 0.3f,
            },
            < 80 => new PlayerCommand { Move = new Vector2(-1, 0), Yaw = 1.1f },
            < 110 => new PlayerCommand
            {
                Move = new Vector2(0, 1),
                Buttons = PlayerButtons.Crouch,
                Yaw = 1.1f,
            },
            _ => new PlayerCommand
            {
                Move = new Vector2(0, -1),
                Buttons = PlayerButtons.Sprint,
                Yaw = 1.1f,
            },
        };

    [Fact]
    public void A_step_is_the_systems_step_for_the_same_character()
    {
        using var systemWorld = World.Create();
        using var stepperWorld = World.Create();
        using var system = new PlayerMovementSystem(
            systemWorld,
            _mover,
            _area,
            NullLogger<PlayerMovementSystem>.Instance
        );
        system.Initialize();
        var moved = CreateCharacter(systemWorld, local: true);
        var stepped = CreateCharacter(stepperWorld, local: false);
        var stepper = new CharacterStepper(stepperWorld, _mover);

        var crouched = false;
        for (var tick = 0; tick < 150; tick++)
        {
            systemWorld.Get<PlayerCommand>(moved) = CommandAt(tick);
            stepperWorld.Get<PlayerCommand>(stepped) = CommandAt(tick);
            system.Update(new FixedStepTime(Seconds, (ulong)tick));
            Assert.True(stepper.Step(stepped, Seconds));

            Assert.Equal(State.Of(systemWorld, moved), State.Of(stepperWorld, stepped));
            crouched |= stepperWorld.Get<PlayerMode>(stepped).Crouching;
        }

        // The character walked, jumped, and crouched: the steps compared were not all standing still.
        Assert.True(crouched);
        Assert.True(stepperWorld.Get<CharacterPosition>(stepped).Feet.Length() > 3);
    }

    [Fact]
    public void A_step_run_again_from_the_same_state_and_command_gives_the_same_state()
    {
        using var world = World.Create();
        var entity = CreateCharacter(world, local: false);
        var stepper = new CharacterStepper(world, _mover);
        var states = new State[150];
        State? saved = null;
        for (var tick = 0; tick < states.Length; tick++)
        {
            if (tick == 30)
            {
                saved = State.Of(world, entity);
            }

            world.Get<PlayerCommand>(entity) = CommandAt(tick);
            stepper.Step(entity, Seconds);
            states[tick] = State.Of(world, entity);
        }

        // Back to the state before tick 30, as a correction puts it, and the same commands again.
        saved!.Value.Restore(world, entity);
        for (var tick = 30; tick < states.Length; tick++)
        {
            world.Get<PlayerCommand>(entity) = CommandAt(tick);
            stepper.Step(entity, Seconds);
            Assert.Equal(states[tick], State.Of(world, entity));
        }
    }

    [Fact]
    public void An_entity_that_is_gone_bodiless_or_without_a_moving_component_is_not_stepped()
    {
        using var world = World.Create();
        var stepper = new CharacterStepper(world, _mover);
        var bodiless = CreateCharacter(world, local: false);
        world.Add(bodiless, new Bodiless());
        var incomplete = world.Create(new PlayerCommand { Move = Vector2.UnitY }, new CharacterPosition());
        var gone = CreateCharacter(world, local: false);
        world.Destroy(gone);
        var before = State.Of(world, bodiless);

        Assert.False(stepper.Step(bodiless, Seconds));
        Assert.False(stepper.Step(incomplete, Seconds));
        Assert.False(stepper.Step(gone, Seconds));
        Assert.False(stepper.Step(Entity.Null, Seconds));
        Assert.Equal(before, State.Of(world, bodiless));
        Assert.Equal(Vector3.Zero, world.Get<CharacterPosition>(incomplete).Feet);
    }

    [Fact]
    public void A_step_allocates_nothing()
    {
        using var world = World.Create();
        var entity = CreateCharacter(world, local: false);
        var stepper = new CharacterStepper(world, _mover);
        for (var tick = 0; tick < 200; tick++)
        {
            world.Get<PlayerCommand>(entity) = CommandAt(tick % 150);
            stepper.Step(entity, Seconds);
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var tick = 0; tick < 150; tick++)
            {
                world.Get<PlayerCommand>(entity) = CommandAt(tick);
                stepper.Step(entity, Seconds);
            }
        });

        Assert.Equal(0, bytes);
    }

    [Fact]
    public void A_character_that_is_not_the_local_player_crouches_and_is_held_back_moving_backwards()
    {
        using var world = World.Create();
        using var system = new PlayerMovementSystem(world, _mover, _area, NullLogger<PlayerMovementSystem>.Instance);
        system.Initialize();
        var remote = CreateCharacter(world, local: false);
        world.Add(remote, new RemotePlayer());
        var standing = world.Get<CharacterSize>(remote);

        for (var tick = 0; tick < 90; tick++)
        {
            world.Get<PlayerCommand>(remote) = new PlayerCommand
            {
                Move = new Vector2(0, -1),
                Buttons = PlayerButtons.Crouch | PlayerButtons.Sprint,
            };
            system.Update(new FixedStepTime(Seconds, (ulong)tick));
        }

        Assert.True(world.Get<PlayerMode>(remote).Crouching);
        Assert.Equal(
            standing.HeightMetres * PlayerMovementSystem.CrouchHeightShare,
            world.Get<CharacterSize>(remote).HeightMetres,
            4
        );
        var speed = world.Get<CharacterVelocity>(remote).MetresPerSecond.Length();
        Assert.Equal(
            MovementStats.Default.Walk * PlayerMovementSystem.CrouchSpeedShare * MovementStats.Default.BackShare,
            speed,
            2
        );
    }

    /// <summary>A 1.7 m character standing on the floor at the middle, the local player or not.</summary>
    private static Entity CreateCharacter(World world, bool local)
    {
        var size = CharacterSize.ForHeight(1.7f);
        var feet = new Vector3(0, 0.05f, 0);
        var entity = world.Create(
            new PlayerCommand(),
            new PlayerMode(),
            size,
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            new CharacterVelocity(),
            new CharacterContact { Waiting = true },
            new EyeSmoothing(),
            MovementStats.Default
        );
        if (local)
        {
            world.Add(entity, new LocalPlayer());
        }

        return entity;
    }

    /// <summary>Everything a step reads and writes of a character, compared whole.</summary>
    private readonly record struct State(
        PlayerCommand Command,
        PlayerMode Mode,
        CharacterSize Size,
        CharacterPosition Position,
        CharacterVelocity Velocity,
        CharacterContact Contact,
        EyeSmoothing Eye
    )
    {
        public static State Of(World world, Entity entity) =>
            new(
                world.Get<PlayerCommand>(entity),
                world.Get<PlayerMode>(entity),
                world.Get<CharacterSize>(entity),
                world.Get<CharacterPosition>(entity),
                world.Get<CharacterVelocity>(entity),
                world.Get<CharacterContact>(entity),
                world.Get<EyeSmoothing>(entity)
            );

        public void Restore(World world, Entity entity)
        {
            world.Get<PlayerCommand>(entity) = Command;
            world.Get<PlayerMode>(entity) = Mode;
            world.Get<CharacterSize>(entity) = Size;
            world.Get<CharacterPosition>(entity) = Position;
            world.Get<CharacterVelocity>(entity) = Velocity;
            world.Get<CharacterContact>(entity) = Contact;
            world.Get<EyeSmoothing>(entity) = Eye;
        }
    }
}
