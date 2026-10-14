using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

public sealed class CameraExtractSystemTests
{
    private static readonly FrameTime Time = new(1f / 60, 1, 1, 0);
    private readonly RenderView _view = new(Options.Create(new EngineOptions()));

    [Fact]
    public void The_active_camera_becomes_the_view()
    {
        using var world = World.Create();
        using var system = new CameraExtractSystem(world, _view);
        system.Initialize();
        var pose = new CameraPose
        {
            Position = new Vector3(12, 3, -40),
            Yaw = 0.5f,
            Pitch = -0.25f,
        };
        world.Create(pose, new CameraLens { VerticalFovRadians = 1.2f, NearMetres = 0.1f }, new ActiveCamera());
        world.Create(new CameraPose { Position = new Vector3(999, 0, 0) }, new CameraLens());

        system.Update(in Time);

        var view = _view.Camera;
        Assert.Equal(pose.Position, view.Position);
        Assert.Equal(1.2f, view.VerticalFovRadians);
        Assert.Equal(0.1f, view.NearMetres);
        var expected = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(0.5f, -0.25f, 0));
        Assert.True(Vector3.Distance(expected, view.Forward) < 1e-5f);
    }

    [Fact]
    public void An_orthographic_lens_keeps_its_size_and_depths_and_the_players_field_of_view_draws_only_perspective()
    {
        using var world = World.Create();
        var settings = new PlayerSettings(
            new EngineOptions(),
            CommandLineKeys.None,
            null,
            NullLogger<PlayerSettings>.Instance
        );
        using var system = new CameraExtractSystem(world, _view, settings);
        system.Initialize();
        var camera = world.Create(
            new CameraPose { Position = new Vector3(4, 60, -2), Pitch = -MathF.PI / 2 },
            CameraLens.Orthographic(64, -100, 100),
            new ActiveCamera()
        );

        system.Update(in Time);

        var view = _view.Camera;
        Assert.True(view.Orthographic);
        Assert.Equal(new Vector3(4, 60, -2), view.Position);
        Assert.Equal((64f, -100f, 100f), (view.HeightMetres, view.NearMetres, view.FarMetres));
        // The player's field of view is not put on it.
        Assert.Equal(0f, view.VerticalFovRadians);
        Assert.True(Vector3.Distance(-Vector3.UnitY, view.Forward) < 1e-5f);

        world.Set(camera, new CameraLens { VerticalFovRadians = 1.2f, NearMetres = 0.1f });
        system.Update(in Time);

        Assert.False(_view.Camera.Orthographic);
        Assert.Equal(settings.VerticalFieldOfViewRadians, _view.Camera.VerticalFovRadians);
    }

    /// <summary>A perspective lens's zoom draws that many times closer than the player's field of view; an orthographic lens keeps its size.</summary>
    [Fact]
    public void A_lens_zoom_draws_closer_than_the_players_field_of_view()
    {
        using var world = World.Create();
        var settings = new PlayerSettings(
            new EngineOptions(),
            CommandLineKeys.None,
            null,
            NullLogger<PlayerSettings>.Instance
        );
        using var system = new CameraExtractSystem(world, _view, settings);
        system.Initialize();
        var camera = world.Create(
            new CameraPose(),
            new CameraLens
            {
                VerticalFovRadians = 1.2f,
                NearMetres = 0.1f,
                Zoom = 2,
            },
            new ActiveCamera()
        );

        system.Update(in Time);
        var zoomed = _view.Camera.VerticalFovRadians;
        world.Get<CameraLens>(camera).Zoom = 1;
        system.Update(in Time);
        var plain = _view.Camera.VerticalFovRadians;
        world.Set(camera, CameraLens.Orthographic(64, -100, 100) with { Zoom = 2 });
        system.Update(in Time);

        var field = settings.VerticalFieldOfViewRadians;
        Assert.Equal(field, plain);
        Assert.Equal(MathF.Tan(field / 2) / 2, MathF.Tan(zoomed / 2), 1e-5f);
        Assert.Equal((64f, 0f), (_view.Camera.HeightMetres, _view.Camera.VerticalFovRadians));
    }

    /// <summary>
    /// A zoom below 1 draws wider than the player's field of view, by the same rule, down to the least zoom; 0 is no zoom.
    /// </summary>
    [Fact]
    public void A_lens_zoom_below_1_draws_wider_down_to_the_least()
    {
        using var world = World.Create();
        var settings = new PlayerSettings(
            new EngineOptions(),
            CommandLineKeys.None,
            null,
            NullLogger<PlayerSettings>.Instance
        );
        using var system = new CameraExtractSystem(world, _view, settings);
        system.Initialize();
        var camera = world.Create(
            new CameraPose(),
            new CameraLens
            {
                VerticalFovRadians = 1.2f,
                NearMetres = 0.1f,
                Zoom = 0.8f,
            },
            new ActiveCamera()
        );

        system.Update(in Time);
        var wider = _view.Camera.VerticalFovRadians;
        world.Get<CameraLens>(camera).Zoom = 0.1f;
        system.Update(in Time);
        var widest = _view.Camera.VerticalFovRadians;
        world.Get<CameraLens>(camera).Zoom = 0;
        system.Update(in Time);
        var plain = _view.Camera.VerticalFovRadians;

        var field = settings.VerticalFieldOfViewRadians;
        Assert.Equal(field, plain);
        Assert.Equal(MathF.Tan(field / 2) / 0.8f, MathF.Tan(wider / 2), 1e-4f);
        Assert.Equal(MathF.Tan(field / 2) / CameraLens.LeastZoom, MathF.Tan(widest / 2), 1e-4f);
    }

    /// <summary>A pose's roll turns the view about the way it looks: its forward stays, its up tips over.</summary>
    [Fact]
    public void A_poses_roll_turns_the_view_about_its_forward()
    {
        using var world = World.Create();
        using var system = new CameraExtractSystem(world, _view);
        system.Initialize();
        var camera = world.Create(
            new CameraPose { Yaw = 0.5f, Roll = 0.3f },
            new CameraLens { VerticalFovRadians = 1.2f, NearMetres = 0.1f },
            new ActiveCamera()
        );

        system.Update(in Time);
        var rolled = _view.Camera;
        world.Get<CameraPose>(camera).Roll = float.NaN;
        system.Update(in Time);
        var broken = _view.Camera;

        var level = Quaternion.CreateFromYawPitchRoll(0.5f, 0, 0);
        Assert.True(Vector3.Distance(Vector3.Transform(-Vector3.UnitZ, level), rolled.Forward) < 1e-5f);
        var up = Vector3.Transform(Vector3.UnitY, level);
        Assert.Equal(MathF.Cos(0.3f), Vector3.Dot(up, rolled.Up), 1e-5f);
        // A roll that is not a number draws level.
        Assert.True(Vector3.Distance(up, broken.Up) < 1e-5f);
    }

    [Fact]
    public void Without_an_active_camera_nothing_changes()
    {
        using var world = World.Create();
        using var system = new CameraExtractSystem(world, _view);
        system.Initialize();
        world.Create(new CameraPose { Position = new Vector3(5, 5, 5) }, new CameraLens());

        system.Update(in Time);

        Assert.Equal(CameraView.Default, _view.Camera);
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        using var world = World.Create();
        using var system = new CameraExtractSystem(world, _view);
        system.Initialize();
        world.Create(new CameraPose(), new CameraLens(), new ActiveCamera());

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Time));
    }
}
