namespace Itera.Engine.Assets;

/// <summary>
/// The GPU format a mesh texture's texels are in: decoded at load, 4, 4, and 2 bytes a texel; or compressed offline by
/// <c>tools/Textures</c> (<see cref="CompressedTextures"/>), 16 bytes a 4x4 block.
/// </summary>
public enum MeshTextureFormat
{
    /// <summary>Four bytes a texel, RGB sRGB-encoded and alpha linear.</summary>
    Rgba8Srgb,

    /// <summary>Four bytes a texel, all channels linear.</summary>
    Rgba8Unorm,

    /// <summary>Two bytes a texel: the x and y of a normal (the shader rebuilds z).</summary>
    Rg8Unorm,

    /// <summary>BC7, RGB sRGB-encoded and alpha linear: a colour texture compressed.</summary>
    Bc7Srgb,

    /// <summary>BC7, all channels linear: a linear texture compressed.</summary>
    Bc7Unorm,

    /// <summary>BC5, a normal's x and y: a normal map compressed.</summary>
    Bc5Unorm,
}
