using Itera.Engine.Assets;

namespace Itera.Engine.Tests.Assets;

public sealed class MipChainTests
{
    [Fact]
    public void Level_counts_run_down_to_one_texel()
    {
        Assert.Equal(1, MipChain.LevelCount(1, 1));
        Assert.Equal(3, MipChain.LevelCount(4, 4));
        Assert.Equal(9, MipChain.LevelCount(300, 200));
        Assert.Equal(3, MipChain.LevelCount(1, 5));
    }

    [Fact]
    public void Level_sizes_halve_rounded_down_and_stop_at_one()
    {
        (int, int)[] expected = [(300, 200), (150, 100), (75, 50), (37, 25), (18, 12), (9, 6), (4, 3), (2, 1), (1, 1)];

        for (var level = 0; level < expected.Length; level++)
        {
            Assert.Equal(expected[level], MipChain.LevelSize(300, 200, level));
        }
    }

    [Theory]
    [InlineData(MeshTextureRole.Colour, MeshTextureFormat.Rgba8Srgb, 4)]
    [InlineData(MeshTextureRole.Linear, MeshTextureFormat.Rgba8Unorm, 4)]
    [InlineData(MeshTextureRole.Normal, MeshTextureFormat.Rg8Unorm, 2)]
    public void The_chain_is_packed_in_the_roles_format(
        MeshTextureRole role,
        MeshTextureFormat expectedFormat,
        int bytes
    )
    {
        const int Width = 300;
        const int Height = 200;
        var rgba = new byte[Width * Height * 4];
        for (var i = 0; i < Width * Height; i++)
        {
            rgba[i * 4 + 2] = 255;
        }

        var chain = MipChain.Build(rgba, Width, Height, role, out var format, out var levels);

        Assert.Equal(expectedFormat, format);
        Assert.Equal(bytes, MipChain.BytesPerTexel(format));
        Assert.Equal(9, levels);
        var total = 0;
        for (var level = 0; level < levels; level++)
        {
            Assert.Equal(total, MipChain.LevelOffset(Width, Height, level, format));
            var (w, h) = MipChain.LevelSize(Width, Height, level);
            total += w * h * bytes;
        }

        Assert.Equal(total, chain.Length);
        Assert.Equal(total, MipChain.LevelOffset(Width, Height, levels, format));
    }

    [Fact]
    public void Colour_is_averaged_in_linear_light()
    {
        byte[] checker = [0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255];

        var chain = MipChain.Build(checker, 2, 2, MeshTextureRole.Colour, out var format, out var levels);

        Assert.Equal(MeshTextureFormat.Rgba8Srgb, format);
        Assert.Equal(2, levels);
        Assert.Equal(16 + 4, chain.Length);
        // Linear 0.5 is 187.5 in sRGB bytes.
        Assert.Equal(new byte[] { 188, 188, 188, 255 }, chain[16..]);
    }

    [Fact]
    public void Linear_is_averaged_as_stored()
    {
        byte[] checker = [0, 0, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 255];

        var chain = MipChain.Build(checker, 2, 2, MeshTextureRole.Linear, out _, out _);

        Assert.Equal(new byte[] { 128, 128, 128, 255 }, chain[16..]);
    }

    [Fact]
    public void Alpha_is_averaged_as_stored_in_the_colour_role()
    {
        byte[] texels = [10, 20, 30, 0, 10, 20, 30, 255, 10, 20, 30, 255, 10, 20, 30, 0];

        var chain = MipChain.Build(texels, 2, 2, MeshTextureRole.Colour, out _, out _);

        Assert.Equal(128, chain[19]);
        // Equal colours stay equal through the linear round trip.
        Assert.Equal(10, chain[16]);
        Assert.Equal(20, chain[17]);
        Assert.Equal(30, chain[18]);
    }

    [Fact]
    public void An_uneven_row_averages_every_texel_with_equal_weight()
    {
        byte[] texels = [0, 0, 0, 255, 30, 30, 30, 255, 60, 60, 60, 255];

        var chain = MipChain.Build(texels, 3, 1, MeshTextureRole.Linear, out _, out var levels);

        Assert.Equal(2, levels);
        Assert.Equal(12 + 4, chain.Length);
        Assert.Equal(new byte[] { 30, 30, 30, 255 }, chain[12..]);
    }

    [Fact]
    public void Opposite_tilts_average_to_straight_up()
    {
        var left = NormalBytes(0.6, 0, 0.8);
        var right = NormalBytes(-0.6, 0, 0.8);
        byte[] texels = [left[0], left[1], left[2], 255, right[0], right[1], right[2], 255];

        var chain = MipChain.Build(texels, 2, 1, MeshTextureRole.Normal, out var format, out var levels);

        Assert.Equal(MeshTextureFormat.Rg8Unorm, format);
        Assert.Equal(2, levels);
        Assert.Equal(4 + 2, chain.Length);
        Assert.InRange(chain[4], 127, 128);
        Assert.InRange(chain[5], 127, 128);
    }

    [Fact]
    public void Averaged_normals_keep_their_strength()
    {
        // Two vectors tilted 90 degrees apart average to a vector tilted halfway, of unit length again.
        var a = NormalBytes(1, 0, 0);
        var b = NormalBytes(0, 0, 1);
        byte[] texels = [a[0], a[1], a[2], 255, b[0], b[1], b[2], 255];

        var chain = MipChain.Build(texels, 2, 1, MeshTextureRole.Normal, out _, out _);

        var x = chain[4] / 255.0 * 2 - 1;
        var y = chain[5] / 255.0 * 2 - 1;
        var z = Math.Sqrt(Math.Max(0, 1 - x * x - y * y));
        Assert.Equal(Math.Sqrt(0.5), x, 0.01);
        Assert.Equal(Math.Sqrt(0.5), z, 0.01);
        Assert.Equal(0, y, 0.01);
    }

    [Fact]
    public void Normals_that_cancel_become_straight_up()
    {
        // (1, 1, 1) and (-1, -1, -1) sum to nothing.
        byte[] texels = [255, 255, 255, 255, 0, 0, 0, 255];

        var chain = MipChain.Build(texels, 2, 1, MeshTextureRole.Normal, out _, out _);

        Assert.InRange(chain[4], 127, 128);
        Assert.InRange(chain[5], 127, 128);
    }

    [Fact]
    public void Level_zero_of_a_normal_map_is_the_inputs_x_and_y()
    {
        var tilts = new (double X, double Y, double Z)[] { (0.6, 0, 0.8), (0, -0.6, 0.8), (0, 0, 1), (0.28, 0.96, 0) };
        var texels = new byte[tilts.Length * 4];
        for (var i = 0; i < tilts.Length; i++)
        {
            var bytes = NormalBytes(tilts[i].X, tilts[i].Y, tilts[i].Z);
            texels[i * 4] = bytes[0];
            texels[i * 4 + 1] = bytes[1];
            texels[i * 4 + 2] = bytes[2];
            texels[i * 4 + 3] = 255;
        }

        var chain = MipChain.Build(texels, tilts.Length, 1, MeshTextureRole.Normal, out _, out _);

        for (var i = 0; i < tilts.Length; i++)
        {
            Assert.InRange(chain[i * 2], texels[i * 4] - 1, texels[i * 4] + 1);
            Assert.InRange(chain[i * 2 + 1], texels[i * 4 + 1] - 1, texels[i * 4 + 1] + 1);
        }
    }

    [Fact]
    public void A_column_one_texel_wide_works()
    {
        byte[] texels = [0, 0, 0, 255, 50, 50, 50, 255, 100, 100, 100, 255, 150, 150, 150, 255, 200, 200, 200, 255];

        var chain = MipChain.Build(texels, 1, 5, MeshTextureRole.Linear, out _, out var levels);

        Assert.Equal(3, levels);
        Assert.Equal(5 * 4 + 2 * 4 + 4, chain.Length);
        Assert.Equal(texels, chain[..20]);
        // Level 1 is 1x2: rows 0 to 2 (the boxes overlap on row 2), then rows 2 to 4.
        Assert.Equal(50, chain[20]);
        Assert.Equal(150, chain[24]);
        // Level 2 is 1x1: the two rows above.
        Assert.Equal(100, chain[28]);
    }

    private static byte[] NormalBytes(double x, double y, double z) => [Encode(x), Encode(y), Encode(z)];

    private static byte Encode(double value) => (byte)Math.Round((value * 0.5 + 0.5) * 255);
}
