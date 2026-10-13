using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Input;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

public sealed class PlayerCameraSystemTests
{
    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();
    private readonly FakeFrameInput _input = new();

    [Fact]
    public void The_camera_sits_at_the_eye_between_the_last_two_steps()
    {
        using var world = World.Create();
        using var system = Create(world);
        CreatePlayer(
            world,
            flying: false,
            new CharacterPosition { PreviousFeet = Vector3.Zero, Feet = new Vector3(1, 0, 0) },
            new EyeSmoothing { PreviousOffset = -0.2f, Offset = -0.1f },
            new PlayerCommand { Yaw = 0.5f, Pitch = -0.25f }
        );

        system.Update(new FrameTime(1f / 240, 1, 1, 0.25f));

        var pose = ReadCamera(world);
        Assert.Equal(0.25f, pose.Position.X, 5);
        Assert.Equal(1.5f - 0.175f, pose.Position.Y, 5);
        Assert.Equal(0.5f, pose.Yaw);
        Assert.Equal(-0.25f, pose.Pitch);
    }

    /// <summary>The game's one camera is made where it starts, with the options' lens and the fly camera's speed; without a start, none.</summary>
    [Fact]
    public void Initialize_makes_the_games_one_camera_at_its_start()
    {
        using var world = World.Create();
        var start = new CameraPose
        {
            Position = new Vector3(1, 2, 3),
            Yaw = 0.5f,
            Pitch = -0.2f,
        };
        using var system = new PlayerCameraSystem(world, _input, Options.Create(new EngineOptions()), start: start);
        system.Initialize();

        var cameras = new QueryDescription().WithAll<CameraPose, CameraLens, FlyCamera, ActiveCamera>();
        Assert.Equal(1, world.CountEntities(in cameras));
        Assert.Equal(start, ReadCamera(world));
        using var none = World.Create();
        using var without = new PlayerCameraSystem(none, _input, Options.Create(new EngineOptions()));
        without.Initialize();
        Assert.Equal(0, none.CountEntities(in cameras));
    }

    [Fact]
    public void Mouse_movement_since_the_last_step_turns_the_view_only_while_captured()
    {
        using var world = World.Create();
        using var system = Create(world);
        CreatePlayer(world, flying: false, default, default, new PlayerCommand { Yaw = 1 });
        var perPixel = CameraRules.RadiansPerPixel(new EngineOptions());
        _input.MouseSinceTick = new Vector2(10, -5);

        system.Update(new FrameTime(1f / 240, 1, 1, 0));
        var free = ReadCamera(world);
        _input.Snapshot = new InputAccumulator().TakeFrame(true);
        system.Update(new FrameTime(1f / 240, 1, 1, 0));
        var captured = ReadCamera(world);

        Assert.Equal(1, free.Yaw);
        Assert.Equal(1 - 10 * perPixel, captured.Yaw, 5);
        Assert.Equal(5 * perPixel, captured.Pitch, 5);
    }

    /// <summary>While the input is blocked the mouse turns nothing between steps, as the input does nothing at them.</summary>
    [Fact]
    public void While_the_input_is_blocked_the_mouse_does_not_turn_the_view()
    {
        using var world = World.Create();
        var block = new PlayerInputBlock();
        using var system = Create(world, block);
        CreatePlayer(world, flying: false, default, default, new PlayerCommand { Yaw = 1 });
        _input.MouseSinceTick = new Vector2(10, -5);
        _input.Snapshot = new InputAccumulator().TakeFrame(true);

        block.Blocked = true;
        system.Update(new FrameTime(1f / 240, 1, 1, 0));
        Assert.Equal(1, ReadCamera(world).Yaw);
        Assert.Equal(0, ReadCamera(world).Pitch);

        block.Blocked = false;
        system.Update(new FrameTime(1f / 240, 1, 2, 0));
        Assert.NotEqual(1, ReadCamera(world).Yaw);
    }

    [Fact]
    public void A_flying_player_leaves_the_camera_alone()
    {
        using var world = World.Create();
        using var system = Create(world);
        CreatePlayer(world, flying: true, new CharacterPosition { Feet = new Vector3(4, 4, 4) }, default, default);

        system.Update(new FrameTime(1f / 240, 1, 1, 0.5f));

        Assert.Equal(new Vector3(9, 9, 9), ReadCamera(world).Position);
    }

    /// <summary>A bodiless player's eye stays where its body was, and it still looks about.</summary>
    [Fact]
    public void A_bodiless_players_eye_stays_where_it_was_and_it_still_looks_about()
    {
        using var world = World.Create();
        var block = new PlayerInputBlock();
        using var system = Create(world, block);
        var feet = new Vector3(5, 0, 5);
        CreatePlayer(
            world,
            flying: false,
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            default,
            new PlayerCommand { Yaw = 0.5f }
        );
        var player = Player(world);
        world.Add(player, new Bodiless());
        var perPixel = CameraRules.RadiansPerPixel(new EngineOptions());
        _input.MouseSinceTick = new Vector2(10, 0);
        _input.Snapshot = new InputAccumulator().TakeFrame(true);

        system.Update(new FrameTime(1f / 60, 1, 1, 0.5f));
        var pose = ReadCamera(world);
        Assert.Equal(new Vector3(5, 1.5f, 5), pose.Position);
        Assert.Equal(0.5f - 10 * perPixel, pose.Yaw, 5);

        // Flying does not hand it to the fly camera while it has no body to follow it.
        world.Get<PlayerMode>(player).Flying = true;
        system.Update(new FrameTime(1f / 60, 1, 2, 0.5f));
        Assert.Equal(new Vector3(5, 1.5f, 5), ReadCamera(world).Position);

        // Blocked, it looks no further.
        block.Blocked = true;
        system.Update(new FrameTime(1f / 60, 1, 3, 0.5f));
        Assert.Equal(0.5f, ReadCamera(world).Yaw);
    }

    [Fact]
    public void In_third_person_the_camera_sits_behind_the_right_shoulder_and_first_person_at_the_eye()
    {
        using var world = World.Create();
        using var system = Create(world);
        var feet = new Vector3(1, 0, 2);
        CreatePlayer(
            world,
            flying: false,
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            default,
            new PlayerCommand { Yaw = 0, Pitch = -0.2f }
        );
        var player = Player(world);
        world.Add(player, new ThirdPersonView { On = true, Metres = 3 });

        system.Update(new FrameTime(1f / 60, 1, 1, 0.5f));
        var eye = feet + new Vector3(0, 1.5f, 0);
        var behind = ReadCamera(world);
        Assert.Equal(ThirdPersonCamera.Position(null, eye, 0, -0.2f, 3), behind.Position);
        Assert.True(behind.Position.X > eye.X && behind.Position.Z > eye.Z + 2, $"{behind.Position}");
        Assert.Equal(-0.2f, behind.Pitch);

        world.Get<ThirdPersonView>(player).On = false;
        system.Update(new FrameTime(1f / 60, 1, 2, 0.5f));
        Assert.Equal(eye, ReadCamera(world).Position);
    }

    [Fact]
    public void A_look_kick_turns_the_whole_view_behind_the_shoulder_and_leaves_the_command_alone()
    {
        using var world = World.Create();
        using var system = Create(world);
        var feet = new Vector3(1, 0, 2);
        CreatePlayer(
            world,
            flying: false,
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            default,
            new PlayerCommand { Yaw = 0.1f, Pitch = -0.2f }
        );
        var player = Player(world);
        world.Add(player, new ThirdPersonView { On = true, Metres = 3 });
        world.Add(player, new LookKick { Pitch = 0.05f, Yaw = -0.02f });

        system.Update(new FrameTime(1f / 60, 1, 1, 0.5f));

        var camera = ReadCamera(world);
        var eye = feet + new Vector3(0, 1.5f, 0);
        // Sitting behind the shoulder of the kicked look, and pointing along it.
        Assert.Equal(ThirdPersonCamera.Position(null, eye, 0.08f, -0.15f, 3), camera.Position);
        Assert.Equal((0.08f, -0.15f), (camera.Yaw, camera.Pitch));
        Assert.Equal((0.1f, -0.2f), (world.Get<PlayerCommand>(player).Yaw, world.Get<PlayerCommand>(player).Pitch));
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world);
        CreatePlayer(world, flying: false, default, default, default);
        _input.MouseSinceTick = new Vector2(3, 1);

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 1, 0.5f)));
        world.Add(Player(world), new ThirdPersonView { On = true, Metres = 2.5f });
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 2, 0.5f)));
        world.Add(Player(world), new Bodiless());
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 3, 0.5f)));
    }

    private PlayerCameraSystem Create(World world, PlayerInputBlock? block = null)
    {
        var system = new PlayerCameraSystem(world, _input, Options.Create(new EngineOptions()), block: block);
        system.Initialize();
        world.Create(new CameraPose { Position = new Vector3(9, 9, 9) }, new ActiveCamera());
        return system;
    }

    /// <summary>A player 1.63 m tall, whose eye is 1.5 m up.</summary>
    private static void CreatePlayer(
        World world,
        bool flying,
        CharacterPosition position,
        EyeSmoothing eye,
        PlayerCommand command
    ) =>
        world.Create(
            new LocalPlayer(),
            new PlayerMode { Flying = flying },
            command,
            new CharacterSize
            {
                HeightMetres = 1.63f,
                RadiusMetres = 0.25f,
                EyeMetres = 1.5f,
                StepMetres = 0.4f,
            },
            position,
            eye
        );

    private static Entity Player(World world)
    {
        var found = Entity.Null;
        world.Query(new QueryDescription().WithAll<LocalPlayer>(), entity => found = entity);
        return found;
    }

    private static CameraPose ReadCamera(World world)
    {
        var result = default(CameraPose);
        world.Query(in s_camera, (ref CameraPose pose) => result = pose);
        return result;
    }
}
