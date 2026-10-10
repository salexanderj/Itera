using System.Numerics;
using Itera.Engine.Matches;
using Itera.Engine.Rendering;
using Itera.MeshSample.Systems;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The yard camera (<see cref="YardCamera"/>, <see cref="YardCameraSystem"/>): declared at the run's start and shown on the
/// screen's material; asked for each frame from its fixed camera, high over the yard's south-west corner, looking at
/// its middle; a frame allocates nothing.
/// </summary>
public sealed class YardCameraSystemTests
{
    private static readonly FrameTime Time = new(1f / 60, 1, 1, 0);

    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
    };

    [Fact]
    public void The_view_is_declared_shown_on_the_screen_and_asked_for_from_over_the_yard()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(2);
        var camera = provider.GetRequiredService<YardCamera>();
        var views = provider.GetRequiredService<RenderViews>();

        var info = views.Info(camera.View);
        Assert.Equal((YardCamera.ViewName, YardCamera.Width, YardCamera.Height), (info.Name, info.Width, info.Height));
        Assert.Equal(camera.View, views.ViewOnMaterial(Yard.Screen, Yard.ScreenMaterial));
        Assert.True(views.Asked(camera.View, out var asked));
        Assert.Equal(YardCamera.Camera, asked);
        Assert.False(asked.Orthographic);
        // It looks from over the south-west corner down at the middle (0, 0, -3).
        var towards = Vector3.Normalize(new Vector3(0, 0, -3) - YardCamera.Position);
        Assert.True(Vector3.Distance(towards, asked.Forward) < 1e-4f, $"Forward {asked.Forward}, expected {towards}.");
        Assert.True(Vector3.Dot(asked.Up, Vector3.UnitY) > 0.8f);
    }

    [Fact]
    public void A_frame_allocates_nothing()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        using var system = new YardCameraSystem(
            match.World,
            provider.GetRequiredService<YardCamera>(),
            provider.GetRequiredService<RenderViews>()
        );

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Time));
    }
}
