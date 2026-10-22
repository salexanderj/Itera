using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Input;
using Itera.Engine.Maths;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Systems;

public sealed class DebugFlyCameraSystemTests
{
    private const float Delta = 0.5f;
    private static readonly Vector3 Start = new(1, 2, 3);
    private static readonly QueryDescription s_cameras = new QueryDescription().WithAll<
        CameraPose,
        CameraLens,
        FlyCamera,
        ActiveCamera
    >();

    private readonly FakeFrameInput _input = new();
    private readonly InputAccumulator _events = new();
    private readonly PlayArea _area = new(VoxelGrid.Unit);

    /// <summary>The camera the player's camera system makes, with the options' lens and speed.</summary>
    [Fact]
    public void The_game_has_one_active_camera_from_the_options()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();

        Assert.Equal(1, world.CountEntities(in s_cameras));
        var (pose, lens, fly) = ReadCamera(world);
        Assert.Equal(Start, pose.Position);
        Assert.Equal(0f, pose.Yaw);
        // 90 degrees across a 16:9 screen.
        Assert.Equal(2 * MathF.Atan(9f / 16f), lens.VerticalFovRadians, 5);
        Assert.Equal(10f, fly.SpeedMetresPerSecond);
    }

    [Fact]
    public void W_flies_along_the_view_at_the_current_speed()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();

        _events.KeyDown(Key.W);
        Frame(system);

        // Yaw 0 and pitch 0 look along -Z; 10 m/s for 0.5 s.
        Assert.Equal(Start + new Vector3(0, 0, -5), ReadCamera(world).Pose.Position);
    }

    [Fact]
    public void While_the_input_is_blocked_the_keys_and_the_mouse_do_nothing()
    {
        using var world = World.Create();
        var block = new PlayerInputBlock { Blocked = true };
        using var system = CreateSystem(world, block: block);
        system.Initialize();

        _events.KeyDown(Key.W);
        _events.AddMouseDelta(new Vector2(200, 0));
        Frame(system, captured: true);
        var blocked = ReadCamera(world).Pose;
        block.Blocked = false;
        Frame(system, captured: true);

        Assert.Equal(Start, blocked.Position);
        Assert.Equal(0f, blocked.Yaw);
        Assert.Equal(Start + new Vector3(0, 0, -5), ReadCamera(world).Pose.Position);
    }

    [Fact]
    public void Space_and_c_move_straight_up_and_down_and_shift_is_four_times_faster()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();

        _events.KeyDown(Key.Space);
        _events.KeyDown(Key.ShiftLeft);
        Frame(system);

        Assert.Equal(Start + new Vector3(0, 20, 0), ReadCamera(world).Pose.Position);
    }

    /// <summary>The camera stops at the play area's edge: at 2,000 m/s with no bound, a camera 168 km out made column walks huge.</summary>
    [Fact]
    public void The_camera_stops_at_the_edge_of_the_play_area()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();
        world.Query(in s_cameras, (ref FlyCamera fly) => fly.SpeedMetresPerSecond = DebugFlyCameraSystem.MaxSpeed);

        _events.KeyDown(Key.W);
        _events.KeyDown(Key.ShiftLeft);
        for (var i = 0; i < 10; i++)
        {
            Frame(system);
        }

        var position = ReadCamera(world).Pose.Position;
        Assert.Equal(-(_area.EdgeMetres - PlayArea.EyeInsetMetres), position.Z, 1e-3f);
        Assert.True(_area.HoldsEye(new WorldPoint(position.X, position.Y, position.Z)));
    }

    [Fact]
    public void A_rebound_forward_and_down_fly_and_the_old_keys_do_nothing()
    {
        using var world = World.Create();
        var keys = MovementKeys.Defaults();
        keys.Bindings.Set(keys.Forward, InputBinding.Of(Key.Up));
        keys.Bindings.Set(keys.FlyDown, InputBinding.Of(Key.Q));
        using var system = CreateSystem(world, keys: keys);
        system.Initialize();

        _events.KeyDown(Key.W);
        _events.KeyDown(Key.C);
        Frame(system);
        var still = ReadCamera(world).Pose.Position;
        _events.KeyUp(Key.W);
        _events.KeyUp(Key.C);
        _events.KeyDown(Key.Up);
        _events.KeyDown(Key.Q);
        Frame(system);

        Assert.Equal(Start, still);
        // Forward and down at once: 10 m/s for 0.5 s along the diagonal.
        var moved = ReadCamera(world).Pose.Position - Start;
        Assert.Equal(0, Vector3.Distance(Vector3.Normalize(new Vector3(0, -1, -1)) * 5, moved), 1e-4f);
    }

    [Fact]
    public void Mouse_right_turns_right_only_while_the_pointer_is_captured()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();

        _events.AddMouseDelta(new Vector2(100, 0));
        Frame(system, captured: false);
        Assert.Equal(0f, ReadCamera(world).Pose.Yaw);

        _events.AddMouseDelta(new Vector2(100, 0));
        Frame(system, captured: true);
        // 0.1 degrees per pixel: 10 degrees to the right, which is a negative yaw.
        Assert.Equal(-10 * MathF.PI / 180, ReadCamera(world).Pose.Yaw, 5);

        _events.KeyDown(Key.W);
        Frame(system, captured: true);
        var moved = ReadCamera(world).Pose.Position - Start;
        Assert.True(moved.X > 0, "Turned right and flew forward, so the camera moved towards +X.");
    }

    [Fact]
    public void Pitch_stops_short_of_straight_up_and_mouse_down_looks_down()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();

        _events.AddMouseDelta(new Vector2(0, -10_000));
        Frame(system, captured: true);
        Assert.Equal(CameraRules.MaxPitch, ReadCamera(world).Pose.Pitch);

        _events.AddMouseDelta(new Vector2(0, 20_000));
        Frame(system, captured: true);
        Assert.Equal(-CameraRules.MaxPitch, ReadCamera(world).Pose.Pitch);
    }

    [Fact]
    public void The_wheel_changes_the_speed_within_its_limits()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();

        _events.AddScroll(2);
        Frame(system);
        Assert.Equal(10f * 1.25f * 1.25f, ReadCamera(world).Fly.SpeedMetresPerSecond, 4);

        _events.AddScroll(-100);
        Frame(system);
        Assert.Equal(DebugFlyCameraSystem.MinSpeed, ReadCamera(world).Fly.SpeedMetresPerSecond);
    }

    [Fact]
    public void The_automatic_path_circles_the_origin_looking_along_it()
    {
        using var world = World.Create();
        using var system = CreateSystem(world, walkSpeed: 5);
        system.Initialize();

        // 20 s at 5 m/s is 100 m, one radian on the 100 m circle. Input is ignored.
        _events.KeyDown(Key.W);
        Frame(system, captured: true, totalSeconds: 20);

        var pose = ReadCamera(world).Pose;
        Assert.Equal(100 * MathF.Cos(1), pose.Position.X, 3);
        Assert.Equal((float)new CameraSettings().CircleHeightMetres, pose.Position.Y);
        Assert.Equal(100 * MathF.Sin(1), pose.Position.Z, 3);

        var forward = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(pose.Yaw, 0, 0));
        var alongPath = new Vector3(-MathF.Sin(1), 0, MathF.Cos(1));
        Assert.Equal(1f, Vector3.Dot(forward, alongPath), 4);
    }

    [Fact]
    public void The_automatic_path_stays_on_the_circle()
    {
        foreach (var seconds in new[] { 0d, 17.3, 400, 12_345.6 })
        {
            var pose = DebugFlyCameraSystem.CirclePose(new WorldOrigin(), 5, seconds, 20);
            var flat = new Vector2(pose.Position.X, pose.Position.Z);
            Assert.Equal(100f, flat.Length(), 2);
            Assert.InRange(pose.Yaw, -MathF.PI, MathF.PI);
        }
    }

    [Fact]
    public void The_camera_flies_only_while_the_local_player_flies_it()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();
        var player = world.Create(new PlayerMode { Flying = false }, new LocalPlayer());

        _events.KeyDown(Key.W);
        Frame(system);
        Assert.Equal(Start, ReadCamera(world).Pose.Position);

        world.Set(player, new PlayerMode { Flying = true });
        Frame(system);
        Assert.Equal(Start + new Vector3(0, 0, -5), ReadCamera(world).Pose.Position);
    }

    [Fact]
    public void Update_allocates_nothing_while_steering()
    {
        using var world = World.Create();
        using var system = CreateSystem(world);
        system.Initialize();
        _events.KeyDown(Key.W);
        _events.KeyDown(Key.ShiftLeft);
        _events.AddMouseDelta(new Vector2(3, 2));
        _events.AddScroll(1);
        _input.Snapshot = _events.TakeFrame(true);

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 1, 1, 0)));
    }

    [Fact]
    public void Update_allocates_nothing_on_the_automatic_path()
    {
        using var world = World.Create();
        using var system = CreateSystem(world, walkSpeed: 5);
        system.Initialize();

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FrameTime(1f / 240, 12.5, 1, 0)));
    }

    /// <summary>The fly camera, over the camera made at the start as <see cref="PlayerCameraSystem"/> makes it.</summary>
    private DebugFlyCameraSystem CreateSystem(
        World world,
        double walkSpeed = 0,
        float startYaw = 0,
        MovementKeys? keys = null,
        PlayerInputBlock? block = null
    )
    {
        var options = new EngineOptions { Voxels = new VoxelSettings { DebugWalkSpeed = walkSpeed } };
        CameraRules.CreateCamera(world, new CameraPose { Position = Start, Yaw = startYaw }, options);
        return new(
            world,
            _input,
            Options.Create(options),
            _area,
            Start,
            startYaw,
            NullLogger<DebugFlyCameraSystem>.Instance,
            keys: keys,
            block: block
        );
    }

    private void Frame(DebugFlyCameraSystem system, bool captured = false, double totalSeconds = 0)
    {
        _input.Snapshot = _events.TakeFrame(captured);
        system.Update(new FrameTime(Delta, totalSeconds, 0, 0));
    }

    private static (CameraPose Pose, CameraLens Lens, FlyCamera Fly) ReadCamera(World world)
    {
        var result = default((CameraPose, CameraLens, FlyCamera));
        world.Query(
            in s_cameras,
            (ref CameraPose pose, ref CameraLens lens, ref FlyCamera fly) => result = (pose, lens, fly)
        );
        return result;
    }
}
