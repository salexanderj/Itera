namespace Itera.Engine.Rendering.Voxels;

/// <summary>
/// A map over the world's x and z of how far each material is drawn from its <see cref="VoxelLook.Colour"/> towards its
/// <see cref="VoxelLook.TintColour"/>, 0 to 255 per texel. The shader blends the four texels around a hit. The map has
/// <see cref="Size"/> x <see cref="Size"/> texels and is uploaded once, in the first frame. The game registers one; the
/// engine's default is 0 everywhere.
/// </summary>
public interface IVoxelTintMap
{
    /// <summary>Texels along each side of the map.</summary>
    const int Size = 256;

    /// <summary>A texel's edge in centimetres, as a shift: 9 gives 512 cm (5.12 m).</summary>
    int TexelShift { get; }

    /// <summary>
    /// The absolute x, in centimetres, of the map's corner at texel column 0; the texel's centre is half a texel further.
    /// </summary>
    int OriginX { get; }

    /// <summary>The absolute z, in centimetres, of the map's corner at texel row 0.</summary>
    int OriginZ { get; }

    /// <summary>Writes the texels, <see cref="Size"/> rows of <see cref="Size"/>: texel (x, z) at <c>z * Size + x</c>.</summary>
    void Write(Span<byte> texels);
}

/// <summary>A tint map of 0 everywhere: every material keeps its colour.</summary>
public sealed class NoVoxelTintMap : IVoxelTintMap
{
    public int TexelShift => 9;

    public int OriginX => 0;

    public int OriginZ => 0;

    public void Write(Span<byte> texels) => texels.Clear();
}
