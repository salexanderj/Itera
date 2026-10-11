using System.Numerics;
using Itera.Engine.Rendering.Materials;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// A material as the raster pipeline shades it (<c>mesh-data.glsl</c>'s <c>MeshMaterial</c>): its colour, the colour the
/// tint map blends it towards, its recipe, its glow, and its texture set if it has one. Written once per run into the mesh
/// heap by <c>WorldMeshes</c>.
/// </summary>
public struct MeshMaterial
{
    /// <summary>Linear RGB where the tint map is 0.</summary>
    public Vector3 Colour;

    /// <summary>The recipe and its sizes (<see cref="VoxelLook.PackLook"/>), which <c>mesh-surface.glsl</c> reads.</summary>
    public uint Look;

    /// <summary>Linear RGB where the tint map is 1.</summary>
    public Vector3 TintColour;

    /// <summary>The recipe's contrast, relief, patches, and glow, a byte each (<see cref="VoxelLook.PackAmounts"/>).</summary>
    public uint Amounts;

    /// <summary>Linear RGB of the recipe's accent (pebbles, inner bark, fresh leaves).</summary>
    public Vector3 Accent;

    /// <summary>0 to 1: light the material gives off, as a share of four times its colour (the CPU's lights read it).</summary>
    public float Glow;

    /// <summary>The mean of the texture set's colour, linear RGB, which the colour match moves to <see cref="Colour"/>.</summary>
    public Vector3 TextureMean;

    /// <summary>The width in metres of the texture set's tile; 0 without a set.</summary>
    public float TextureMetres;

    /// <summary>
    /// The texture set: its layer plus 1 in bits 0 to 15 (0 for none: plain), <see cref="LargeTextureBit"/> when it is in
    /// the large arrays, <see cref="BandsTextureBit"/>, and the colour match (0 to 255) in bits 24 to 31.
    /// </summary>
    public uint Texture;

    /// <summary>
    /// How the texture set is drawn, a byte each from the lowest: the detail kept round its mean (0 to 1), the relief of
    /// its normal map and occlusion (0 to 1), and the blur in mip levels (0 to 4, a quarter of the byte's range a level).
    /// </summary>
    public uint TextureStyle;

    /// <summary>
    /// The match's origin's place within the material's texture tile, in metres on each axis: the origin modulo the
    /// tile's width (<see cref="TextureMetres"/>, or <see cref="AllMaterialsTileMetres"/> for a material without a set),
    /// worked out in doubles for the frame's origin (<see cref="TilePhase"/>, <c>WorldMeshes</c>), so mesh-surface.glsl's
    /// <c>tilePoint</c> finds the same place in the tile far from the map's middle; zero at the middle.
    /// </summary>
    public Vector3 TileOrigin;

    /// <summary>
    /// The tile's width, in metres, that the console's <c>materials all</c> draws a material without a set with (mesh.frag's
    /// <c>drawnTexture</c>).
    /// </summary>
    public const float AllMaterialsTileMetres = 3;

    /// <summary>
    /// The place of <paramref name="origin"/> (absolute centimetres) within a tile <paramref name="tileMetres"/> wide on
    /// each axis, 0 to the width, in doubles.
    /// </summary>
    public static Vector3 TilePhase(VoxelCoord origin, double tileMetres)
    {
        if (!(tileMetres > 0))
        {
            return Vector3.Zero;
        }

        return new Vector3(Phase(origin.X), Phase(origin.Y), Phase(origin.Z));

        float Phase(int centimetres)
        {
            var metres = centimetres / (double)VoxelConstants.CentimetresPerMetre;
            return (float)(metres - Math.Floor(metres / tileMetres) * tileMetres);
        }
    }

    /// <summary>The <see cref="Texture"/> bit of a set in the large arrays.</summary>
    public const uint LargeTextureBit = 1 << 16;

    /// <summary>
    /// The <see cref="Texture"/> bit of strata's bands by height (<c>mesh-surface.glsl</c>'s <c>strataBands</c>), which a
    /// set's 2D tile cannot hold: on for a material of the <see cref="VoxelPattern.Strata"/> or
    /// <see cref="VoxelPattern.Mottle"/> recipe.
    /// </summary>
    public const uint BandsTextureBit = 1 << 17;

    /// <summary>
    /// The material as <paramref name="look"/> describes it, drawn from the texture set <paramref name="texture"/> when
    /// there is one.
    /// </summary>
    public static MeshMaterial From(in VoxelLook look, MaterialTextureSlot? texture = null) =>
        new()
        {
            TextureMean = texture?.Mean ?? Vector3.Zero,
            TextureStyle = Unit(look.TextureDetail) | Unit(look.TextureRelief) << 8 | Unit(look.TextureBlur / 4f) << 16,
            TextureMetres = texture?.Metres ?? 0,
            Texture = texture is { } slot
                ? (uint)(slot.Layer + 1)
                    | (slot.Large ? LargeTextureBit : 0)
                    | (look.Pattern is VoxelPattern.Strata or VoxelPattern.Mottle ? BandsTextureBit : 0)
                    | (uint)MathF.Round(Math.Clamp(look.TextureColourMatch, 0f, 1f) * 255f) << 24
                : 0,
            Colour = look.Colour,
            Look = look.PackLook(),
            TintColour = look.TintColour,
            Amounts = look.PackAmounts(),
            Accent = look.Accent,
            Glow = look.Glow,
        };

    private static uint Unit(float value) => (uint)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
