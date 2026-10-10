using System.Numerics;

namespace Itera.Engine.Rendering;

/// <summary>
/// The sky, sunlight, and air a frame is drawn with. A frame system may set it every frame
/// (<see cref="RenderView.SetAtmosphere"/>); until one does, the sky comes from the <c>Engine:Renderer</c> sky options
/// and the rest is neutral.
/// </summary>
/// <param name="SkyHorizon">Linear colour of the sky at the horizon; fog fades into it, and the sky light is a share of it.</param>
/// <param name="SunColour">Linear colour and strength of direct sunlight, and of the sun's disc.</param>
/// <param name="Haze">0 to 1: fog that thickens with distance on top of the fog at the view distance. At 1, half of a
/// surface the look's haze half distance away (<see cref="RenderLook.HazeHalfMetres"/>, 60 m) is lost in it.</param>
/// <param name="Overcast">0 to 1: flattens the sky towards its horizon colour and hides the sun's disc.</param>
/// <param name="Grade">0 to 1: how far the picture is desaturated, darkened, and shifted towards the sky's hue. 0 leaves
/// it as the tone curve gives it.</param>
/// <param name="SunDirection">From the ground towards the sun (any length); zero, the default, is the engine's sun
/// (<see cref="GpuData.ViewConstants.DefaultSunDirection"/>). Its light fades out from 2 degrees over the horizon to 2
/// degrees under it, where night starts: no sunlight, no disc, and no shadow maps (<see cref="SunPath"/>). A game's moon
/// is this light at night, dim and cool.</param>
public readonly record struct Atmosphere(
    Vector3 SkyHorizon,
    Vector3 SunColour,
    float Haze,
    float Overcast,
    float Grade,
    Vector3 SunDirection = default
)
{
    /// <summary>The sunlight used before the atmosphere had options: slightly warm white.</summary>
    public static Vector3 DefaultSunColour => new(1.0f, 0.93f, 0.82f);

    /// <summary>A clear sky of <paramref name="skyHorizon"/>, the default sunlight, and no haze, overcast, or grade.</summary>
    public static Atmosphere Clear(Vector3 skyHorizon) => new(skyHorizon, DefaultSunColour, 0, 0, 0);

    /// <summary>
    /// <see cref="Haze"/>, <see cref="Overcast"/>, and <see cref="Grade"/> as one word, a byte each from the lowest (the
    /// layout of GLSL's <c>unpackUnorm4x8</c>: x haze, y overcast, z grade, w unused). Values are clamped to 0 to 1.
    /// </summary>
    public uint PackAir() => Byte(Haze) | Byte(Overcast) << 8 | Byte(Grade) << 16;

    /// <summary>The unit direction towards the sun: <see cref="SunDirection"/>, or the engine's sun when it is zero.</summary>
    public Vector3 TowardsSun =>
        !(SunDirection.LengthSquared() is > 1e-12f and < float.PositiveInfinity)
            ? GpuData.ViewConstants.DefaultSunDirection
            : Vector3.Normalize(SunDirection);

    private static uint Byte(float value) => (uint)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
