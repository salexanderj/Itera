namespace Itera.Engine.Assets;

/// <summary>
/// A mesh asset's texture with every mip level (<see cref="MipChain"/>), level 0 first, tightly packed in
/// <see cref="Format"/> (a compressed format's levels in whole 4x4 blocks, <see cref="MipChain.LevelBytes"/>);
/// <see cref="Role"/> says what it holds. <see cref="Name"/> says where it came from (the file and the image), for log
/// lines; <see cref="Image"/> and <see cref="SourceHash"/> say which of a model's images it was made from.
/// </summary>
public sealed class MeshTextureData(
    string name,
    int width,
    int height,
    int levels,
    MeshTextureRole role,
    MeshTextureFormat format,
    byte[] texels,
    int image = -1,
    string sourceHash = ""
)
{
    public string Name { get; } = name;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Levels { get; } = levels;

    public MeshTextureRole Role { get; } = role;

    public MeshTextureFormat Format { get; } = format;

    public byte[] Texels { get; } = texels;

    /// <summary>The model's image it was made from (glTF's index); -1 for a texture made in code.</summary>
    public int Image { get; } = image;

    /// <summary>The SHA-256 of that image's bytes (<see cref="CompressedTextures.Hash"/>); empty for a texture made in code.</summary>
    public string SourceHash { get; } = sourceHash;

    /// <summary>True when its texels are block-compressed (read from a model's compressed textures).</summary>
    public bool Compressed => MipChain.CompressedFormat(Format) is not null;

    /// <summary>
    /// A texture made in code from straight-alpha RGBA8 texels (<paramref name="rgba"/>, rows top first,
    /// <paramref name="width"/> by <paramref name="height"/>), with every mip level made for its role as a file's are:
    /// for a game that builds a mesh asset in code (<c>MeshAssets.Add</c>).
    /// </summary>
    public static MeshTextureData FromRgba(string name, int width, int height, MeshTextureRole role, byte[] rgba)
    {
        var chain = MipChain.Build(rgba, width, height, role, out var format, out var levels);
        return new MeshTextureData(name, width, height, levels, role, format, chain);
    }
}
