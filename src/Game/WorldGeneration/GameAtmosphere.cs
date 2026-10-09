using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Systems;

namespace Game.WorldGeneration;

/// <summary>
/// The sky, sunlight, and air everywhere: a pale, slightly hazy sky, a warm sun, and a light grade. A game that wants
/// places to differ (regions, heights, weather) returns another <see cref="Atmosphere"/> by place.
/// </summary>
public sealed class GameAtmosphere : IAtmosphereSource
{
    /// <summary>The one look.</summary>
    public static readonly Atmosphere Day = new(
        new Vector3(0.58f, 0.66f, 0.74f),
        new Vector3(1.00f, 0.92f, 0.78f),
        Haze: 0.04f,
        Overcast: 0.2f,
        Grade: 0.12f
    );

    public Atmosphere At(Vector3 positionMetres) => Day;
}
