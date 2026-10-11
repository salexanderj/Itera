using System.Numerics;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

public sealed class PlayerMovementSystemTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private static readonly QueryDescription s_players = new QueryDescription().WithAll<
        CharacterPosition,
        CharacterVelocity,
        CharacterContact,
        LocalPlayer
    >();

    private readonly VoxelWorld _voxels;
    private readonly VoxelCharacterMover _mover;
    private readonly PlayArea _area = new(VoxelGrid.Unit);

    public PlayerMovementSystemTests()
    {
        _voxels = LoadWorld(new FlatGenerator(), Vector3.Zero);
        _mover = new VoxelCharacterMover(_voxels, new NothingPassable());
    }

    public void Dispose() => _voxels.Dispose();

    [Fact]
    public void A_command_is_made_safe_before_it_is_acted_on()
    {
        // What a peer could send: numbers that are not numbers, a move faster than the keys allow, a pitch past
        // straight up, and button bits that mean nothing. The game's actions are not the engine's to judge.
        var command = new PlayerCommand
        {
            Move = new Vector2(3, 4),
            Yaw = float.NaN,
            Pitch = 2,
            Buttons = PlayerButtons.Jump | (PlayerButtons)(1 << 20),
            Actions = 0xF0F0,
        };
        PlayerMovementSystem.Sanitize(ref command);
        var bad = new PlayerCommand
        {
            Move = new Vector2(float.NaN, 1),
            Yaw = 7,
            Pitch = float.NegativeInfinity,
        };
        PlayerMovementSystem.Sanitize(ref bad);

        Assert.Equal(1, command.Move.Length(), 5);
        Assert.Equal(0, command.Yaw);
        Assert.Equal(CameraRules.MaxPitch, command.Pitch);
        Assert.Equal(PlayerButtons.Jump, command.Buttons);
        Assert.Equal(0xF0F0u, command.Actions);
        Assert.Equal(Vector2.Zero, bad.Move);
        Assert.InRange(bad.Yaw, -MathF.PI, MathF.PI);
        Assert.Equal(0, bad.Pitch);
    }

    [Fact]
    public void A_velocity_that_is_not_a_number_stops_the_player_instead_of_freezing_it()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 3, 0));
        Run(world, system, 90, default);
        var (before, _, _) = Read(world);
        world.Query(
            in s_players,
            (ref CharacterVelocity velocity) =>
                velocity = new CharacterVelocity
                {
                    MetresPerSecond = new Vector3(float.NaN, 0, 0),
                    Push = new Vector3(0, float.NaN, 0),
                }
        );

        Run(world, system, 60, new PlayerCommand { Move = new Vector2(0, 1) });

        var (after, velocity, contact) = Read(world);
        Assert.True(float.IsFinite(after.Feet.X) && float.IsFinite(after.Feet.Y) && float.IsFinite(after.Feet.Z));
        Assert.True(float.IsFinite(velocity.MetresPerSecond.X));
        Assert.True(contact.Grounded);
        // It walked on after the bad step.
        Assert.True(
            Vector3.Distance(before.Feet, after.Feet) > 1,
            $"The player moved from {before.Feet} to {after.Feet}."
        );
    }

    [Fact]
    public void The_system_makes_no_entity_and_a_player_waits_for_its_first_step()
    {
        using var world = World.Create();
        using var system = new PlayerMovementSystem(world, _mover, _area, NullLogger<PlayerMovementSystem>.Instance);
        system.Initialize();
        Assert.Equal(0, world.Size);

        CreatePlayer(world, new Vector3(1, 3, 2));
        var (position, _, contact) = Read(world);

        var size = CharacterSize.ForHeight(1.7f);
        Assert.Equal(new Vector3(1, 3 - size.EyeMetres, 2), position.Feet);
        Assert.Equal(position.Feet, position.PreviousFeet);
        Assert.True(contact.Waiting);
    }

    [Fact]
    public void A_player_falls_and_lands_on_the_floor()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 3, 0));

        Run(world, system, 90, default);

        var (position, velocity, contact) = Read(world);
        Assert.Equal(0, position.Feet.Y, 4);
        Assert.True(contact.Grounded);
        Assert.False(contact.Waiting);
        Assert.Equal(0, velocity.MetresPerSecond.Y);
    }

    [Theory]
    [InlineData(false, 4.5f)]
    [InlineData(true, 7f)]
    public void Forward_walks_along_the_look_at_walking_or_sprinting_speed(bool sprint, float speed)
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0), yaw: MathF.PI / 2);
        Run(world, system, 30, default);

        var command = new PlayerCommand
        {
            Move = new Vector2(0, 1),
            Yaw = MathF.PI / 2,
            Buttons = sprint ? PlayerButtons.Sprint : PlayerButtons.None,
        };
        Run(world, system, 20, command);

        // Yaw 90 degrees looks along -X.
        var (position, velocity, contact) = Read(world);
        Assert.Equal(-speed, velocity.MetresPerSecond.X, 3);
        Assert.Equal(0, velocity.MetresPerSecond.Z, 3);
        Assert.True(position.Feet.X < -speed * 0.25f, $"Walked to x = {position.Feet.X}.");
        Assert.True(contact.Grounded);
    }

    [Fact]
    public void Jump_lifts_the_feet_to_the_jump_height()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        Run(world, system, 30, default);

        var highest = 0f;
        for (var i = 0; i < 40; i++)
        {
            Run(world, system, 1, new PlayerCommand { Buttons = i == 0 ? PlayerButtons.Jump : PlayerButtons.None });
            highest = Math.Max(highest, Read(world).Position.Feet.Y);
        }

        // The highest step comes within a centimetre of the jump height.
        Assert.InRange(highest, 1.09f, 1.11f);
    }

    [Theory]
    [InlineData(false, 6.75f)]
    [InlineData(true, 10.5f)]
    public void The_players_stats_set_its_walking_and_sprinting_speed(bool sprint, float speed)
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0), yaw: MathF.PI / 2);
        var stats = MovementStats.Default;
        stats.Walk = 6.75f;
        stats.Sprint = 10.5f;
        SetStats(world, stats);
        Run(world, system, 30, default);

        var command = new PlayerCommand
        {
            Move = new Vector2(0, 1),
            Yaw = MathF.PI / 2,
            Buttons = sprint ? PlayerButtons.Sprint : PlayerButtons.None,
        };
        // Up to speed (50 m/s each second) and still on the loaded floor.
        Run(world, system, 16, command);

        Assert.Equal(-speed, Read(world).Velocity.MetresPerSecond.X, 3);
    }

    [Fact]
    public void The_players_stats_set_its_jumps_height_and_its_gravity()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        var stats = MovementStats.Default;
        stats.JumpHeight = 2.2f;
        stats.Gravity = 30;
        SetStats(world, stats);
        Run(world, system, 30, default);

        var highest = 0f;
        for (var i = 0; i < 60; i++)
        {
            Run(world, system, 1, new PlayerCommand { Buttons = i == 0 ? PlayerButtons.Jump : PlayerButtons.None });
            highest = Math.Max(highest, Read(world).Position.Feet.Y);
        }

        Assert.InRange(highest, 2.18f, 2.22f);
    }

    [Fact]
    public void Under_half_the_matchs_gravity_a_jump_rises_twice_as_high_and_lasts_twice_as_long()
    {
        var options = new EngineOptions();
        options.Physics.Gravity.Y = -PhysicsWorld.StandardGravity / 2;
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, Options.Create(options));
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0), physics: physics);
        Run(world, system, 30, default);
        var ground = Read(world).Position.Feet.Y;

        var highest = ground;
        var airborne = 0;
        for (var i = 0; i < 240; i++)
        {
            Run(world, system, 1, new PlayerCommand { Buttons = i == 0 ? PlayerButtons.Jump : PlayerButtons.None });
            highest = Math.Max(highest, Read(world).Position.Feet.Y);
            airborne += Read(world).Contact.Grounded ? 0 : 1;
        }

        Assert.Equal(0.5f, physics.GravityShare, 5);
        // 1.1 m and about 40 steps under the character's own gravity (20 m/s² up and down at 6.6 m/s).
        Assert.InRange(highest - ground, 2.17f, 2.23f);
        Assert.InRange(airborne, 76, 84);
    }

    [Fact]
    public void A_player_on_a_60_degree_ramp_slides_down_it_and_cannot_jump()
    {
        using var voxels = LoadWorld(new RampGenerator(), new Vector3(0.5f, 1, 0));
        var mover = new VoxelCharacterMover(voxels, new NothingPassable());
        using var world = World.Create();
        // Dropped onto the ramp 60 cm up it, pressing jump every step.
        using var system = Create(world, new Vector3(0.6f, 3.5f, 0), mover: mover);

        var sawSteep = false;
        var jumpedWhileSteep = false;
        for (var i = 0; i < 120; i++)
        {
            var wasSteep = Read(world).Contact.Steep;
            Run(world, system, 1, new PlayerCommand { Buttons = PlayerButtons.Jump });
            var (_, velocity, contact) = Read(world);
            sawSteep |= contact.Steep;
            jumpedWhileSteep |= wasSteep && velocity.MetresPerSecond.Y > 1;
        }

        var feet = Read(world).Position.Feet;
        Assert.True(sawSteep);
        Assert.False(jumpedWhileSteep);
        Assert.True(feet.X < 0.3f, $"Still on the ramp at x = {feet.X:F2} m.");
    }

    [Fact]
    public void A_player_on_the_rim_of_an_85_degree_face_cannot_jump_up_it()
    {
        // A face 4 m high, steeper than the step reaches across 8 cm: it must count as steep, not as a wall to ignore.
        using var voxels = LoadWorld(new RampGenerator(risePerMille: 11430, top: 400), new Vector3(0, 1, 0));
        var mover = new VoxelCharacterMover(voxels, new NothingPassable());
        using var world = World.Create();
        // Dropped so the body's high edge lands on the face, 1.7 m up it; then jumping and pushing into the face.
        using var system = Create(world, new Vector3(-0.1f, 4, 0), yaw: -MathF.PI / 2, mover: mover);

        var landed = float.NaN;
        var highest = float.MinValue;
        for (var i = 0; i < 180; i++)
        {
            Run(
                world,
                system,
                1,
                new PlayerCommand
                {
                    Move = new Vector2(0, 1),
                    Yaw = -MathF.PI / 2,
                    Buttons = PlayerButtons.Jump,
                }
            );
            var (position, _, contact) = Read(world);
            if (contact.Grounded && float.IsNaN(landed))
            {
                landed = position.Feet.Y;
            }
            else if (contact.Grounded)
            {
                highest = Math.Max(highest, position.Feet.Y);
            }
        }

        Assert.False(float.IsNaN(landed));
        Assert.True(highest <= landed + 0.02f, $"Landed at {landed:F2} m and later stood at {highest:F2} m.");
    }

    [Fact]
    public void While_flying_the_feet_follow_the_camera()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0), flying: true);
        world.Create(new CameraPose { Position = new Vector3(5, 20, -3) }, new ActiveCamera());

        Run(world, system, 1, default);

        var (position, velocity, _) = Read(world);
        var eye = CharacterSize.ForHeight(1.7f).EyeMetres;
        Assert.Equal(new Vector3(5, 20 - eye, -3), position.Feet);
        Assert.Equal(position.Feet, position.PreviousFeet);
        Assert.Equal(Vector3.Zero, velocity.MetresPerSecond);
    }

    [Fact]
    public void A_player_waits_where_voxels_are_not_loaded()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(100, 5, 0));

        Run(world, system, 30, new PlayerCommand { Move = new Vector2(0, 1) });

        var (position, velocity, contact) = Read(world);
        Assert.True(contact.Waiting);
        Assert.Equal(new Vector3(100, 5 - CharacterSize.ForHeight(1.7f).EyeMetres, 0), position.Feet);
        Assert.Equal(Vector3.Zero, velocity.MetresPerSecond);
    }

    /// <summary>A body without a body stays where it is, whatever its command asks.</summary>
    [Fact]
    public void A_bodiless_player_is_not_moved()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 3, 0));
        world.Add(Player(world), new Bodiless());
        var before = Read(world).Position;

        Run(world, system, 60, new PlayerCommand { Move = new Vector2(0, 1), Buttons = PlayerButtons.Jump });

        var (after, velocity, _) = Read(world);
        Assert.Equal(before.Feet, after.Feet);
        Assert.Equal(Vector3.Zero, velocity.MetresPerSecond);
    }

    /// <summary>A crouched player that loses its body stands up once its buttons are cleared, as the input system clears them.</summary>
    [Fact]
    public void A_crouched_player_that_goes_bodiless_stands_up()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        Run(world, system, 30, default);
        var player = Player(world);
        var standing = world.Get<CharacterSize>(player);
        Run(world, system, 1, new PlayerCommand { Buttons = PlayerButtons.Crouch });
        Assert.True(world.Get<PlayerMode>(player).Crouching);

        world.Add(player, new Bodiless());
        Run(world, system, 1, default);

        Assert.False(world.Get<PlayerMode>(player).Crouching);
        Assert.Equal(standing.HeightMetres, world.Get<CharacterSize>(player).HeightMetres, 4);
    }

    [Fact]
    public void Control_crouches_lower_and_slower_with_no_jump_or_sprint_and_letting_go_stands()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        Run(world, system, 30, default);
        var player = Player(world);
        var standing = world.Get<CharacterSize>(player);
        var forward = new PlayerCommand
        {
            Move = new Vector2(0, 1),
            Buttons = PlayerButtons.Crouch | PlayerButtons.Sprint | PlayerButtons.Jump,
        };

        Run(world, system, 1, forward);
        var crouched = world.Get<CharacterSize>(player);
        Assert.True(world.Get<PlayerMode>(player).Crouching);
        Assert.Equal(standing.HeightMetres * PlayerMovementSystem.CrouchHeightShare, crouched.HeightMetres, 4);
        Assert.Equal(standing.EyeMetres * PlayerMovementSystem.CrouchHeightShare, crouched.EyeMetres, 4);
        Assert.Equal(standing.StepMetres, crouched.StepMetres);
        // The eye starts where it was and moves down to the crouched height at a steady pace.
        var eye = world.Get<EyeSmoothing>(player);
        Assert.Equal(standing.EyeMetres - crouched.EyeMetres, eye.PreviousHeight, 4);
        Assert.Equal(eye.PreviousHeight - PlayerMovementSystem.HeightEyeMetresPerSecond / 60f, eye.Height, 4);

        Run(world, system, 60, forward);
        var (position, velocity, contact) = Read(world);
        Assert.Equal(
            MovementStats.Default.Walk * PlayerMovementSystem.CrouchSpeedShare,
            velocity.MetresPerSecond.Length(),
            2
        );
        Assert.Equal(0, position.Feet.Y, 4);
        Assert.True(contact.Grounded);
        Assert.Equal(0, world.Get<EyeSmoothing>(player).Offset);
        Assert.Equal(0, world.Get<EyeSmoothing>(player).Height);

        Run(world, system, 1, new PlayerCommand { Move = new Vector2(0, 1) });
        Assert.False(world.Get<PlayerMode>(player).Crouching);
        Assert.Equal(standing.HeightMetres, world.Get<CharacterSize>(player).HeightMetres, 4);
        Assert.Equal(standing.EyeMetres, world.Get<CharacterSize>(player).EyeMetres, 4);
        // Standing up, the eye starts low and moves up.
        Assert.True(world.Get<EyeSmoothing>(player).PreviousHeight < 0);
    }

    /// <summary>
    /// Moving backwards: at most 0.7 of full speed straight back, between that and full speed moving back and to the
    /// side, and no sprint; to the side, full speed and sprint.
    /// </summary>
    [Theory]
    [InlineData(0, -1, false, 4.5f * PlayerMovementSystem.BackwardSpeedShare)]
    [InlineData(0, -1, true, 4.5f * PlayerMovementSystem.BackwardSpeedShare)]
    [InlineData(-1, -1, true, 4.5f * (1 - (1 - PlayerMovementSystem.BackwardSpeedShare) * 0.70710678f))]
    [InlineData(1, -1, false, 4.5f * (1 - (1 - PlayerMovementSystem.BackwardSpeedShare) * 0.70710678f))]
    [InlineData(-1, 0, false, 4.5f)]
    [InlineData(1, 0, true, 7f)]
    [InlineData(-1, 1, true, 7f)]
    public void Backwards_it_moves_slower_and_cannot_sprint(float x, float y, bool sprint, float speed)
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        Run(world, system, 30, default);
        var player = Player(world);
        var command = new PlayerCommand
        {
            Move = Vector2.Normalize(new Vector2(x, y)),
            Buttons = sprint ? PlayerButtons.Sprint : PlayerButtons.None,
        };

        Run(world, system, 30, command);

        Assert.Equal(speed, Read(world).Velocity.MetresPerSecond.Length(), 2);
        // A game that spends a resource on a sprint reads the command's sprint after the move: none backwards.
        var sprinting = (world.Get<PlayerCommand>(player).Buttons & PlayerButtons.Sprint) != 0;
        Assert.Equal(sprint && y >= 0, sprinting);
    }

    [Fact]
    public void Crouched_backwards_it_moves_at_both_shares_and_one_command_used_again_stays_held()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        Run(world, system, 30, default);
        var player = Player(world);
        Run(world, system, 30, new PlayerCommand { Move = new Vector2(0, -1), Buttons = PlayerButtons.Crouch });
        Assert.Equal(
            4.5f * PlayerMovementSystem.CrouchSpeedShare * PlayerMovementSystem.BackwardSpeedShare,
            Read(world).Velocity.MetresPerSecond.Length(),
            2
        );

        // Standing again, the command set once and stepped on with: the move is held, not shrunk again each step.
        world.Get<PlayerCommand>(player) = new PlayerCommand { Move = new Vector2(0, -1) };
        for (var i = 0; i < 30; i++)
        {
            system.Update(in Step);
        }

        Assert.Equal(4.5f * PlayerMovementSystem.BackwardSpeedShare, Read(world).Velocity.MetresPerSecond.Length(), 2);
        Assert.Equal(PlayerMovementSystem.BackwardSpeedShare, world.Get<PlayerCommand>(player).Move.Length(), 5);
    }

    [Fact]
    public void Under_a_low_roof_it_stays_crouched_until_it_comes_out_and_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        Run(world, system, 30, default);
        var player = Player(world);
        var standing = world.Get<CharacterSize>(player).HeightMetres;
        Run(world, system, 1, new PlayerCommand { Buttons = PlayerButtons.Crouch });
        // A stone roof 1.4 m up, 2 m across, over the crouched player: room for its 1.31 m, not for its 1.7 m.
        _voxels.Apply(
            VoxelEdit.Box(
                new VoxelCoord(-100, 140, -100),
                new VoxelCoord(200, 20, 200),
                VoxelEditOperation.Set,
                FlatGenerator.Stone
            )
        );

        Run(world, system, 30, default);
        Assert.True(world.Get<PlayerMode>(player).Crouching);
        var start = SystemAllocation.StartMeasurement();
        system.Update(in Step);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);

        // Walking forward (-z) at the crouch's pace, it comes out from under the roof and stands up at once.
        var steps = 0;
        while (world.Get<PlayerMode>(player).Crouching && steps < 240)
        {
            Run(world, system, 1, new PlayerCommand { Move = new Vector2(0, 1) });
            steps++;
        }

        var feet = Read(world).Position.Feet;
        Assert.False(world.Get<PlayerMode>(player).Crouching);
        Assert.Equal(standing, world.Get<CharacterSize>(player).HeightMetres, 4);
        Assert.InRange(-feet.Z, 1, 1 + 2 * world.Get<CharacterSize>(player).RadiusMetres);
    }

    [Fact]
    public void Flying_ends_the_crouch_and_its_height_comes_back_whole()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        Run(world, system, 30, default);
        var player = Player(world);
        var standing = world.Get<CharacterSize>(player);
        var crouch = new PlayerCommand { Buttons = PlayerButtons.Crouch };
        Run(world, system, 1, crouch);
        Assert.True(world.Get<PlayerMode>(player).Crouching);

        world.Get<PlayerMode>(player).Flying = true;
        Run(world, system, 1, crouch);

        Assert.False(world.Get<PlayerMode>(player).Crouching);
        Assert.Equal(standing.HeightMetres, world.Get<CharacterSize>(player).HeightMetres, 4);
        Assert.Equal(standing.EyeMetres, world.Get<CharacterSize>(player).EyeMetres, 4);

        // Walking again, it can crouch again.
        world.Get<PlayerMode>(player).Flying = false;
        Run(world, system, 1, crouch);
        Assert.True(world.Get<PlayerMode>(player).Crouching);
    }

    [Fact]
    public void Sprinted_is_a_sprint_held_on_the_ground_at_speed()
    {
        var sprint = new PlayerCommand { Buttons = PlayerButtons.Sprint };
        var grounded = new CharacterContact { Grounded = true };
        var fast = new CharacterVelocity { MetresPerSecond = new Vector3(0, 0, -7) };

        Assert.True(PlayerMovementSystem.Sprinted(in sprint, new PlayerMode(), in grounded, in fast));
        Assert.False(PlayerMovementSystem.Sprinted(new PlayerCommand(), new PlayerMode(), in grounded, in fast));
        Assert.False(PlayerMovementSystem.Sprinted(in sprint, new PlayerMode { Flying = true }, in grounded, in fast));
        Assert.False(PlayerMovementSystem.Sprinted(in sprint, new PlayerMode(), new CharacterContact(), in fast));
        Assert.False(PlayerMovementSystem.Sprinted(in sprint, new PlayerMode(), in grounded, new CharacterVelocity()));
    }

    [Fact]
    public void Tp_moves_the_player_there_at_the_next_step_and_pos_says_where_it_looks()
    {
        using var world = World.Create();
        var commands = NewCommands();
        var output = new RecordingOutput();
        using var system = Create(world, new Vector3(0, 1.6f, 0), commands: commands, places: Places);
        Run(world, system, 30, default);

        commands.Execute("tp 100 5 0 90 10", output);
        Run(world, system, 1, default);
        commands.Execute("pos", output);

        var (position, velocity, contact) = Read(world);
        var eye = CharacterSize.ForHeight(1.7f).EyeMetres;
        Assert.Equal(new Vector3(100, 5 - eye, 0), position.Feet);
        Assert.Equal(Vector3.Zero, velocity.MetresPerSecond);
        Assert.True(contact.Waiting);
        Assert.Equal("100.00,5.00,0.00,90,10", output.Lines[^1]);
        Assert.Empty(output.Errors);

        // The one-word forms: the text form of a pose, and a named place.
        commands.Execute("tp 7,8,9,180,-20", output);
        Run(world, system, 1, default);
        Assert.Equal(new Vector3(7, 8 - eye, 9), Read(world).Position.Feet);
        commands.Execute("tp home", output);
        Run(world, system, 1, default);
        Assert.Equal(new Vector3(-30, 2 - eye, 40), Read(world).Position.Feet);
        Assert.Empty(output.Errors);
    }

    [Fact]
    public void Tp_refuses_a_place_outside_the_play_area_and_moves_the_fly_camera_too()
    {
        using var world = World.Create();
        var commands = NewCommands();
        var output = new RecordingOutput();
        using var system = Create(world, new Vector3(0, 1.6f, 0), commands: commands);
        world.Create(new CameraPose { Position = new Vector3(1, 2, 3) }, new FlyCamera(), new ActiveCamera());
        var feetBefore = Read(world).Position.Feet;

        commands.Execute("tp 1e9 5 0", output);
        Run(world, system, 1, default);

        Assert.Single(output.Errors);
        Assert.Contains("outside the play area", output.Errors[0], StringComparison.Ordinal);
        Assert.Equal(feetBefore.X, Read(world).Position.Feet.X);

        commands.Execute("tp 20 6 -4", output);
        Run(world, system, 1, default);
        var pose = default(CameraPose);
        world.Query(new QueryDescription().WithAll<CameraPose, FlyCamera>(), (ref CameraPose camera) => pose = camera);
        Assert.Equal(new Vector3(20, 6, -4), pose.Position);
    }

    /// <summary>
    /// A tp 600 m out asks for the origin there; the next step moves it before the jump, and the jump moves the active
    /// camera (a game's, with no fly camera's component) with the player, so a second step before any frame keeps the
    /// origin where the jump went instead of following the camera back.
    /// </summary>
    [Fact]
    public void A_far_tp_moves_the_origin_first_and_a_second_step_before_a_frame_keeps_it()
    {
        var options = Options.Create(new EngineOptions { Physics = { Threads = 1 } });
        using var ecs = new EcsWorld(NullLogger<EcsWorld>.Instance, new SystemTimes());
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, options);
        physics.Initialize();
        using var models = new VoxelModels();
        var shifts = new OriginShifts(
            _voxels.Origin,
            physics,
            ecs,
            models,
            new PointLights(),
            new ParticleEmitter(options, NullLogger<ParticleEmitter>.Instance),
            [],
            NullLogger<OriginShifts>.Instance,
            _voxels,
            options: options
        );
        var world = ecs.World;
        var commands = NewCommands();
        var output = new RecordingOutput();
        using var origin = new OriginShiftSystem(world, shifts);
        using var movement = new PlayerMovementSystem(
            world,
            _mover,
            _area,
            NullLogger<PlayerMovementSystem>.Instance,
            commands,
            shifts: shifts
        );
        origin.Initialize();
        movement.Initialize();
        CreatePlayer(world, new Vector3(0, 1.6f, 0));
        var camera = world.Create(new CameraPose { Position = new Vector3(0, 1.6f, 0) }, new ActiveCamera());

        commands.Execute("tp 600 5 0", output);
        for (var step = 0; step < 2; step++)
        {
            origin.Update(in Step);
            movement.Update(in Step);
        }

        Assert.Empty(output.Errors);
        Assert.Equal(new VoxelCoord(60_000, 500, 0), _voxels.Origin.Centimetres);
        Assert.Equal(Vector3.Zero, world.Get<CameraPose>(camera).Position);
        // Near the jump's end: past this test world's 51 m map, the next step falls through air.
        var feet = Read(world).Position.Feet;
        Assert.True(
            Vector3.Distance(new Vector3(0, -CharacterSize.ForHeight(1.7f).EyeMetres, 0), feet) < 0.05f,
            $"{feet}"
        );
    }

    [Fact]
    public void A_movement_mode_that_takes_the_step_moves_the_body_by_its_velocity_through_the_mover()
    {
        using var world = World.Create();
        // Straight up at 1 m/s with no step and no gravity, from standing on the floor.
        var mode = new Rising { Taking = true };
        using var system = Create(world, new Vector3(0, 1.6f, 0), mode: mode);
        Run(world, system, 30, default);
        var standing = Read(world).Position.Feet.Y;

        Run(world, system, 30, new PlayerCommand { Buttons = PlayerButtons.Jump });
        var (position, velocity, contact) = Read(world);

        Assert.Equal(Player(world), mode.Entity);
        Assert.Equal(standing + 0.5f, position.Feet.Y, 0.01f);
        Assert.Equal(1, velocity.MetresPerSecond.Y, 1e-4f);
        Assert.False(contact.Grounded);
        Assert.False(contact.Jumped);
    }

    [Fact]
    public void A_movement_mode_that_leaves_the_step_lets_the_engine_walk_and_jump()
    {
        using var world = World.Create();
        var mode = new Rising { Taking = false };
        using var system = Create(world, new Vector3(0, 1.6f, 0), mode: mode);
        Run(world, system, 30, default);
        var standing = Read(world).Position.Feet.Y;

        Run(world, system, 1, new PlayerCommand { Buttons = PlayerButtons.Jump });

        Assert.True(Read(world).Contact.Jumped);
        Assert.True(Read(world).Position.Feet.Y > standing);
        Assert.True(mode.Asked > 30);
    }

    [Fact]
    public void Update_with_a_movement_mode_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0), mode: new Rising { Taking = true });
        world.Create(new CameraPose(), new ActiveCamera());

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world, new Vector3(0, 1.6f, 0));
        world.Create(new CameraPose(), new ActiveCamera());

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
        world.Get<PlayerMode>(Player(world)).Flying = true;
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }

    /// <summary>A movement mode that takes every step (while Jump is held) at 1 m/s straight up with no step, or none.</summary>
    private sealed class Rising : IMovementMode
    {
        public bool Taking;
        public int Asked;
        public Entity Entity = Entity.Null;

        public bool Steer(in MovementModeInput input, ref MovementModeMove move)
        {
            Asked++;
            Entity = input.Entity;
            if (!Taking || (input.Command.Buttons & PlayerButtons.Jump) == 0)
            {
                return false;
            }

            move.Velocity = Vector3.UnitY;
            move.StepMetres = 0;
            return true;
        }
    }

    private static (WorldPoint Eye, float Yaw, float Pitch)? Places(string name) =>
        name == "home" ? (new WorldPoint(-30, 2, 40), 0.5f, -0.25f) : null;

    private static ConsoleCommands NewCommands() =>
        new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);

    private static Entity Player(World world)
    {
        var found = Entity.Null;
        world.Query(new QueryDescription().WithAll<LocalPlayer>(), entity => found = entity);
        return found;
    }

    /// <summary>The local player a game would make: 1.7 m tall with its eye at <paramref name="eye"/>, waiting for the voxels.</summary>
    private static void CreatePlayer(World world, Vector3 eye, float yaw = 0, bool flying = false)
    {
        var size = CharacterSize.ForHeight(1.7f);
        var feet = eye - new Vector3(0, size.EyeMetres, 0);
        world.Create(
            new LocalPlayer(),
            new PlayerCommand { Yaw = yaw },
            new PlayerMode { Flying = flying },
            size,
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            new CharacterVelocity(),
            new CharacterContact { Waiting = true },
            new EyeSmoothing(),
            MovementStats.Default
        );
    }

    private PlayerMovementSystem Create(
        World world,
        Vector3 eye,
        float yaw = 0,
        bool flying = false,
        VoxelCharacterMover? mover = null,
        ConsoleCommands? commands = null,
        Func<string, (WorldPoint Eye, float Yaw, float Pitch)?>? places = null,
        IMovementMode? mode = null,
        PhysicsWorld? physics = null
    )
    {
        var system = new PlayerMovementSystem(
            world,
            mover ?? _mover,
            _area,
            NullLogger<PlayerMovementSystem>.Instance,
            commands,
            places,
            mode,
            physics: physics
        );
        system.Initialize();
        CreatePlayer(world, eye, yaw, flying);
        return system;
    }

    private static void SetStats(World world, MovementStats stats) =>
        world.Query(
            new QueryDescription().WithAll<MovementStats, LocalPlayer>(),
            (ref MovementStats current) => current = stats
        );

    /// <summary>Sets the player's command and runs <paramref name="ticks"/> fixed steps.</summary>
    private static void Run(World world, PlayerMovementSystem system, int ticks, PlayerCommand command)
    {
        for (var i = 0; i < ticks; i++)
        {
            world.Query(
                new QueryDescription().WithAll<PlayerCommand, LocalPlayer>(),
                (ref PlayerCommand current) => current = command
            );
            system.Update(in Step);
        }
    }

    private static (CharacterPosition Position, CharacterVelocity Velocity, CharacterContact Contact) Read(World world)
    {
        var result = default((CharacterPosition, CharacterVelocity, CharacterContact));
        world.Query(
            in s_players,
            (ref CharacterPosition position, ref CharacterVelocity velocity, ref CharacterContact contact) =>
                result = (position, velocity, contact)
        );
        return result;
    }

    private static VoxelWorld LoadWorld(
        IChunkGenerator generator,
        Vector3 centre,
        double radiusMetres = 5,
        int workers = 2
    )
    {
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings
            {
                StreamingRadiusMetres = radiusMetres,
                Levels = 1,
                WorkerThreads = workers,
            },
        };
        var voxels = new VoxelWorld(Options.Create(options), generator, NullLogger<VoxelWorld>.Instance);
        voxels.Initialize();
        voxels.SetCenters([centre]);
        VoxelLoading.WaitUntilLoaded(voxels);

        return voxels;
    }

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Errors.Add(text);
    }
}
