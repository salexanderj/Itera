using System.Numerics;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Input;
using Itera.Engine.Networking;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Systems;

public sealed class PlayerInputSystemTests
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private static readonly QueryDescription s_players = new QueryDescription().WithAll<
        PlayerCommand,
        PlayerMode,
        LocalPlayer
    >();

    private readonly FakeTickInput _input = new();
    private readonly InputAccumulator _events = new();

    [Fact]
    public void W_and_d_move_forward_and_right_at_most_at_full_speed()
    {
        using var world = World.Create();
        using var system = Create(world);

        _events.KeyDown(Key.W);
        _events.KeyDown(Key.D);
        Tick(system);

        var move = Read(world).Command.Move;
        Assert.Equal(1, move.Length(), 5);
        Assert.Equal(move.X, move.Y, 5);
        Assert.True(move.X > 0);
    }

    [Fact]
    public void Control_held_crouches_with_the_pointer_free_or_captured()
    {
        using var world = World.Create();
        using var system = Create(world);

        _events.KeyDown(Key.ControlLeft);
        Tick(system, captured: false);
        var free = Read(world).Command.Buttons;
        Tick(system, captured: true);
        var captured = Read(world).Command.Buttons;
        _events.KeyUp(Key.ControlLeft);
        _events.KeyDown(Key.ControlRight);
        Tick(system, captured: true);
        var right = Read(world).Command.Buttons;
        _events.KeyUp(Key.ControlRight);
        Tick(system, captured: true);
        var up = Read(world).Command.Buttons;

        Assert.Equal(PlayerButtons.Crouch, free);
        Assert.Equal(PlayerButtons.Crouch, captured);
        Assert.Equal(PlayerButtons.Crouch, right);
        Assert.Equal(PlayerButtons.None, up);
    }

    [Fact]
    public void Shift_sprints_whether_or_not_the_pointer_is_captured()
    {
        using var world = World.Create();
        using var system = Create(world);

        _events.KeyDown(Key.ShiftLeft);
        Tick(system, captured: false);
        var free = Read(world).Command.Buttons;
        Tick(system, captured: true);
        var captured = Read(world).Command.Buttons;

        Assert.Equal(PlayerButtons.Sprint, free);
        Assert.Equal(PlayerButtons.Sprint, captured);
    }

    [Fact]
    public void Tab_switches_to_third_person_and_back_and_the_wheel_moves_the_camera_only_there()
    {
        using var world = World.Create();
        using var system = Create(world);
        var player = Player(world);
        world.Add(player, new ThirdPersonView { Metres = ThirdPersonCamera.StartMetres });

        _events.AddScroll(2);
        Tick(system);
        Assert.False(world.Get<ThirdPersonView>(player).On);
        Assert.Equal(ThirdPersonCamera.StartMetres, world.Get<ThirdPersonView>(player).Metres);

        _events.KeyDown(Key.Tab);
        Tick(system);
        Assert.True(world.Get<ThirdPersonView>(player).On);
        // Held, it does not switch again.
        Tick(system);
        Assert.True(world.Get<ThirdPersonView>(player).On);

        // Away from the user, nearer; towards, farther; never past the limits.
        _events.AddScroll(2);
        Tick(system);
        Assert.Equal(
            ThirdPersonCamera.StartMetres - 2 * ThirdPersonCamera.WheelStepMetres,
            world.Get<ThirdPersonView>(player).Metres,
            4
        );
        _events.AddScroll(-100);
        Tick(system);
        Assert.Equal(ThirdPersonCamera.MostMetres, world.Get<ThirdPersonView>(player).Metres);

        _events.KeyUp(Key.Tab);
        Tick(system);
        _events.KeyDown(Key.Tab);
        Tick(system);
        Assert.False(world.Get<ThirdPersonView>(player).On);

        // Flying, Tab does nothing.
        world.Get<PlayerMode>(player).Flying = true;
        _events.KeyUp(Key.Tab);
        Tick(system);
        _events.KeyDown(Key.Tab);
        Tick(system);
        Assert.False(world.Get<ThirdPersonView>(player).On);
    }

    /// <summary>
    /// A game that leaves the third-person key out (its camera always over the shoulder): Tab does nothing and the
    /// Keybindings tab lists no such action, while the wheel and the console's <c>camera</c> still work.
    /// </summary>
    [Fact]
    public void Without_the_third_person_key_tab_does_nothing()
    {
        using var world = World.Create();
        var bindings = new KeyBindings();
        var keys = new MovementKeys(bindings, thirdPersonKey: false);
        var commands = NewCommands();
        using var system = Create(world, commands: commands, keys: keys);
        var player = Player(world);
        world.Add(player, new ThirdPersonView { On = true, Metres = ThirdPersonCamera.StartMetres });

        _events.KeyDown(Key.Tab);
        Tick(system);
        var afterTab = world.Get<ThirdPersonView>(player).On;
        _events.AddScroll(2);
        Tick(system);
        var metres = world.Get<ThirdPersonView>(player).Metres;
        commands.Execute("camera first", new RecordingOutput());
        Tick(system);

        Assert.Null(keys.ThirdPerson);
        Assert.DoesNotContain(bindings.Rows, row => row.Label == "Third person");
        Assert.True(afterTab);
        Assert.Equal(ThirdPersonCamera.StartMetres - 2 * ThirdPersonCamera.WheelStepMetres, metres, 4);
        Assert.False(world.Get<ThirdPersonView>(player).On);
    }

    [Fact]
    public void The_camera_command_switches_the_view_and_says_where_it_is()
    {
        using var world = World.Create();
        var commands = NewCommands();
        var output = new RecordingOutput();
        using var system = Create(world, commands: commands);
        var player = Player(world);
        world.Add(player, new ThirdPersonView { Metres = 3 });

        commands.Execute("camera third", output);
        Tick(system);
        Assert.True(world.Get<ThirdPersonView>(player).On);
        commands.Execute("camera", output);
        commands.Execute("camera first", output);
        Tick(system);
        Assert.False(world.Get<ThirdPersonView>(player).On);

        Assert.Contains("camera third, 3 m behind the shoulder", output.Lines);
        Assert.Empty(output.Errors);
    }

    /// <summary>While <see cref="PlayerInputBlock.Blocked"/> the input does nothing; unblocked, the keys held count once more.</summary>
    [Fact]
    public void While_the_input_is_blocked_it_does_nothing()
    {
        using var world = World.Create();
        var block = new PlayerInputBlock();
        using var system = Create(world, block: block);
        block.Blocked = true;

        _events.KeyDown(Key.W);
        _events.KeyDown(Key.Space);
        _events.KeyDown(Key.ShiftLeft);
        _events.AddMouseDelta(new Vector2(10, 4));
        Tick(system, captured: true);
        var command = Read(world).Command;
        Assert.Equal(Vector2.Zero, command.Move);
        Assert.Equal(PlayerButtons.None, command.Buttons);
        Assert.Equal(0, command.Yaw);

        block.Blocked = false;
        Tick(system, captured: true);
        command = Read(world).Command;
        Assert.NotEqual(Vector2.Zero, command.Move);
        Assert.Equal(PlayerButtons.Sprint, command.Buttons);
    }

    /// <summary>A bodiless player only looks about: its move and buttons clear, and the game's actions stay.</summary>
    [Fact]
    public void A_bodiless_player_only_looks_about()
    {
        using var world = World.Create();
        using var system = Create(world);
        var player = Player(world);
        world.Add(player, new Bodiless());
        world.Get<PlayerCommand>(player).Actions = 5;
        var perPixel = CameraRules.RadiansPerPixel(new EngineOptions());

        _events.KeyDown(Key.W);
        _events.KeyDown(Key.Space);
        _events.AddMouseDelta(new Vector2(10, 4));
        Tick(system, captured: true);

        var command = Read(world).Command;
        Assert.Equal(Vector2.Zero, command.Move);
        Assert.Equal(PlayerButtons.None, command.Buttons);
        Assert.Equal(-10 * perPixel, command.Yaw, 5);
        Assert.Equal(-4 * perPixel, command.Pitch, 5);
        Assert.Equal(5u, command.Actions);
    }

    [Fact]
    public void Actions_a_game_set_are_left_alone()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Get<PlayerCommand>(Player(world)).Actions = 0b1010;

        _events.KeyDown(Key.W);
        Tick(system);

        Assert.Equal(0b1010u, Read(world).Command.Actions);
    }

    [Fact]
    public void Holding_space_jumps_once_and_each_press_jumps_again()
    {
        using var world = World.Create();
        using var system = Create(world);

        _events.KeyDown(Key.Space);
        Tick(system);
        var pressed = Read(world).Command.Buttons;
        Tick(system);
        var held = Read(world).Command.Buttons;
        _events.KeyUp(Key.Space);
        _events.KeyDown(Key.Space);
        Tick(system);
        var again = Read(world).Command.Buttons;

        Assert.Equal(PlayerButtons.Jump, pressed);
        Assert.Equal(PlayerButtons.None, held);
        Assert.Equal(PlayerButtons.Jump, again);
    }

    [Fact]
    public void The_mouse_turns_the_look_only_while_the_pointer_is_captured()
    {
        using var world = World.Create();
        using var system = Create(world);
        var perPixel = CameraRules.RadiansPerPixel(new EngineOptions());

        _events.AddMouseDelta(new Vector2(10, 4));
        Tick(system, captured: false);
        Assert.Equal(0, Read(world).Command.Yaw);

        _events.AddMouseDelta(new Vector2(10, 4));
        Tick(system, captured: true);
        var command = Read(world).Command;

        // Mouse right turns right (yaw goes down); mouse down looks down.
        Assert.Equal(-10 * perPixel, command.Yaw, 5);
        Assert.Equal(-4 * perPixel, command.Pitch, 5);
    }

    [Fact]
    public void A_rebound_jump_and_move_work_and_the_old_keys_do_nothing()
    {
        using var world = World.Create();
        var keys = MovementKeys.Defaults();
        keys.Bindings.Set(keys.Jump, InputBinding.Of(Key.J));
        keys.Bindings.Set(keys.Forward, InputBinding.Of(Key.Up));
        using var system = Create(world, keys: keys);

        _events.KeyDown(Key.Space);
        _events.KeyDown(Key.W);
        Tick(system, captured: true);
        var old = Read(world).Command;
        _events.KeyUp(Key.Space);
        _events.KeyUp(Key.W);
        _events.KeyDown(Key.J);
        _events.KeyDown(Key.Up);
        Tick(system, captured: true);
        var rebound = Read(world).Command;

        Assert.Equal(PlayerButtons.None, old.Buttons);
        Assert.Equal(Vector2.Zero, old.Move);
        Assert.Equal(PlayerButtons.Jump, rebound.Buttons);
        Assert.Equal(new Vector2(0, 1), rebound.Move);
    }

    [Fact]
    public void Pitch_stops_short_of_straight_down()
    {
        using var world = World.Create();
        using var system = Create(world);

        _events.AddMouseDelta(new Vector2(0, 100_000));
        Tick(system, captured: true);

        Assert.Equal(-CameraRules.MaxPitch, Read(world).Command.Pitch);
    }

    [Fact]
    public void Fly_switches_to_flying_which_takes_the_look_and_place_from_the_camera_and_back_and_F1_does_not()
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, commands: commands);
        world.Create(
            new CameraPose
            {
                Position = new Vector3(3, 12, -7),
                Yaw = 1.25f,
                Pitch = -0.5f,
            },
            new ActiveCamera()
        );

        _events.KeyDown(Key.F1);
        Tick(system);
        var stillWalking = Read(world).Mode.Flying;
        _events.KeyUp(Key.F1);

        commands.Execute("fly on", new RecordingOutput());
        _events.KeyDown(Key.W);
        Tick(system);
        var flying = Read(world);

        commands.Execute("fly off", new RecordingOutput());
        Tick(system);
        var walking = Read(world);

        Assert.False(stillWalking);

        Assert.True(flying.Mode.Flying);
        Assert.Equal(Vector2.Zero, flying.Command.Move);
        Assert.Equal(1.25f, flying.Command.Yaw);
        Assert.Equal(-0.5f, flying.Command.Pitch);
        // The command carries where the camera is, which a client sends its host.
        Assert.Equal(PlayerButtons.Fly, flying.Command.Buttons & PlayerButtons.Fly);
        Assert.Equal(new Vector3(3, 12, -7), flying.Command.Viewpoint);
        Assert.False(walking.Mode.Flying);
        Assert.Equal(PlayerButtons.None, walking.Command.Buttons & PlayerButtons.Fly);
        Assert.Equal(new Vector2(0, 1), walking.Command.Move);
        Assert.Equal(1.25f, walking.Command.Yaw);
    }

    [Fact]
    public void Forced_flying_refuses_fly_off()
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, forceFlying: true, commands: commands);
        var output = new RecordingOutput();

        commands.Execute("fly off", output);
        Tick(system);

        Assert.Single(output.Errors);
        Assert.True(Read(world).Mode.Flying);
    }

    [Fact]
    public void A_games_request_flies_and_walks_without_the_fly_command_and_forced_flying_refuses_it()
    {
        using var world = World.Create();
        var commands = NewCommands();
        using var system = Create(world, commands: commands, flyCommand: false);
        world.Create(new CameraPose { Position = new Vector3(1, 5, 2) }, new ActiveCamera());
        using var forcedWorld = World.Create();
        using var forced = Create(forcedWorld, forceFlying: true);

        var asked = system.RequestFlying(true);
        Tick(system);
        var flying = Read(world);
        system.RequestFlying(false);
        Tick(system);
        var walking = Read(world).Mode.Flying;

        Assert.False(commands.TryGet("fly", out _));
        Assert.True(commands.TryGet("camera", out _));
        Assert.True(asked);
        Assert.True(flying.Mode.Flying);
        Assert.Equal(new Vector3(1, 5, 2), flying.Command.Viewpoint);
        Assert.False(walking);
        Assert.False(forced.RequestFlying(false));
    }

    [Fact]
    public void Starting_flying_waits_for_the_first_local_player()
    {
        using var world = World.Create();
        using var system = new PlayerInputSystem(
            world,
            _input,
            Options.Create(new EngineOptions { Camera = { StartFlying = true } }),
            false,
            NullLogger<PlayerInputSystem>.Instance
        );
        system.Initialize();

        Tick(system);
        world.Create(new PlayerCommand(), new PlayerMode(), new LocalPlayer());
        Tick(system);
        var started = Read(world).Mode.Flying;

        Assert.True(started);
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world);
        world.Create(new CameraPose(), new ActiveCamera());
        world.Add(Player(world), new ThirdPersonView { Metres = 2.5f });
        _events.KeyDown(Key.W);
        _events.KeyDown(Key.Space);
        _events.AddMouseDelta(new Vector2(3, 2));
        _events.AddScroll(1);
        _input.Snapshot = _events.TakeTick(true);

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
        world.Add(Player(world), new Bodiless());
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }

    private static ConsoleCommands NewCommands() =>
        new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);

    private PlayerInputSystem Create(
        World world,
        bool forceFlying = false,
        PlayerInputBlock? block = null,
        ConsoleCommands? commands = null,
        MovementKeys? keys = null,
        bool flyCommand = true
    )
    {
        var system = new PlayerInputSystem(
            world,
            _input,
            Options.Create(new EngineOptions()),
            forceFlying,
            NullLogger<PlayerInputSystem>.Instance,
            commands,
            block: block,
            keys: keys,
            flyCommand: flyCommand
        );
        system.Initialize();
        world.Create(new PlayerCommand(), new PlayerMode(), new LocalPlayer());
        return system;
    }

    private static Entity Player(World world)
    {
        var found = Entity.Null;
        world.Query(new QueryDescription().WithAll<LocalPlayer>(), entity => found = entity);
        return found;
    }

    private void Tick(PlayerInputSystem system, bool captured = false)
    {
        _input.Snapshot = _events.TakeTick(captured);
        system.Update(in Step);
    }

    private static (PlayerCommand Command, PlayerMode Mode) Read(World world)
    {
        var result = default((PlayerCommand, PlayerMode));
        world.Query(in s_players, (ref PlayerCommand command, ref PlayerMode mode) => result = (command, mode));
        return result;
    }

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Errors.Add(text);
    }
}
