using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// A frame slot's numbers for the grass (<c>grass-data.glsl</c>'s <c>GrassFrame</c>, member for member, in the scalar
/// layout; <c>GrassPassTests</c> checks it): the frame's near blocks (<see cref="GrassBlock"/>) and their triangles, the
/// material blades grow on, the blades' room, and the field's numbers from the game's <c>GrassLook</c>. Written by
/// <c>GrassPlacePass</c> each frame, one region per frame slot; read by <c>grass-place.comp</c>, <c>grass-count.comp</c>,
/// and <c>grass.vert</c>.
/// </summary>
public struct GrassFrame
{
    /// <summary>The blocks listed, and their triangles in all.</summary>
    public uint Blocks;

    /// <inheritdoc cref="Blocks"/>
    public uint Triangles;

    /// <summary>The material blades grow on.</summary>
    public uint Material;

    /// <summary>The near blades' room, from the blades' first, and the far ones', from <see cref="NearMost"/>.</summary>
    public uint NearMost;

    /// <inheritdoc cref="NearMost"/>
    public uint FarMost;

    /// <summary>Where the field ends, where it starts to thin towards that, and where the near blades end, in metres.</summary>
    public float Radius;

    /// <inheritdoc cref="Radius"/>
    public float FullMetres;

    /// <inheritdoc cref="Radius"/>
    public float NearMetres;

    /// <summary>Blades a square metre at full density.</summary>
    public float Density;

    /// <summary>A blade's mean height in metres, and its spread, a share either way.</summary>
    public float Height;

    /// <inheritdoc cref="Height"/>
    public float HeightSpread;

    /// <summary>A blade's mean width at its root in metres, and its least width there in pixels.</summary>
    public float Width;

    /// <inheritdoc cref="Width"/>
    public float LeastPixels;

    /// <summary>The least up of a surface that holds grass: the cosine of the steepest slope.</summary>
    public float LeastUp;

    /// <summary>The blades' mean lean from upright, in radians.</summary>
    public float Lean;

    /// <summary>How far the wind pushes the tips, a share of their height, and its gusts' speed in metres a second.</summary>
    public float Wind;

    /// <inheritdoc cref="Wind"/>
    public float WindSpeed;

    /// <summary>The way the wind blows on the ground: a unit vector of x and z.</summary>
    public Vector2 WindWay;

    /// <summary>The frame's seconds, wrapped.</summary>
    public float Seconds;

    /// <summary>How light a blade's tip and root are, as shares of the ground's colour, and how far its colour spreads.</summary>
    public float TipLight;

    /// <inheritdoc cref="TipLight"/>
    public float RootShade;

    /// <inheritdoc cref="TipLight"/>
    public float ColourSpread;

    /// <summary>
    /// How far the ground's texture may stray from its set's mean hue and still hold blades (0 lets every texel hold
    /// them).
    /// </summary>
    public float HueTolerance;
}
