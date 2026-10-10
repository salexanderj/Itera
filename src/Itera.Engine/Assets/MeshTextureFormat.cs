namespace Itera.Engine.Assets;

/// <summary>
/// The GPU format a mesh texture's texels are in: 4, 4, and 2 bytes a texel. Block-compressed formats come later.
/// </summary>
public enum MeshTextureFormat
{
    /// <summary>Four bytes a texel, RGB sRGB-encoded and alpha linear.</summary>
    Rgba8Srgb,

    /// <summary>Four bytes a texel, all channels linear.</summary>
    Rgba8Unorm,

    /// <summary>Two bytes a texel: the x and y of a normal (the shader rebuilds z).</summary>
    Rg8Unorm,
}
