namespace Itera.Engine.Assets;

/// <summary>What a texture inside a model file holds, which sets how its texels are stored and filtered.</summary>
public enum MeshTextureRole
{
    /// <summary>RGBA whose RGB is sRGB-encoded: a material's base colour and emission.</summary>
    Colour,

    /// <summary>
    /// RGBA stored linearly: occlusion in R, and for metallic-roughness, roughness in G and metallic in B.
    /// </summary>
    Linear,

    /// <summary>
    /// A tangent-space normal map: RGB = xyz * 0.5 + 0.5, +X right, +Y up, +Z out of the surface.
    /// </summary>
    Normal,
}
