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

    /// <summary>
    /// An orthographic view from 50 m up, looking down with north up: it sees its box (64 m across, 36 m high, 100 m each
    /// way along the view from its middle), and its cascades lie round its middle, both ways along the view.
    /// </summary>
    [Fact]
    public void An_orthographic_view_sees_its_box_and_fits_its_cascades_round_its_middle()
    {
        var view = NewView();

        view.Begin(0, LookingDown(ViewSwitches.Shadows));

        Assert.True(view.Orthographic);
        Assert.True(view.InView(new Vector3(30, 0, 0), 1));
        Assert.True(view.InView(new Vector3(30, 50, 17), 1));
        Assert.True(view.InView(new Vector3(0, 120, 0), 1));
        Assert.False(view.InView(new Vector3(40, 0, 0), 1));
        Assert.False(view.InView(new Vector3(0, 0, 25), 1));
        Assert.False(view.InView(new Vector3(0, -60, 0), 1));
        Assert.False(view.InView(new Vector3(0, 160, 0), 1));
        ref readonly var lighting = ref _target.LightingOf(0);
        Assert.Equal(1u, lighting.Shadows);
        Assert.Equal(MeshView.NearShadowMetres, lighting.NearEnd);
        Assert.True(view.NearCascade.Holds(new Vector3(20, 60, 0), 1));
        Assert.True(view.NearCascade.Holds(new Vector3(20, 30, 0), 1));
        Assert.True(view.FarCascade.Holds(new Vector3(20, -40, 0), 1));
        // Behind the middle, where a slice from the middle on (as a perspective view's) would not reach.
        Assert.True(view.NearCascade.Holds(new Vector3(-30, 78, -16), 1));
        Assert.False(view.NearCascade.Holds(new Vector3(20, 10, 0), 1));
        // Past the box's side towards the sun (the side planes lie the half width out).
        Assert.False(view.NearCascade.Holds(new Vector3(-40, 60, 0), 1));
        // Nearest first by the depth past the box's near end.
        Assert.Equal(0f, view.NearFirstKey(new Vector3(0, 200, 0)));
        Assert.Equal(150f, view.NearFirstKey(new Vector3(5, 0, 5)), 3);
    }

    /// <summary>
    /// An orthographic box that misses a cascade's slice (it starts 40 m past its middle), or lies far along the view,
    /// still fits both cascades; nothing off the box casts into them.
    /// </summary>
    [Theory]
    [InlineData(40f, 100f)]
    [InlineData(99_000f, 99_100f)]
    public void An_orthographic_box_away_from_its_middle_still_fits_its_cascades(float near, float far)
    {
        var view = NewView();

        view.Begin(0, LookingDown(ViewSwitches.Shadows) with { Near = near, Far = far });

        Assert.False(view.NearCascade.Holds(new Vector3(0, 50 - near - 200, 0), 1));
        Assert.True(view.InView(new Vector3(0, 50 - near - 10, 0), 1));
        Assert.False(view.InView(new Vector3(0, 50, 0), 1));
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
    public void Casters_reach_farther_as_the_sun_sinks()
    {
        Assert.Equal(MeshView.CasterMetres, MeshView.CasterReach(ViewConstants.DefaultSunDirection));
        Assert.Equal(MeshView.CasterMetres, MeshView.CasterReach(SunPath.FromAngles(10, 30)), 3);
        // A caster as tall at 15 degrees: 100 m times tan 30 over tan 15.
        Assert.Equal(215.47f, MeshView.CasterReach(SunPath.FromAngles(10, 15)), 1);
        Assert.Equal(MeshView.MostCasterMetres, MeshView.CasterReach(SunPath.FromAngles(10, 5)));
        Assert.Equal(MeshView.MostCasterMetres, MeshView.CasterReach(SunPath.FromAngles(10, -5)));
    }

    /// <summary>
    /// Without <c>Engine:Renderer:DistantShadowMetres</c>, or under the engine's sun (58 degrees up), no distant cascade is
    /// drawn and its part of the record is zero, so mesh.frag never reads its layer; the far cascade ends at 200 m.
    /// </summary>
    [Fact]
    public void The_distant_cascade_is_left_out_by_default_and_under_a_high_sun()
    {
        var constants = LookingAlongMinusZ(ViewSwitches.Shadows) with { MaxDistance = 3000 };
        var view = NewView();

        view.Begin(0, in constants);

        Assert.False(view.DistantShadows);
        Assert.Equal(0, _target.LightingOf(0).DistantStrength);
        Assert.Equal(0, _target.LightingOf(0).DistantEnd);

        var withOption = NewView(distantMetres: 2000);
        withOption.Begin(0, in constants);

        Assert.False(withOption.DistantShadows);
        Assert.Equal(0, _target.LightingOf(0).DistantStrength);
        Assert.Equal(MeshView.FarShadowMetres, _target.LightingOf(0).FarEnd);
    }

    /// <summary>
    /// Under a sun 10 degrees up in the east (+x), a 90 degree view of 16:9 along -z reaching 3 km: the distant cascade
    /// covers from where the far one starts to fade (180 m) to the option's 2000 m, whole, fading out over its last 200 m,
    /// with texels about 2.4 m wide (as the option's documentation says). It holds a block 1 km ahead and casters beside
    /// the view towards the sun, not those beside it away from the sun.
    /// </summary>
    [Fact]
    public void Under_a_low_sun_the_distant_cascade_reaches_from_the_far_ones_fade_to_its_end()
    {
        var view = NewView(distantMetres: 2000);
        var constants = LookingAlongMinusZ(ViewSwitches.Shadows) with
        {
            Up = new Vector3(0, 9f / 16, 0),
            SunDirection = SunPath.FromAngles(90, 10),
            MaxDistance = 3000,
        };

        view.Begin(0, in constants);

        ref readonly var lighting = ref _target.LightingOf(0);
        Assert.True(view.DistantShadows);
        Assert.Equal(1, lighting.DistantStrength);
        Assert.Equal(MeshView.FarShadowMetres, lighting.FarEnd);
        Assert.Equal(180, lighting.FarFadeStart, 3);
        Assert.Equal(2000, lighting.DistantEnd);
        Assert.Equal(1800, lighting.DistantFadeStart, 3);
        Assert.InRange(lighting.DistantTexelMetres, 2.35f, 2.45f);
        Assert.True(view.DistantCascade.Holds(new Vector3(0, 0, -1000), 10));
        Assert.True(view.DistantCascade.Holds(new Vector3(3000, 0, -1000), 10));
        Assert.False(view.DistantCascade.Holds(new Vector3(-3000, 0, -1000), 10));
    }

    /// <summary>
    /// An orthographic view chooses its cascades by how far before or behind its middle a point lies: a box 100 m each
    /// way never reaches the distant cascade, so it is not drawn; a box 1 km each way draws it.
    /// </summary>
    [Theory]
    [InlineData(100f, false)]
    [InlineData(1000f, true)]
    public void An_orthographic_box_draws_the_distant_cascade_only_when_it_reaches_it(float half, bool drawn)
    {
        var view = NewView(distantMetres: 2000);
        var constants = LookingDown(ViewSwitches.Shadows) with
        {
            SunDirection = SunPath.FromAngles(90, 10),
            MaxDistance = 3000,
            Near = -half,
            Far = half,
        };

        view.Begin(0, in constants);

        Assert.Equal(drawn, view.DistantShadows);
        Assert.Equal(drawn ? 1 : 0, _target.LightingOf(0).DistantStrength);
    }

    [Fact]
    public void The_distant_cascade_fades_in_as_the_sun_sinks_from_30_to_25_degrees()
    {
        Assert.Equal(0, MeshView.DistantStrength(ViewConstants.DefaultSunDirection));
        Assert.Equal(0, MeshView.DistantStrength(SunPath.FromAngles(90, 30)), 4);
        Assert.Equal(0.5f, MeshView.DistantStrength(SunPath.FromAngles(90, 27.5f)), 3);
        Assert.Equal(1, MeshView.DistantStrength(SunPath.FromAngles(90, 25)), 4);
        Assert.Equal(1, MeshView.DistantStrength(SunPath.FromAngles(90, 5)));
    }

    /// <summary>
    /// A view shorter than the option ends the distant cascade; one that ends at or before the far cascade's end, or one
    /// without shadows, draws none.
    /// </summary>
    [Theory]
    [InlineData(1000f, true, 1000f)]
    [InlineData(200f, true, 0f)]
    [InlineData(150f, true, 0f)]
    [InlineData(3000f, false, 0f)]
    public void The_view_ends_the_distant_cascade_sooner(float maxDistance, bool shadows, float end)
    {
        var view = NewView(distantMetres: 2000);
        var constants = LookingAlongMinusZ(shadows ? ViewSwitches.Shadows : ViewSwitches.None) with
        {
            SunDirection = SunPath.FromAngles(90, 10),
            MaxDistance = maxDistance,
        };

        view.Begin(0, in constants);

        Assert.Equal(end > 0, view.DistantShadows);
        Assert.Equal(end, _target.LightingOf(0).DistantEnd);
    }

    /// <summary>
    /// The test day through the view, a frame every six minutes: the cascades fit at every hour with an image axis that
    /// turns no more than a few degrees a frame, and the shadow maps are on exactly while the sun is less than 2 degrees
    /// under the horizon.
    /// </summary>
    [Fact]
    public void A_full_test_day_fits_the_cascades_and_draws_no_shadow_maps_at_night()
    {
        var view = NewView();
        var camera = CameraView.Default with { Position = new Vector3(3, 2, 1) };
        var atmosphere = Atmosphere.Clear(new Vector3(0.5f, 0.6f, 0.8f));
        var lastAxis = Vector3.Zero;
        for (var hours = 0f; hours <= 24; hours += 0.1f)
        {
            var sun = SunPath.AtHour(hours);
            var constants = ViewConstants.Create(in camera, 640, 480, 0, sun, in atmosphere, 100, true, default);

            view.Begin(0, in constants);

            ref readonly var lighting = ref _target.LightingOf(0);
            Assert.Equal(sun.Y > SunPath.NightSine ? 1u : 0u, lighting.Shadows);
            var near = lighting.Near;
            Assert.True(float.IsFinite(near.M11 + near.M22 + near.M33 + near.M41 + near.M42 + near.M43));
            var axis = Vector3.Normalize(new Vector3(near.M11, near.M21, near.M31));
            Assert.Equal(0, Vector3.Dot(axis, sun), 4);
            if (lastAxis != Vector3.Zero)
            {
                var turn = MathF.Acos(Math.Clamp(Vector3.Dot(axis, lastAxis), -1, 1)) * 180 / MathF.PI;
                Assert.True(turn < 5, $"{hours} h: the image axis turned {turn} degrees");
            }

            lastAxis = axis;
        }
    }

    /// <summary>
    /// A sun that passes straight overhead turns the maps' image axis a little each frame (the fit's own axis would turn
    /// half round as it passes); a match's end forgets it, so the next match's fixed sun gets the fit's own axis again.
    /// </summary>
    [Fact]
    public void A_sun_passing_overhead_turns_the_maps_a_little_a_frame_and_a_matchs_end_forgets_it()
    {
        var view = NewView();
        var constants = LookingAlongMinusZ(ViewSwitches.Shadows);
        var lastAxis = Vector3.Zero;
        for (var degrees = 80f; degrees <= 100; degrees += 0.1f)
        {
            var angle = degrees * MathF.PI / 180;
            constants.SunDirection = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0);
            view.Begin(0, in constants);
            var near = _target.LightingOf(0).Near;
            var axis = Vector3.Normalize(new Vector3(near.M11, near.M21, near.M31));
            if (lastAxis != Vector3.Zero)
            {
                var turn = MathF.Acos(Math.Clamp(Vector3.Dot(axis, lastAxis), -1, 1)) * 180 / MathF.PI;
                Assert.True(turn < 0.5f, $"{degrees} degrees: the image axis turned {turn} degrees");
            }

            lastAxis = axis;
        }

        constants.SunDirection = ViewConstants.DefaultSunDirection;
        view.ForgetSun();
        view.Begin(0, in constants);
        var forgotten = _target.LightingOf(0).Near;
        var fresh = NewView();
        fresh.Begin(0, in constants);
        Assert.Equal(_target.LightingOf(0).Near, forgotten);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_frame_allocates_nothing(bool orthographic)
    {
        // With the distant cascade drawn: a low sun and a long view.
        var view = NewView(distantMetres: 2000);
        var constants = (
            orthographic ? LookingDown(ViewSwitches.Shadows) : LookingAlongMinusZ(ViewSwitches.Shadows)
        ) with
        {
            SunDirection = SunPath.FromAngles(90, 10),
            MaxDistance = 3000,
        };
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
        view.DistantCascade.Holds(new Vector3(0, 0, -1000), 1);
        view.NearFirstKey(new Vector3(0, 0, -10));
        view.End(1, events);
    }

    private MeshView NewView(int mostLights = 128, float distantMetres = 0) =>
        new(
            _target,
            Options.Create(
                new EngineOptions
                {
                    Renderer = new RendererSettings { MostLights = mostLights, DistantShadowMetres = distantMetres },
                }
            )
        );

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

    private static ViewConstants LookingDown(ViewSwitches switches = ViewSwitches.None) =>
        new()
        {
            Position = new Vector3(0, 50, 0),
            Forward = new Vector3(0, -1, 0),
            Right = new Vector3(32, 0, 0),
            Up = new Vector3(0, 0, -18),
            SunDirection = ViewConstants.DefaultSunDirection,
            MaxDistance = 100,
            Switches = switches | ViewSwitches.Orthographic,
            Near = -100,
            Far = 100,
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
