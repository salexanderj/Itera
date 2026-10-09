using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Rendering;
using Itera.Engine.Systems;
using Itera.Modding;
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
