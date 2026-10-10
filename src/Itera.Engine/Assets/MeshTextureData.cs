namespace Itera.Engine.Assets;

/// <summary>
/// A mesh asset's texture with every mip level (<see cref="MipChain"/>), level 0 first, tightly packed in
/// <see cref="Format"/>; <see cref="Role"/> says what it holds. <see cref="Name"/> says where it came from (the file and
/// the image), for log lines.
/// </summary>
public sealed class MeshTextureData(
    string name,
    int width,
    int height,
    int levels,
    MeshTextureRole role,
    MeshTextureFormat format,
    byte[] texels
)
{
    public string Name { get; } = name;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Levels { get; } = levels;

    public MeshTextureRole Role { get; } = role;

    public MeshTextureFormat Format { get; } = format;

    public byte[] Texels { get; } = texels;
}
