using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// <see cref="MeshView"/>: the frame's frustum, the two shadow cascades and the slot's lighting record, and the point
/// lights kept and ranked for the slot's list, over a draw target in memory.
/// </summary>
public sealed class MeshViewTests
{
    private readonly FakeTarget _target = new();

    [Fact]
    public void A_cube_in_front_is_in_view_and_one_behind_beside_or_past_the_distance_is_not()
    {
        var view = NewView();
        view.Begin(0, LookingAlongMinusZ());

        Assert.True(view.InView(new Vector3(0, 0, -10), 1));
        Assert.True(view.InView(new Vector3(10.4f, 0, -10), 1));
        Assert.False(view.InView(new Vector3(0, 0, 10), 1));
        Assert.False(view.InView(new Vector3(0, 0, -150), 1));
        Assert.False(view.InView(new Vector3(30, 0, -10), 1));
        Assert.False(view.InView(new Vector3(0, -30, -10), 1));
    }

    [Fact]
    public void Begin_fits_both_cascades_and_writes_the_slot_record_with_no_lights()
    {
        var view = NewView();
        _target.LightingOf(1).LightCount = 7;

        view.Begin(1, LookingAlongMinusZ(ViewSwitches.Shadows));

        ref readonly var lighting = ref _target.LightingOf(1);
        Assert.True(view.Shadows);
        Assert.Equal(1u, lighting.Shadows);
        Assert.Equal(MeshView.NearShadowMetres, lighting.NearEnd);
        Assert.Equal(100, lighting.FarEnd);
        Assert.Equal(MeshView.NearShadowMetres - MeshView.NearBlendMetres, lighting.NearBlendStart);
        Assert.Equal(0u, lighting.LightCount);
        Assert.True(view.NearCascade.Holds(new Vector3(0, 0, -10), 1));
        Assert.True(view.FarCascade.Holds(new Vector3(0, 0, -60), 1));
        Assert.False(view.NearCascade.Holds(new Vector3(500, 0, -10), 1));
    }

    [Fact]
    public void The_budget_keeps_the_lights_brightest_at_the_camera_brightest_first_events_last()
    {
        var view = NewView(mostLights: 2);
        view.Begin(0, LookingAlongMinusZ());

        // Ranked by power over the squared distance plus one: 3 / 5, then 10 / 26, then 100 / 401.
        view.Offer(new Vector3(0, 0, -5), Vector3.UnitX, 10);
        view.Offer(new Vector3(0, 0, -20), Vector3.UnitY, 100);
        // Past the gathering distance, and behind the camera beyond its reach: not kept.
        view.Offer(new Vector3(0, 0, -60), Vector3.One, 1000);
        view.Offer(new Vector3(0, 0, 30), Vector3.One, 1);
        view.End(0, [new PointLight(new Vector3(0, 0, -2), Vector3.UnitZ, 3)]);

        Assert.Equal(2u, _target.LightingOf(0).LightCount);
        Assert.Equal(2, view.LastLights);
        Assert.Equal(1, view.LastEventLights);
        Assert.Equal(3, _target.LightsOf(0)[0].Power);
        Assert.Equal(Vector3.UnitZ, _target.LightsOf(0)[0].Colour);
        Assert.Equal(10, _target.LightsOf(0)[1].Power);
    }

    [Fact]
    public void A_light_reaches_where_its_power_falls_to_the_cutoff_at_most_16_metres()
    {
        var view = NewView();
        view.Begin(0, LookingAlongMinusZ());

        view.Offer(new Vector3(0, 0, -5), Vector3.One, 0.5f);
        view.Offer(new Vector3(0, 0, -6), Vector3.One, 1000);
        view.End(0, []);

        // Brightest at the camera first.
        Assert.Equal(16, _target.LightsOf(0)[0].Reach);
        Assert.Equal(5, _target.LightsOf(0)[1].Reach, 4);
    }

    [Fact]
    public void A_frame_allocates_nothing()
    {
        var view = NewView();
        var constants = LookingAlongMinusZ(ViewSwitches.Shadows);
        PointLight[] events = [new(new Vector3(1, 0, -4), Vector3.One, 5)];
        for (var i = 0; i < 100; i++)
        {
            Frame(view, in constants, events);
        }

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                Frame(view, in constants, events);
            }
        });

        Assert.Equal(0, bytes);
    }

    private static void Frame(MeshView view, in ViewConstants constants, PointLight[] events)
    {
        view.Begin(1, in constants);
        view.Offer(new Vector3(0, 0, -5), Vector3.One, 10);
        view.InView(new Vector3(0, 0, -10), 1);
        view.NearCascade.Holds(new Vector3(0, 0, -10), 1);
        view.End(1, events);
    }

    private MeshView NewView(int mostLights = 128) =>
        new(_target, Options.Create(new EngineOptions { Renderer = new RendererSettings { MostLights = mostLights } }));

    private static ViewConstants LookingAlongMinusZ(ViewSwitches switches = ViewSwitches.None) =>
        new()
        {
            Position = Vector3.Zero,
            Forward = new Vector3(0, 0, -1),
            Right = new Vector3(1, 0, 0),
            Up = new Vector3(0, 1, 0),
            SunDirection = ViewConstants.DefaultSunDirection,
            MaxDistance = 100,
            Switches = switches,
        };

    /// <summary>The lighting records and point lights of two slots; the view writes nothing else.</summary>
    private sealed class FakeTarget : IMeshDrawTarget
    {
        private readonly MeshLighting[] _lighting = new MeshLighting[FrameSync.FramesInFlight];
        private readonly MeshLight[] _lights = new MeshLight[FrameSync.FramesInFlight * MeshPass.MostLights];

        public ulong HeapBytes => 0;

        public ref MeshLighting LightingOf(int slot) => ref _lighting[slot];

        public Span<MeshLight> LightsOf(int slot) => _lights.AsSpan(slot * MeshPass.MostLights, MeshPass.MostLights);

        public Span<MeshDraw> DrawsOf(int slot) => throw new NotSupportedException();

        public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) => throw new NotSupportedException();

        public Span<MeshModelDraw> ModelDrawsOf(int slot, int list) => throw new NotSupportedException();

        public Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list) =>
            throw new NotSupportedException();
    }
}
