using System.Security.Cryptography;
using Itera.Engine.Rendering;

namespace Itera.Engine.Tests.Rendering;

public sealed class SmaaTexturesTests
{
    [Fact]
    public void The_area_texture_is_the_bytes_of_smaa_s_header()
    {
        var bytes = SmaaTextures.ReadArea();

        Assert.Equal(SmaaTextures.AreaBytes, bytes.Length);
        Assert.Equal(
            "35065cef2a02cabcad711d6bf430239ae64e27d71c4e4fa06f29cce2c992f0d2",
            Convert.ToHexStringLower(SHA256.HashData(bytes))
        );
    }

    [Fact]
    public void The_search_texture_is_the_bytes_of_smaa_s_header()
    {
        var bytes = SmaaTextures.ReadSearch();

        Assert.Equal(SmaaTextures.SearchBytes, bytes.Length);
        Assert.Equal(
            "3694eae5e9d44b8ebb4415a13f8c7b94dc08a2fc86658434d771c4610fe5744d",
            Convert.ToHexStringLower(SHA256.HashData(bytes))
        );
    }

    [Fact]
    public void The_licence_ships_beside_the_textures()
    {
        var licence = File.ReadAllText(Path.Combine(SmaaTextures.Folder, "LICENSE.txt"));

        Assert.Contains("Jorge Jimenez", licence, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", licence, StringComparison.Ordinal);
    }
}
