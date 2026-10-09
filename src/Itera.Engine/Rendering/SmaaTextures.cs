using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering;

/// <summary>
/// SMAA's two lookup textures (<see cref="SmaaPass"/>): the area texture, the share of a pixel each edge shape covers,
/// and the search texture, how far an edge search ends. They are the bytes of <c>areaTexBytes</c> and
/// <c>searchTexBytes</c> in SMAA's <c>Textures/AreaTex.h</c> and <c>Textures/SearchTex.h</c> (github.com/iryoku/smaa
/// at commit 71c806a838bdd7d517df19192a20f0c61b3ca29d, MIT licence in <c>Smaa/LICENSE.txt</c>), rows from the top as
/// the headers give them, in <c>Smaa/</c> next to the executable.
/// </summary>
public static class SmaaTextures
{
    /// <summary>The area texture's width in texels.</summary>
    public const uint AreaWidth = 160;

    /// <summary>The area texture's height in texels.</summary>
    public const uint AreaHeight = 560;

    /// <summary>The area texture's format: two bytes a texel.</summary>
    public const Format AreaFormat = Format.R8G8Unorm;

    /// <summary>The search texture's width in texels.</summary>
    public const uint SearchWidth = 64;

    /// <summary>The search texture's height in texels.</summary>
    public const uint SearchHeight = 16;

    /// <summary>The search texture's format: one byte a texel.</summary>
    public const Format SearchFormat = Format.R8Unorm;

    /// <summary>The area texture's size in bytes.</summary>
    public const int AreaBytes = (int)(AreaWidth * AreaHeight * 2);

    /// <summary>The search texture's size in bytes.</summary>
    public const int SearchBytes = (int)(SearchWidth * SearchHeight);

    /// <summary>The folder that holds the two files: <c>Smaa/</c> next to the executable.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Smaa");

    /// <summary>Reads the area texture's bytes; throws when the file is missing or not <see cref="AreaBytes"/> long.</summary>
    public static byte[] ReadArea() => Read("AreaTex.bin", AreaBytes);

    /// <summary>Reads the search texture's bytes; throws when the file is missing or not <see cref="SearchBytes"/> long.</summary>
    public static byte[] ReadSearch() => Read("SearchTex.bin", SearchBytes);

    private static byte[] Read(string name, int length)
    {
        var path = Path.Combine(Folder, name);
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != length)
        {
            throw new InvalidDataException($"{path} holds {bytes.Length} bytes; SMAA needs {length}.");
        }

        return bytes;
    }
}
