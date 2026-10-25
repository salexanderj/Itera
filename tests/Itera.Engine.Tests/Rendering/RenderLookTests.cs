using System.Numerics;
using System.Reflection;
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
    /// The sun's glow, the clouds, and the ground haze are off in the engine's look, so a game that sets none of them
    /// draws as before; set, the record holds them with the atmosphere's colours, the heading as a direction, and the
    /// ground haze's floor local to the origin.
    /// </summary>
    [Fact]
    public void The_glow_clouds_and_ground_haze_are_off_by_default_and_worked_out_from_a_games_look()
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
        view.LookTuning.Set(LookTuning.Find("SkyFloor"), new Vector3(0.1f, 0, 0));

        view.ResetAfterMatch();

        Assert.Equal(RenderLook.Default, view.Look);
        Assert.Equal(RenderLook.Default with { SkyFloor = 0.1f }, view.DrawnLook);
    }

    private static void AssertNear(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected}, got {actual}");
}
