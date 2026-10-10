using System.Numerics;

namespace Itera.Engine.Assets;

/// <summary>
/// A mesh asset's material, as glTF 2.0 gives it (its metallic-roughness model): factors and the textures they multiply.
/// The engine draws the base colour, the normal map, the occlusion, and the emission with the world's lighting; it keeps
/// the metallic and roughness factors and texture but does not draw them yet.
/// </summary>
public sealed record MeshMaterialData
{
    /// <summary>The material of a primitive whose file names none: white, opaque, rough, single-sided (glTF's default).</summary>
    public static MeshMaterialData Default { get; } = new();

    public string Name { get; init; } = "";

    /// <summary>Linear RGBA the base colour texture is multiplied by.</summary>
    public Vector4 BaseColour { get; init; } = Vector4.One;

    public MeshTextureRef BaseColourTexture { get; init; } = MeshTextureRef.None;

    /// <summary>Linear RGB the surface gives off, the emissive texture multiplied by it (and by its strength already).</summary>
    public Vector3 Emission { get; init; }

    public MeshTextureRef EmissionTexture { get; init; } = MeshTextureRef.None;

    public MeshTextureRef NormalTexture { get; init; } = MeshTextureRef.None;

    /// <summary>How much the normal map's x and y tilt the normal.</summary>
    public float NormalScale { get; init; } = 1;

    /// <summary>Occlusion in the texture's red channel.</summary>
    public MeshTextureRef OcclusionTexture { get; init; } = MeshTextureRef.None;

    /// <summary>How much of the occlusion applies, 0 to 1.</summary>
    public float OcclusionStrength { get; init; } = 1;

    /// <summary>Roughness in the texture's green channel and metalness in its blue.</summary>
    public MeshTextureRef MetalRoughTexture { get; init; } = MeshTextureRef.None;

    public float Metallic { get; init; } = 1;

    public float Roughness { get; init; } = 1;

    public MeshAlphaMode AlphaMode { get; init; }

    /// <summary>With <see cref="MeshAlphaMode.Mask"/>, the alpha below which a pixel is not drawn.</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>Both faces drawn (and lit from the side seen), not only the front.</summary>
    public bool DoubleSided { get; init; }
}

/// <summary>How a material's alpha is used (glTF's alphaMode).</summary>
public enum MeshAlphaMode
{
    /// <summary>Alpha is ignored.</summary>
    Opaque,

    /// <summary>A pixel whose alpha is below the cutoff is not drawn.</summary>
    Mask,

    /// <summary>Blended with what is behind; drawn as <see cref="Mask"/> until the engine has a transparent pass.</summary>
    Blend,
}

/// <summary>
/// A material's texture: its index in the asset's textures (-1 for none: the material's factor alone), how it is
/// sampled, and which texture coordinates (0 or 1) it reads.
/// </summary>
public readonly record struct MeshTextureRef(int Texture, MeshSampler Sampler, int UvSet)
{
    public static MeshTextureRef None { get; } = new(-1, MeshSampler.LinearRepeat, 0);

    public bool IsNone => Texture < 0;
}

/// <summary>How a texture is filtered and what happens past its edges (glTF's sampler, wrapS for both directions).</summary>
public enum MeshSampler
{
    LinearRepeat,
    LinearClamp,
    LinearMirror,
    NearestRepeat,
    NearestClamp,
    NearestMirror,
}
