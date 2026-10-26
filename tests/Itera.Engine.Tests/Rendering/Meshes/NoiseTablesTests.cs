using Itera.Engine.Rendering.Meshes;

namespace Itera.Engine.Tests.Rendering.Meshes;

public sealed class NoiseTablesTests
{
    [Fact]
    public void Each_texel_holds_its_cells_hash_in_sixteen_bits_the_cells_round_the_middle()
    {
        var texels = new byte[NoiseTables.Bytes];
        NoiseTables.Write(texels);

        Assert.Equal(0, NoiseTables.CellOf(0));
        Assert.Equal(511, NoiseTables.CellOf(511));
        Assert.Equal(-512, NoiseTables.CellOf(512));
        Assert.Equal(-1, NoiseTables.CellOf(NoiseTables.Cells - 1));
        for (var layer = 0; layer < NoiseTables.Salts.Length; layer++)
        {
            foreach (var (x, z) in new[] { (0, 0), (5, 7), (511, 3), (512, 900), (1023, 1023) })
            {
                var at = 2 * ((layer * NoiseTables.Cells * NoiseTables.Cells) + (z * NoiseTables.Cells) + x);
                var stored = texels[at] | (texels[at + 1] << 8);
                var hash = NoiseTables.LatticeHash(
                    NoiseTables.CellOf(x),
                    0,
                    NoiseTables.CellOf(z),
                    NoiseTables.Salts[layer]
                );
                Assert.InRange(stored / 65535f - hash, -0.51f / 65535, 0.51f / 65535);
            }
        }
    }

    [Fact]
    public void The_hash_is_the_shaders()
    {
        // surface-noise.glsl's latticeHash of (3, 0, -2) with salt 41, worked by hand: the axes' products xored with the
        // salt, then one xor-shift and multiply, its top 24 bits.
        var h = unchecked((3u * 0x8DA6B343u) ^ ((uint)-2 * 0xCB1AB31Fu) ^ 41u);
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        Assert.Equal((h >> 8) / 16777216f, NoiseTables.LatticeHash(3, 0, -2, 41));
        Assert.InRange(NoiseTables.LatticeHash(-512, 0, 511, 1), 0f, 1f);
    }
}
