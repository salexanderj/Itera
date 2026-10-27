using System.Numerics;
using Itera.Engine.Maths;

namespace Itera.Engine.Rendering;

/// <summary>
/// A place where the air glows: the light of a large source (a fortress's fires, a burning city) that the haze scatters
/// towards the eye along every view ray passing near it, in the sky and in front of every surface, so the source stands
/// as a shape against lit air. A game gives up to <see cref="Most"/> through its atmosphere source
/// (<see cref="Systems.IAtmosphereSource.GlowsAt"/>). The light falls off as one over the square of the distance from
/// <see cref="Place"/> softened by <see cref="RadiusMetres"/>, and the haze between the glow and the eye dims it
/// (<c>sky.glsl</c>'s <c>hazeGlows</c>).
/// </summary>
/// <param name="Place">The glow's middle, absolute.</param>
/// <param name="Colour">The linear light the glow adds to a ray through its middle from far away, before the haze between
/// it and the eye dims it.</param>
/// <param name="RadiusMetres">How wide its bright middle is: a ray passing this far from the middle takes about 0.7 of
/// <see cref="Colour"/>, one three times as far about 0.3.</param>
public readonly record struct HazeGlow(WorldPoint Place, Vector3 Colour, float RadiusMetres)
{
    /// <summary>The most glows a frame draws; a source's others are left out.</summary>
    public const int Most = 4;
}
