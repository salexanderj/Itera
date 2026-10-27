using System.Numerics;
using System.Reflection;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// The look as data (<see cref="RenderLook"/>): the engine's values are those the shaders held as constants; the shaders'
/// record (<see cref="LookConstants"/>) holds them and the light they give with the atmosphere; two looks blend value by
/// value; the console's tuning names every value, lays the ones set over a frame's look without allocating, and stays
/// when a match ends while the look itself goes back to the engine's.
/// </summary>
public sealed class RenderLookTests
{
    /// <summary>The values the shaders had as constants before the look was data: the starter's look.</summary>
    [Fact]
    public void The_engines_look_is_the_one_the_shaders_had()
    {
        var look = RenderLook.Default;

        Assert.Equal(look, new RenderLook());
        Assert.Equal(
            [0.75f, 0.85f, 0.7f, 1.1f, -0.3f, 0.9f],
            [
                look.WorldSaturation,
                look.WorldValue,
                look.WorldSideShade,
                look.WorldTopShade,
                look.SideShadeBelow,
                look.TopShadeAbove,
            ]
        );
        Assert.Equal(new Vector3(1.08f, 1.0f, 0.88f), look.SunWarmth);
        Assert.Equal(new Vector3(0.85f, 0.95f, 1.15f), look.ShadeCool);
        Assert.Equal(new Vector3(0.6875f, 0.75f, 0.875f), look.SkyLightShare);
        Assert.Equal(new Vector3(0.2f, 0.18f, 0.14f), look.GroundLight);
        Assert.Equal(
            [0.2f, 0.35f, 0.02f, 4f, 0.6f, 60f],
            [
                look.GroundBounce,
                look.OcclusionFloor,
                look.SkyFloor,
                look.GlowStrength,
                look.FogStart,
                look.HazeHalfMetres,
            ]
        );
        Assert.Equal(new Vector3(0.35f, 0.5f, 0.85f), look.ZenithClear);
        Assert.Equal(new Vector3(0.8f), look.ZenithOvercast);
        Assert.Equal(
            [0.45f, 400f, 4f, 0.9f, 0.8f, 0.6f, 0.3f, 0.15f, 0.6f],
            [
                look.BelowHorizon,
                look.SunDiscSharpness,
                look.SunDiscStrength,
                look.SunDiscOvercast,
                look.Exposure,
                look.GradeDesaturate,
                look.GradeDarken,
                look.ShadowsBelow,
                look.LightsAbove,
            ]
        );
        Assert.Equal(new Vector3(0.9f, 1.0f, 1.08f), look.GradeShadows);
        Assert.Equal(new Vector3(1.06f, 1.0f, 0.9f), look.GradeLights);
    }

    /// <summary>
    /// The shaders' record holds the look's values and the light it gives with the frame's atmosphere, as the shaders
    /// worked it out at every pixel before (the haze, overcast, and grade as the bytes the view packs).
    /// </summary>
    [Fact]
    public void The_shaders_record_holds_the_look_and_the_light_it_gives_with_the_atmosphere()
    {
        var look = RenderLook.Default with { GroundBounce = 0.25f, SunDiscOvercast = 0.5f };
        var atmosphere = new Atmosphere(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(1, 0.9f, 0.7f), 0.4f, 0.6f, 0.2f);
        var camera = CameraView.Default;
        var sun = Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.2f));
        var view = ViewConstants.Create(
            in camera,
            640,
            480,
            1,
            sun,
            in atmosphere,
            256,
            true,
            default,
            1,
            ViewSwitches.None
        );
        var haze = MathF.Round(0.4f * 255) / 255;
        var overcast = MathF.Round(0.6f * 255) / 255;
        var grade = MathF.Round(0.2f * 255) / 255;

        var constants = LookConstants.From(in look, in view);

        Assert.Equal(atmosphere.SunColour * look.SunWarmth, constants.SunLight);
        AssertNear(atmosphere.SkyHorizon * look.SkyLightShare * (1 + overcast), constants.SkyLight);
        AssertNear(look.GroundLight + atmosphere.SunColour * 0.25f * sun.Y, constants.GroundLight);
        AssertNear(
            atmosphere.SkyHorizon * Vector3.Lerp(look.ZenithClear, look.ZenithOvercast, overcast),
            constants.Zenith
        );
        Assert.Equal(atmosphere.SkyHorizon * 0.45f, constants.Below);
        AssertNear(atmosphere.SunColour * 4 * (1 - 0.5f * overcast), constants.SunDisc);
        var luma = Vector3.Dot(atmosphere.SkyHorizon, new Vector3(0.2126f, 0.7152f, 0.0722f));
        AssertNear(atmosphere.SkyHorizon / luma, constants.GradeHue);
        Assert.Equal(0.6f * grade, constants.GradeMix, 6);
        Assert.Equal(1 - 0.3f * grade, constants.GradeScale, 6);
        Assert.Equal(haze / 60, constants.HazePerMetre, 6);
        Assert.Equal(
            (look.WorldSaturation, look.WorldValue, look.WorldSideShade, look.WorldTopShade, look.SideShadeBelow),
            (
                constants.WorldSaturation,
                constants.WorldValue,
                constants.WorldSideShade,
                constants.WorldTopShade,
                constants.SideShadeBelow
            )
        );
        Assert.Equal(
            (
                look.TopShadeAbove,
                look.ShadeCool,
                look.GradeShadows,
                look.GradeLights,
                look.ShadowsBelow,
                look.LightsAbove
            ),
            (
                constants.TopShadeAbove,
                constants.ShadeCool,
                constants.GradeShadows,
                constants.GradeLights,
                constants.ShadowsBelow,
                constants.LightsAbove
            )
        );
        Assert.Equal(
            (
                look.SunDiscSharpness,
                look.OcclusionFloor,
                look.SkyFloor,
                look.GlowStrength,
                look.FogStart,
                look.Exposure
            ),
            (
                constants.SunDiscSharpness,
                constants.OcclusionFloor,
                constants.SkyFloor,
                constants.GlowStrength,
                constants.FogStart,
                constants.Exposure
            )
        );
        // Under the horizon the sun throws no light back from the ground.
        var below = ViewConstants.Create(
            in camera,
            640,
            480,
            1,
            -sun,
            in atmosphere,
            256,
            true,
            default,
            1,
            ViewSwitches.None
        );
        Assert.Equal(look.GroundLight, LookConstants.From(in look, in below).GroundLight);
    }

    /// <summary>
    /// The sun's glow, the clouds, the ground haze, and the edge light are off in the engine's look, so a game that
    /// sets none of them draws as before; set, the record holds them with the atmosphere's colours, the heading as a
    /// direction, and the ground haze's floor local to the origin.
    /// </summary>
    [Fact]
    public void The_glow_clouds_ground_haze_and_edge_light_are_off_by_default_and_worked_out_from_a_games_look()
    {
        var atmosphere = new Atmosphere(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(1, 0.9f, 0.7f), 0.4f, 0.6f, 0.2f);
        var camera = CameraView.Default;
        var sun = Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.2f));
        var view = ViewConstants.Create(
            in camera,
            640,
            480,
            1,
            sun,
            in atmosphere,
            256,
            true,
            new VoxelCoord(0, 300, 0)
        );

        var plain = LookConstants.From(RenderLook.Default, in view);
        Assert.Equal(Vector3.Zero, plain.SunGlow);
        Assert.Equal(0, plain.CloudCover);
        Assert.Equal(0, plain.GroundHazePerMetre);
        Assert.Equal(Vector3.Zero, plain.RimLight);

        var look = RenderLook.Default with
        {
            SunGlowStrength = 0.5f,
            SunGlowSharpness = 0,
            CloudCover = 1.5f,
            CloudColour = new Vector3(0.5f, 0.4f, 0.3f),
            CloudHeadingDegrees = 90,
            CloudStretch = 3,
            GroundHaze = 0.5f,
            GroundHazeHalfMetres = 50,
            GroundHazeFloor = 12,
            GroundHazeFalloffMetres = 20,
            RimStrength = 0.4f,
            RimSharpness = 0,
        };
        var constants = LookConstants.From(in look, in view);

        AssertNear(atmosphere.SunColour * 0.5f, constants.SunGlow);
        // A power under 1 would light the whole sky, and 0 gives no number where the sun is behind.
        Assert.Equal(1, constants.SunGlowSharpness);
        Assert.Equal(1, constants.CloudCover);
        AssertNear(atmosphere.SkyHorizon * look.CloudColour, constants.CloudColour);
        Assert.Equal(3, constants.CloudStretch);
        // 90 degrees from north is east, +x.
        Assert.Equal(1, constants.CloudAlongX, 5);
        Assert.Equal(0, constants.CloudAlongZ, 5);
        Assert.Equal(0.01f, constants.GroundHazePerMetre, 6);
        // 12 m in the world is 9 m over an origin 3 m up.
        Assert.Equal(9, constants.GroundHazeFloor, 5);
        Assert.Equal(0.05f, constants.GroundHazeFalloff, 6);
        // The edge light is the sky's light, so it takes the look's shade of the sky, not the horizon's colour.
        AssertNear(constants.SkyLight * 0.4f, constants.RimLight);
        Assert.Equal(1, constants.RimSharpness);
    }

    /// <summary>
    /// The light towards the sun, the darker sky away from it, the horizon band, the haze's start, cap, and colour fade,
    /// and the lights' halos leave the engine's look as it was, and a game's values reach the record clamped, the light
    /// faded with the sun.
    /// </summary>
    [Fact]
    public void The_sky_lobe_horizon_band_and_haze_shape_are_off_by_default_and_worked_out_from_a_games_look()
    {
        var atmosphere = new Atmosphere(new Vector3(0.5f, 0.6f, 0.8f), new Vector3(1, 0.9f, 0.7f), 0.4f, 0.6f, 0.2f);
        var camera = CameraView.Default;
        var sun = Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.2f));
        var view = ViewConstants.Create(in camera, 640, 480, 1, sun, in atmosphere, 256, true, default);

        var plain = LookConstants.From(RenderLook.Default, in view);
        Assert.Equal(Vector3.Zero, plain.SkyLobe);
        Assert.Equal(0, plain.SkyAwayDarken);
        Assert.Equal(Vector3.Zero, plain.HorizonBand);
        // A power of a half is the square root the sky had.
        Assert.Equal(0.5f, plain.ZenithPower);
        Assert.Equal(0, plain.HazeStart);
        Assert.Equal(1, plain.HazeMost);
        Assert.Equal(Vector3.One, plain.HazeFade);
        Assert.Equal(1, plain.HazeFadeMean, 5);
        Assert.Equal(0u, plain.GlowCount);
        Assert.Equal(0, plain.Halo);

        var look = RenderLook.Default with
        {
            SkyLobeStrength = 0.3f,
            SkyLobeSharpness = 0,
            SkyAwayDarken = 2,
            ZenithPower = 0,
            HorizonBand = 0.4f,
            HorizonBandDegrees = 30,
            HazeStartMetres = -5,
            HazeMost = 0.9f,
            HazeColourFade = new Vector3(0.5f, 1, -1),
            LightHalo = -1,
            LightHaloLights = 20,
            LightHaloRadiusMetres = 0,
        };
        var constants = LookConstants.From(in look, in view);
        // No light gives less than none, at most the record's most have halos, and a halo keeps a middle.
        Assert.Equal(0, constants.Halo);
        Assert.Equal((uint)LookConstants.MostHaloLights, constants.HaloLights);
        Assert.Equal(0.1f, constants.HaloRadius);

        AssertNear(atmosphere.SunColour * 0.3f, constants.SkyLobe);
        // A power of 0 would light the whole sky evenly, and the zenith's would turn the sky at once.
        Assert.Equal(0.1f, constants.SkyLobeSharpness);
        Assert.Equal(1, constants.SkyAwayDarken);
        Assert.Equal(0.05f, constants.ZenithPower);
        // Half as bright 30 degrees up: the view's height there is a half.
        Assert.Equal(2, constants.HorizonBandFalloff, 4);
        AssertNear(atmosphere.SkyHorizon * 0.4f, constants.HorizonBand);
        Assert.Equal(0, constants.HazeStart);
        Assert.Equal(0.9f, constants.HazeMost);
        Assert.Equal(new Vector3(0.5f, 1, 0), constants.HazeFade);
        Assert.Equal(0.2126f * 0.5f + 0.7152f, constants.HazeFadeMean, 5);

        var night = ViewConstants.Create(
            in camera,
            640,
            480,
            1,
            new Vector3(0, -1, 0),
            in atmosphere,
            256,
            true,
            default
        );
        Assert.Equal(Vector3.Zero, LookConstants.From(in look, in night).SkyLobe);
    }

    /// <summary>
    /// The frame's haze glows reach the record up to the most it holds, their places local to the view's origin, their
    /// radius at least a metre and their colour never negative.
    /// </summary>
    [Fact]
    public void Haze_glows_reach_the_record_local_to_the_origin_up_to_the_most()
    {
        var atmosphere = Atmosphere.Clear(new Vector3(0.5f));
        var camera = CameraView.Default;
        var origin = new VoxelCoord(100_000, -300, 2_000);
        var view = ViewConstants.Create(in camera, 640, 480, 1, Vector3.UnitY, in atmosphere, 256, true, origin);
        HazeGlow[] glows =
        [
            new(new WorldPoint(1_010.5, 20, -30), new Vector3(0.5f, -0.1f, 0.2f), 0.2f),
            new(new WorldPoint(0, 0, 0), Vector3.One, 40),
            new(new WorldPoint(0, 0, 0), Vector3.One, 41),
            new(new WorldPoint(0, 0, 0), Vector3.One, 42),
            new(new WorldPoint(0, 0, 0), Vector3.One, 43),
        ];

        var constants = LookConstants.From(RenderLook.Default, in view, glows, 0);

        Assert.Equal((uint)HazeGlow.Most, constants.GlowCount);
        // 1,010.5 m less an origin 1,000 m along x; 20 m over one 3 m under; -30 m less one 20 m along z.
        AssertNear(new Vector3(10.5f, 23, -50), constants.Glow0.Place);
        Assert.Equal(1, constants.Glow0.Radius);
        Assert.Equal(new Vector3(0.5f, 0, 0.2f), constants.Glow0.Colour);
        Assert.Equal(42, constants.Glow3.Radius);
        Assert.Equal(0u, LookConstants.From(RenderLook.Default, in view, glows.AsSpan(0, 0), 0).GlowCount);
    }

    /// <summary>
    /// The clouds' lattice point of a place in the world is the same whatever the origin, they drift along their heading
    /// with the clock, every lattice point stays within the clouds' period, their light fades with the sun, and their
    /// shape's values are off by default and clamped.
    /// </summary>
    [Fact]
    public void The_clouds_stay_over_the_land_whatever_the_origin_and_drift_along_their_heading()
    {
        var atmosphere = new Atmosphere(new Vector3(0.5f), new Vector3(1, 0.9f, 0.7f), 0, 0, 0);
        var camera = CameraView.Default;
        var sun = Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.2f));
        var look = RenderLook.Default with
        {
            CloudCover = 0.5f,
            CloudScale = 2,
            CloudStretch = 4,
            CloudHeadingDegrees = 90,
            CloudHeightMetres = 1000,
            CloudDriftMetresPerSecond = 10,
            CloudLight = 0.5f,
        };
        var near = ViewConstants.Create(in camera, 640, 480, 1, sun, in atmosphere, 256, true, default);
        var far = ViewConstants.Create(
            in camera,
            640,
            480,
            1,
            sun,
            in atmosphere,
            256,
            true,
            new VoxelCoord(3_000_000_00, 20_00, -7_000_000_00)
        );

        var atNear = LookConstants.From(in look, in near, default, 0);
        var atFar = LookConstants.From(in look, in far, default, 0);
        // A place 3,000 km east and 7,000 km south: local to the far origin it is at its middle.
        var place = new Vector2(3_000_000, -7_000_000);
        AssertSameLattice(Lattice(atNear, place), Lattice(atFar, Vector2.Zero));
        // The upper clouds' lattice is a scale of the lower's: 0.7 times, twice as high.
        Assert.Equal(atNear.CloudNearScale * 0.35f, atNear.CloudFarScale, 6);
        // 20 m higher, the plane is 20 m nearer.
        Assert.Equal(1000, atNear.CloudLocalHeight);
        Assert.Equal(980, atFar.CloudLocalHeight);

        // Heading east (+x), 10 m a second for 100 s: the clouds over a place came from 1,000 m west of it.
        var later = LookConstants.From(in look, in near, default, 100);
        AssertSameLattice(Lattice(atNear, new Vector2(-1000, 0)), Lattice(later, Vector2.Zero));
        // After a long run the point still lies within the tables' period, where a float keeps its precision.
        var afterLongRun = LookConstants.From(in look, in far, default, 1e9);
        Assert.InRange(afterLongRun.CloudNear.X, 0, 4096);
        Assert.InRange(afterLongRun.CloudNear.Y, 0, 4096);

        AssertNear(atmosphere.SunColour * 0.5f, atNear.CloudLight);
        // The shape's values are off by default and reach the record clamped.
        var plain = LookConstants.From(RenderLook.Default, in near);
        Assert.Equal((0f, 0f, 0f), (plain.CloudBillow, plain.CloudErosion, plain.CloudShadow));
        var shaped = LookConstants.From(look with { CloudBillow = 2, CloudErosion = -1, CloudShadow = -3 }, in near);
        Assert.Equal((1f, 0f, 0f), (shaped.CloudBillow, shaped.CloudErosion, shaped.CloudShadow));
        var night = ViewConstants.Create(
            in camera,
            640,
            480,
            1,
            new Vector3(0, -1, 0),
            in atmosphere,
            256,
            true,
            default
        );
        Assert.Equal(Vector3.Zero, LookConstants.From(in look, in night).CloudLight);
    }

    /// <summary>
    /// The lower wisps lie at their share of the clouds' height, drawn out more along the clouds' heading, and drift
    /// faster; their lattice point of a place in the world is the same whatever the origin. The puffs', creases', reach's,
    /// and wisps' values are off by default and clamped.
    /// </summary>
    [Fact]
    public void The_lower_wisps_lie_lower_and_drift_faster_and_the_depth_values_are_off_by_default()
    {
        var atmosphere = new Atmosphere(new Vector3(0.5f), new Vector3(1, 0.9f, 0.7f), 0, 0, 0);
        var camera = CameraView.Default;
        var sun = Vector3.Normalize(new Vector3(0.3f, 0.6f, 0.2f));
        var look = RenderLook.Default with
        {
            CloudCover = 0.5f,
            CloudStretch = 2,
            CloudHeadingDegrees = 90,
            CloudHeightMetres = 1000,
            CloudDriftMetresPerSecond = 10,
            CloudLower = 0.5f,
        };
        var near = ViewConstants.Create(in camera, 640, 480, 1, sun, in atmosphere, 256, true, default);
        var far = ViewConstants.Create(
            in camera,
            640,
            480,
            1,
            sun,
            in atmosphere,
            256,
            true,
            new VoxelCoord(3_000_000_00, 20_00, -7_000_000_00)
        );

        var atNear = LookConstants.From(in look, in near, default, 0);
        var atFar = LookConstants.From(in look, in far, default, 0);
        AssertSameLattice(LowerLattice(atNear, new Vector2(3_000_000, -7_000_000)), LowerLattice(atFar, Vector2.Zero));
        Assert.Equal(1000 * LookConstants.LowerCloudHeightShare, atNear.CloudLowerLocalHeight, 3);
        Assert.Equal(1000 * LookConstants.LowerCloudHeightShare - 20, atFar.CloudLowerLocalHeight, 3);
        Assert.Equal(2 * LookConstants.LowerCloudStretch, atNear.CloudLowerStretch);
        // 100 s at 10 m a second, half as fast again: the wisps over a place came from 1,500 m west of it.
        var later = LookConstants.From(in look, in near, default, 100);
        AssertSameLattice(
            LowerLattice(atNear, new Vector2(-1000 * LookConstants.LowerCloudDrift, 0)),
            LowerLattice(later, Vector2.Zero)
        );

        var plain = LookConstants.From(RenderLook.Default, in near);
        Assert.Equal((0f, 0f, 0f, 0f), (plain.CloudPuffLight, plain.CloudCreases, plain.CloudReach, plain.CloudLower));
        var clamped = LookConstants.From(
            look with
            {
                CloudPuffLight = -1,
                CloudCreases = -2,
                CloudReachMetres = -5,
                CloudLower = 3,
            },
            in near
        );
        Assert.Equal(
            (0f, 0f, 0f, 1f),
            (clamped.CloudPuffLight, clamped.CloudCreases, clamped.CloudReach, clamped.CloudLower)
        );
    }

    // The lower clouds' lattice point of the local place on their plane, as clouds.glsl finds it.
    private static Vector2 Lattice(in LookConstants constants, Vector2 place)
    {
        var along = new Vector2(constants.CloudAlongX, constants.CloudAlongZ);
        var frame = new Vector2(
            Vector2.Dot(place, along) / constants.CloudStretch,
            Vector2.Dot(place, new Vector2(-along.Y, along.X))
        );
        return constants.CloudNear + frame * constants.CloudNearScale;
    }

    // The lower wisps' lattice point of the local place on their plane, as clouds.glsl finds it.
    private static Vector2 LowerLattice(in LookConstants constants, Vector2 place)
    {
        var along = new Vector2(constants.CloudAlongX, constants.CloudAlongZ);
        var frame = new Vector2(
            Vector2.Dot(place, along) / constants.CloudLowerStretch,
            Vector2.Dot(place, new Vector2(-along.Y, along.X))
        );
        return constants.CloudLowerNear + frame * constants.CloudLowerScale;
    }

    // Two lattice points the clouds read alike: the same within their period of 4,096 cells.
    private static void AssertSameLattice(Vector2 expected, Vector2 actual)
    {
        var difference = expected - actual;
        var x = difference.X - 4096 * MathF.Round(difference.X / 4096);
        var y = difference.Y - 4096 * MathF.Round(difference.Y / 4096);
        Assert.True(MathF.Abs(x) < 1e-2f && MathF.Abs(y) < 1e-2f, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void Two_looks_blend_value_by_value_without_allocating()
    {
        var dusk = RenderLook.Default with { Exposure = 0.4f, SunWarmth = new Vector3(1.4f, 0.8f, 0.6f) };

        var half = RenderLook.Lerp(RenderLook.Default, dusk, 0.5f);

        Assert.Equal(RenderLook.Default, RenderLook.Lerp(RenderLook.Default, dusk, 0));
        Assert.Equal(dusk, RenderLook.Lerp(RenderLook.Default, dusk, 1));
        Assert.Equal(0.6f, half.Exposure, 6);
        Assert.True(Vector3.Distance(new Vector3(1.24f, 0.9f, 0.74f), half.SunWarmth) < 1e-6f);
        Assert.Equal(RenderLook.Default.GlowStrength, half.GlowStrength);
        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            half = RenderLook.Lerp(RenderLook.Default, dusk, i / 100f);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>The console's table names every value of the look, in its order, and reads back what it sets.</summary>
    [Fact]
    public void The_tuning_names_every_value_and_reads_back_what_it_sets()
    {
        var properties = typeof(RenderLook)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(float) || p.PropertyType == typeof(Vector3))
            .ToList();

        Assert.Equal(properties.Select(p => p.Name), LookTuning.Fields.Select(f => f.Name));
        foreach (var field in LookTuning.Fields)
        {
            var property = properties.Single(p => p.Name == field.Name);
            Assert.Equal(property.PropertyType == typeof(Vector3) ? 3 : 1, field.Components);
            var value = field.Components == 3 ? new Vector3(0.5f, 1.5f, 2.5f) : new Vector3(7.5f, 0, 0);
            var look = field.Set(RenderLook.Default, value);
            Assert.Equal(value, field.Get(look));
            Assert.Equal(field.Components == 3 ? value : (object)value.X, property.GetValue(look));
            Assert.Equal(LookTuning.Fields.ToList().IndexOf(field), LookTuning.Find(field.Name.ToUpperInvariant()));
        }

        Assert.Equal(-1, LookTuning.Find("Fog"));
    }

    [Fact]
    public void Tuned_values_lay_over_a_frames_look_without_allocating()
    {
        var tuning = new LookTuning();
        var look = RenderLook.Default with { Exposure = 0.5f, GlowStrength = 2 };
        Assert.Equal(look, tuning.Apply(look));

        tuning.Set(LookTuning.Find("Exposure"), new Vector3(0.9f, 0, 0));
        tuning.Set(LookTuning.Find("GradeLights"), new Vector3(1, 1, 1));
        var tuned = tuning.Apply(look);

        Assert.Equal(look with { Exposure = 0.9f, GradeLights = Vector3.One }, tuned);
        Assert.Equal(2, tuning.Count);
        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            tuned = tuning.Apply(look);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        tuning.Reset();
        Assert.Equal(look, tuning.Apply(look));
        Assert.Equal(0, tuning.Count);
    }

    [Fact]
    public void A_matchs_end_gives_the_engines_look_back_and_keeps_the_runs_tuning()
    {
        var view = new RenderView(Options.Create(new EngineOptions()));
        view.SetLook(RenderLook.Default with { Exposure = 2 });
        view.SetGlows([new HazeGlow(new WorldPoint(1, 2, 3), Vector3.One, 50)]);
        view.LookTuning.Set(LookTuning.Find("SkyFloor"), new Vector3(0.1f, 0, 0));

        view.ResetAfterMatch();

        Assert.Equal(RenderLook.Default, view.Look);
        Assert.Equal(0, view.Glows.Length);
        Assert.Equal(RenderLook.Default with { SkyFloor = 0.1f }, view.DrawnLook);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected}, got {actual}");
}
