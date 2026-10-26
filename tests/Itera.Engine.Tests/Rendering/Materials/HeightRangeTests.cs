using System.Numerics;
using Itera.Engine.Rendering.Materials;

namespace Itera.Engine.Tests.Rendering.Materials;

/// <summary>
/// The range of heights found from a height layer's BC4 blocks (<see cref="HeightRange.FromBc4"/>): both palette modes,
/// blocks that use some of their palette, a whole file's largest level, and a set read from disk.
/// </summary>
public sealed class HeightRangeTests
{
    [Fact]
    public void A_block_with_eight_interpolated_values_gives_the_extremes_of_the_indices_it_uses()
    {
        // Red0 above red1: index 0 is red0 (200), 1 is red1 (40), 2 to 7 lie between, 2 nearest red0.
        var block = Block(200, 40, 1, 5, 1, 5, 1, 5, 1, 5, 1, 5, 1, 5, 1, 5, 1, 5);

        var range = HeightRange.FromBc4(block);

        Assert.Equal(40 / 255f, range.Low, 1e-6f);
        Assert.Equal((3 * 200 + 4 * 40) / 7f / 255f, range.High, 1e-6f);
    }

    [Fact]
    public void A_block_with_six_interpolated_values_has_zero_and_one_at_its_ends()
    {
        // Red0 not above red1: indices 2 to 5 are interpolated, 6 is 0 and 7 is 255.
        var all = Block(50, 150, 0, 1, 2, 3, 4, 5, 6, 7, 0, 1, 2, 3, 4, 5, 6, 7);
        var middle = Block(50, 150, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3);
        var equal = Block(90, 90, 0, 1, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7);

        Assert.Equal(new HeightRange(0f, 1f), HeightRange.FromBc4(all));
        var inner = HeightRange.FromBc4(middle);
        Assert.Equal((4 * 50 + 1 * 150) / 5f / 255f, inner.Low, 1e-6f);
        Assert.Equal((3 * 50 + 2 * 150) / 5f / 255f, inner.High, 1e-6f);
        // Equal endpoints use the six-value mode too: index 7 is 255.
        Assert.Equal(new HeightRange(90 / 255f, 1f), HeightRange.FromBc4(equal));
    }

    [Fact]
    public void A_block_using_one_index_gives_that_value_alone()
    {
        var range = HeightRange.FromBc4(Block(200, 40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0));

        Assert.Equal(new HeightRange(200 / 255f, 200 / 255f), range);
    }

    [Fact]
    public void Blocks_in_both_modes_give_the_range_over_all_of_them()
    {
        var blocks = Block(120, 100, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1)
            .Concat(Block(30, 220, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2))
            .ToArray();

        var range = HeightRange.FromBc4(blocks);

        // The second block holds only (4 * 30 + 220) / 5 = 68; the first holds 100 and 120.
        Assert.Equal(68 / 255f, range.Low, 1e-6f);
        Assert.Equal(120 / 255f, range.High, 1e-6f);
    }

    [Fact]
    public void No_blocks_give_the_full_range()
    {
        Assert.Equal(HeightRange.Full, HeightRange.FromBc4([]));
        Assert.Equal(HeightRange.Full, HeightRange.FromBc4(new byte[7]));
        Assert.Equal(new HeightRange(0f, 1f), HeightRange.Full);
        Assert.Equal(HeightRange.Full, new MaterialTextureSlot(false, 0, 1f, Vector3.One).Heights);
    }

    [Fact]
    public void Random_blocks_match_a_plain_decoder()
    {
        var random = new Random(11);
        var blocks = new byte[8 * 4096];
        random.NextBytes(blocks);

        var range = HeightRange.FromBc4(blocks);

        var (low, high) = Reference(blocks);
        Assert.Equal(low, range.Low, 1e-6f);
        Assert.Equal(high, range.High, 1e-6f);
    }

    [Fact]
    public void A_file_gives_the_range_of_its_largest_level_only()
    {
        // 8x8 texels: four blocks of heights 100 to 110, then smaller levels that hold 0 and 255.
        var level0 = new List<byte>();
        for (var i = 0; i < 4; i++)
        {
            level0.AddRange(Block(110, 100, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1));
        }

        var extreme = Block(0, 255, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1);
        var mips = new List<byte[]> { level0.ToArray(), extreme, extreme, extreme };
        using var stream = new MemoryStream();
        DdsFile.Write(stream, DdsFormat.Bc4, 8, 8, mips);
        var bytes = stream.ToArray();
        var dds = DdsFile.Read(bytes);

        var range = HeightRange.FromBc4(bytes.AsSpan(dds.Offsets[0], dds.Lengths[0]));

        Assert.Equal(new HeightRange(100 / 255f, 110 / 255f), range);
    }

    [Fact]
    public void A_loaded_set_carries_the_range_of_its_height_layer()
    {
        var folder = Path.Combine(Path.GetTempPath(), "itera-heights-" + Guid.NewGuid().ToString("N"));
        try
        {
            var set = Path.Combine(folder, "a-ground");
            Directory.CreateDirectory(set);
            const int Texels = MaterialTextureSets.SmallTexels;
            byte[] heights = [];
            for (var i = 0; i < MaterialTextureSets.LayerFiles.Length; i++)
            {
                var format = MaterialTextureSets.LayerFormats[i];
                var random = new Random(i);
                var levels = new List<byte[]>();
                for (var m = 0; m < DdsFile.MipCount(Texels, Texels); m++)
                {
                    var level = new byte[DdsFile.MipBytes(format, Texels, Texels, m)];
                    random.NextBytes(level);
                    levels.Add(level);
                }

                if (format == DdsFormat.Bc4)
                {
                    // Red0 from 100 to 199 and red1 from 60 to 98 keep every block in the eight-value mode, within 60 to 199.
                    heights = levels[0];
                    for (var b = 0; b < heights.Length; b += 8)
                    {
                        heights[b] = (byte)(100 + heights[b] % 100);
                        heights[b + 1] = (byte)(60 + heights[b + 1] % 39);
                    }
                }

                using var file = File.Create(Path.Combine(set, MaterialTextureSets.LayerFiles[i]));
                DdsFile.Write(file, format, Texels, Texels, levels);
            }

            File.WriteAllText(
                Path.Combine(set, "set.json"),
                """{ "texels": 1024, "metres": 2, "mean": [0.1, 0.2, 0.3], "site": "test" }"""
            );
            var sets = new MaterialTextureSets();

            sets.Load(folder, (name, why) => Assert.Fail($"{name}: {why}"));

            var (low, high) = Reference(heights);
            Assert.True(sets.TryFind("a-ground", out var slot));
            Assert.Equal(low, slot.Heights.Low, 1e-6f);
            Assert.Equal(high, slot.Heights.High, 1e-6f);
            Assert.Equal(slot.Heights, sets.Small[0].Heights);
            Assert.InRange(slot.Heights.Low, 60 / 255f, 100 / 255f);
            Assert.InRange(slot.Heights.High, 100 / 255f, 199 / 255f);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // A block: the endpoints, then sixteen 3-bit indices, texel 0 in the lowest bits.
    private static byte[] Block(byte red0, byte red1, params int[] indices)
    {
        Assert.Equal(16, indices.Length);
        ulong bits = 0;
        for (var i = 0; i < 16; i++)
        {
            bits |= (ulong)indices[i] << (3 * i);
        }

        var block = new byte[8];
        block[0] = red0;
        block[1] = red1;
        for (var i = 0; i < 6; i++)
        {
            block[2 + i] = (byte)(bits >> (8 * i));
        }

        return block;
    }

    // The specification's decoder in doubles, one texel at a time.
    private static (float Low, float High) Reference(byte[] blocks)
    {
        var low = double.MaxValue;
        var high = double.MinValue;
        for (var b = 0; b < blocks.Length; b += 8)
        {
            double r0 = blocks[b];
            double r1 = blocks[b + 1];
            var palette = new double[8];
            palette[0] = r0;
            palette[1] = r1;
            if (r0 > r1)
            {
                for (var i = 2; i < 8; i++)
                {
                    palette[i] = ((8 - i) * r0 + (i - 1) * r1) / 7;
                }
            }
            else
            {
                for (var i = 2; i < 6; i++)
                {
                    palette[i] = ((6 - i) * r0 + (i - 1) * r1) / 5;
                }

                palette[6] = 0;
                palette[7] = 255;
            }

            for (var t = 0; t < 16; t++)
            {
                var bit = 16 + 3 * t;
                var index = 0;
                for (var k = 0; k < 3; k++)
                {
                    var position = bit + k;
                    index |= ((blocks[b + position / 8] >> (position % 8)) & 1) << k;
                }

                low = Math.Min(low, palette[index]);
                high = Math.Max(high, palette[index]);
            }
        }

        return ((float)(low / 255), (float)(high / 255));
    }
}
