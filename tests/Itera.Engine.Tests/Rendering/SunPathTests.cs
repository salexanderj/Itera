using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// A moving sun (<see cref="SunPath"/>, <see cref="Atmosphere.SunDirection"/>): its light fades out over the 4 degrees
/// round the horizon, its shadow maps stop 2 degrees under it, the test day rises at 6 through the engine's sun at noon
/// to sunset at 18, compass angles turn into directions and back, and an atmosphere without a sun has the engine's.
/// </summary>
public sealed class SunPathTests
{
    [Fact]
    public void Daylight_fades_from_two_degrees_over_the_horizon_to_two_under()
    {
        Assert.Equal(1f, SunPath.Daylight(SunPath.FromAngles(30, 10)));
        Assert.Equal(1f, SunPath.Daylight(ViewConstants.DefaultSunDirection));
        Assert.Equal(0.5f, SunPath.Daylight(SunPath.FromAngles(30, 0)), 4);
        Assert.Equal(0f, SunPath.Daylight(SunPath.FromAngles(30, -2.01f)));
        Assert.Equal(0f, SunPath.Daylight(SunPath.FromAngles(30, -40)));
        Assert.True(SunPath.CastsShadows(SunPath.FromAngles(30, -1.9f)));
        Assert.False(SunPath.CastsShadows(SunPath.FromAngles(30, -2.1f)));
    }

    [Fact]
    public void The_test_day_rises_at_6_is_the_engines_sun_at_noon_and_sets_at_18()
    {
        Assert.True(Vector3.Distance(ViewConstants.DefaultSunDirection, SunPath.AtHour(12)) < 1e-6f);
        Assert.Equal(0, SunPath.Angles(SunPath.AtHour(6)).ElevationDegrees, 3);
        Assert.Equal(0, SunPath.Angles(SunPath.AtHour(18)).ElevationDegrees, 3);
        for (var hours = 0f; hours <= 24; hours += 0.25f)
        {
            var sun = SunPath.AtHour(hours);
            Assert.Equal(1, sun.Length(), 5);
            if (hours is > 6.01f and < 17.99f)
            {
                Assert.True(sun.Y > 0, $"{hours} h: {sun}");
            }
            else if (hours is < 5.99f or > 18.01f)
            {
                Assert.True(sun.Y < 0, $"{hours} h: {sun}");
            }
        }

        // It rises and sets on opposite sides.
        Assert.True(Vector3.Dot(SunPath.AtHour(6), SunPath.AtHour(18)) < -0.999f);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 30)]
    [InlineData(200, -10)]
    [InlineData(359, 85)]
    public void Compass_angles_turn_into_a_direction_and_back(float azimuth, float elevation)
    {
        var sun = SunPath.FromAngles(azimuth, elevation);

        var (backAzimuth, backElevation) = SunPath.Angles(sun);

        Assert.Equal(1, sun.Length(), 5);
        Assert.Equal(azimuth, backAzimuth, 3);
        Assert.Equal(elevation, backElevation, 3);
    }

    [Fact]
    public void The_engines_sun_is_58_degrees_high_and_azimuth_0_looks_along_minus_z()
    {
        var (azimuth, elevation) = SunPath.Angles(ViewConstants.DefaultSunDirection);

        Assert.Equal(126.87f, azimuth, 2);
        Assert.Equal(57.99f, elevation, 2);
        Assert.True(Vector3.Distance(new Vector3(0, 0, -1), SunPath.FromAngles(0, 0)) < 1e-6f);
        Assert.True(Vector3.Distance(new Vector3(1, 0, 0), SunPath.FromAngles(90, 0)) < 1e-6f);
    }

    [Fact]
    public void An_atmosphere_without_a_sun_has_the_engines_and_one_with_its_own_unit()
    {
        var clear = Atmosphere.Clear(Vector3.One);

        Assert.Equal(ViewConstants.DefaultSunDirection, clear.TowardsSun);
        Assert.Equal(Vector3.UnitY, (clear with { SunDirection = new Vector3(0, 2, 0) }).TowardsSun);
        Assert.Equal(
            ViewConstants.DefaultSunDirection,
            (clear with { SunDirection = new Vector3(float.NaN, 0, 0) }).TowardsSun
        );
        Assert.Equal(
            ViewConstants.DefaultSunDirection,
            (clear with { SunDirection = new Vector3(1e-30f, 0, 0) }).TowardsSun
        );
    }

    /// <summary>
    /// At night the view draws no shadow maps and the look gives no sunlight and no disc; at the horizon half of them.
    /// </summary>
    [Fact]
    public void At_night_no_shadow_maps_are_drawn_and_the_sun_gives_no_light()
    {
        var camera = CameraView.Default;
        var atmosphere = Atmosphere.Clear(new Vector3(0.5f, 0.6f, 0.8f));
        var night = ViewConstants.Create(in camera, 640, 480, 0, SunPath.AtHour(22), in atmosphere, 100, true, default);
        var dusk = ViewConstants.Create(
            in camera,
            640,
            480,
            0,
            SunPath.FromAngles(200, 0),
            in atmosphere,
            100,
            true,
            default
        );
        var day = ViewConstants.Create(in camera, 640, 480, 0, SunPath.AtHour(12), in atmosphere, 100, true, default);

        Assert.False(night.Has(ViewSwitches.Shadows));
        Assert.True(dusk.Has(ViewSwitches.Shadows));
        Assert.True(day.Has(ViewSwitches.Shadows));
        var look = RenderLook.Default;
        Assert.Equal(Vector3.Zero, LookConstants.From(in look, in night).SunLight);
        Assert.Equal(Vector3.Zero, LookConstants.From(in look, in night).SunDisc);
        var full = LookConstants.From(in look, in day).SunLight;
        Assert.True(Vector3.Distance(full * 0.5f, LookConstants.From(in look, in dusk).SunLight) < 1e-3f);
        // The particles' sunlight, which takes no warmth, fades by the daylight.
        Assert.Equal(0f, LookConstants.From(in look, in night).Daylight);
        Assert.Equal(0.5f, LookConstants.From(in look, in dusk).Daylight, 3);
        Assert.Equal(1f, LookConstants.From(in look, in day).Daylight);
    }
}
