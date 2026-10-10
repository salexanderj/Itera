using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// A mesh asset's material as the asset shaders read it (<c>mesh-asset-data.glsl</c>'s <c>MeshAssetMaterial</c>, set 1
/// binding 3), 72 bytes: the base colour (linear RGBA), the emission (linear RGB, its strength in it), the alpha cutoff,
/// five texture references (<see cref="PackTexture"/>: the texture's index among the pass's in bits 0 to 15,
/// <see cref="NoTexture"/> for none; its sampler in bits 16 to 18; its texture coordinates, 0 or 1, in bit 19), the
/// normal map's scale, the occlusion's strength, metalness and roughness (kept, not drawn yet), and flags
/// (<see cref="Masked"/>, <see cref="TwoSided"/>). Written once, at the pass's start.
/// </summary>
public struct MeshAssetMaterial
{
    public Vector4 BaseColour;

    public Vector3 Emission;

    public float AlphaCutoff;

    public uint BaseColourTexture;

    public uint NormalTexture;

    public uint OcclusionTexture;

    public uint EmissionTexture;

    public uint MetalRoughTexture;

    public float NormalScale;

    public float OcclusionStrength;

    public float Metallic;

    public float Roughness;

    public uint Flags;

    /// <summary>A texture reference's index for none: the material's factor alone.</summary>
    public const uint NoTexture = 0xFFFFu;

    /// <summary><see cref="Flags"/>: pixels whose alpha is below the cutoff are not drawn (glTF's MASK).</summary>
    public const uint Masked = 1u;

    /// <summary><see cref="Flags"/>: both faces drawn, the back lit from its own side.</summary>
    public const uint TwoSided = 2u;

    /// <summary>
    /// The material <paramref name="material"/> with its textures' indices among the pass's: an asset's texture i is
    /// <paramref name="firstTexture"/> + i, or none when that is <paramref name="textureCapacity"/> or more.
    /// </summary>
    public static MeshAssetMaterial From(MeshMaterialData material, int firstTexture, int textureCapacity)
    {
        ArgumentNullException.ThrowIfNull(material);
        return new MeshAssetMaterial
        {
            BaseColour = material.BaseColour,
            Emission = material.Emission,
            AlphaCutoff = material.AlphaCutoff,
            BaseColourTexture = PackTexture(material.BaseColourTexture, firstTexture, textureCapacity),
            NormalTexture = PackTexture(material.NormalTexture, firstTexture, textureCapacity),
            OcclusionTexture = PackTexture(material.OcclusionTexture, firstTexture, textureCapacity),
            EmissionTexture = PackTexture(material.EmissionTexture, firstTexture, textureCapacity),
            MetalRoughTexture = PackTexture(material.MetalRoughTexture, firstTexture, textureCapacity),
            NormalScale = material.NormalScale,
            OcclusionStrength = material.OcclusionStrength,
            Metallic = material.Metallic,
            Roughness = material.Roughness,
            Flags = (material.AlphaMode == MeshAlphaMode.Mask ? Masked : 0u) | (material.DoubleSided ? TwoSided : 0u),
        };
    }

    /// <summary>A texture reference as the shaders read it (see the type's summary).</summary>
    public static uint PackTexture(MeshTextureRef texture, int firstTexture, int textureCapacity)
    {
        var index = texture.IsNone ? -1 : firstTexture + texture.Texture;
        var stored = index < 0 || index >= Math.Min(textureCapacity, (int)NoTexture) ? NoTexture : (uint)index;
        return stored | ((uint)texture.Sampler << 16) | ((uint)(texture.UvSet & 1) << 19);
    }
}
