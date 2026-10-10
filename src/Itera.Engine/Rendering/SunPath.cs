using System.Numerics;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Rendering;

/// <summary>
/// The sun's height and its path, for a moving sun (<see cref="Atmosphere.SunDirection"/>): when it lights the world
/// (<see cref="Daylight"/>, fading out from 2 degrees over the horizon to 2 degrees under it), when its shadow maps are
/// drawn (<see cref="CastsShadows"/>, down to 2 degrees under), the console's test clock (<see cref="AtHour"/>), and
/// directions as compass angles (<see cref="FromAngles"/>). Allocates nothing.
/// </summary>
public static class SunPath
{
    /// <summary>The sine of the height (2 degrees under the horizon) at and below which it is night.</summary>
    public static readonly float NightSine = MathF.Sin(-2 * MathF.PI / 180);

    /// <summary>The sine of the height (2 degrees over the horizon) at and above which the sun lights fully.</summary>
    public static readonly float DaySine = MathF.Sin(2 * MathF.PI / 180);

    /// <summary>
    /// How much of its light the sun towards <paramref name="towardsSun"/> (a unit vector) gives: 1 from 2 degrees over
    /// the horizon up, 0 from 2 degrees under it down, and between them in proportion to the height's sine.
    /// </summary>
    public static float Daylight(Vector3 towardsSun) =>
        Math.Clamp((towardsSun.Y - NightSine) / (DaySine - NightSine), 0f, 1f);

    /// <summary>True when the sun towards <paramref name="towardsSun"/> is over 2 degrees under the horizon: its shadow maps are drawn.</summary>
    public static bool CastsShadows(Vector3 towardsSun) => towardsSun.Y > NightSine;

    /// <summary>
    /// The console's test clock: the sun at <paramref name="hours"/> (0 to 24) on a day whose sunrise is at 6, whose noon
    /// is the engine's sun (<see cref="ViewConstants.DefaultSunDirection"/>), and whose sunset is at 18, on the circle
    /// through them; under the horizon from 18 to 6.
    /// </summary>
    public static Vector3 AtHour(float hours)
    {
        var noon = ViewConstants.DefaultSunDirection;
        // Sunrise: on the horizon, square to noon's direction, on its +x side.
        var rise = Vector3.Normalize(new Vector3(noon.Z, 0, -noon.X));
        var angle = (hours - 6) / 12 * MathF.PI;
        return Vector3.Normalize((MathF.Cos(angle) * rise) + (MathF.Sin(angle) * noon));
    }

    /// <summary>
    /// The direction towards a sun at compass <paramref name="azimuthDegrees"/> (from -z, the starter's start view, round
    /// towards +x) and <paramref name="elevationDegrees"/> over the horizon.
    /// </summary>
    public static Vector3 FromAngles(float azimuthDegrees, float elevationDegrees)
    {
        var azimuth = azimuthDegrees * MathF.PI / 180;
        var elevation = elevationDegrees * MathF.PI / 180;
        var flat = MathF.Cos(elevation);
        return new Vector3(flat * MathF.Sin(azimuth), MathF.Sin(elevation), -flat * MathF.Cos(azimuth));
    }

    /// <summary>The compass azimuth and elevation of <paramref name="towardsSun"/> in degrees (<see cref="FromAngles"/>'s).</summary>
    public static (float AzimuthDegrees, float ElevationDegrees) Angles(Vector3 towardsSun)
    {
        var unit = Vector3.Normalize(towardsSun);
        var azimuth = MathF.Atan2(unit.X, -unit.Z) * 180 / MathF.PI;
        var elevation = MathF.Asin(Math.Clamp(unit.Y, -1f, 1f)) * 180 / MathF.PI;
        return (azimuth < 0 ? azimuth + 360 : azimuth, elevation);
    }
}
