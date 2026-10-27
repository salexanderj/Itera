using Itera.Engine.Rendering.Meshes;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The clouds' noise texture (<see cref="CloudNoise"/>): every mip level down to one texel, each the mean of the one
/// above; the largest tiles, meeting itself across its edges as smoothly as across its middle; and its channels spread
/// over their range.
/// </summary>
public sealed class CloudNoiseTests
{
    private static readonly byte[] s_texels = Write();

    [Fact]
    public void Every_level_down_to_one_texel_is_the_mean_of_the_one_above()
    {
        Assert.Equal(CloudNoise.Texels, 1 << (CloudNoise.Mips - 1));
        Assert.Equal(1, CloudNoise.SideOf(CloudNoise.Mips - 1));
        Assert.Equal(CloudNoise.OffsetOf(CloudNoise.Mips - 1) + 4, CloudNoise.Bytes);
        for (var mip = 1; mip < CloudNoise.Mips; mip++)
        {
            var side = CloudNoise.SideOf(mip);
            var above = CloudNoise.SideOf(mip - 1);
            foreach (var (x, z) in new[] { (0, 0), (side - 1, side / 2), (side / 3, side - 1) })
            {
                for (var channel = 0; channel < 4; channel++)
                {
                    var mean =
                        (
                            At(mip - 1, above, 2 * x, 2 * z, channel)
                            + At(mip - 1, above, 2 * x + 1, 2 * z, channel)
                            + At(mip - 1, above, 2 * x, 2 * z + 1, channel)
                            + At(mip - 1, above, 2 * x + 1, 2 * z + 1, channel)
                        ) / 4.0;
                    // Each level is made from the one above's unrounded values, so it may differ by a level of rounding.
                    Assert.InRange(At(mip, side, x, z, channel) - mean, -2.5, 2.5);
                }
            }
        }
    }

    [Fact]
    public void The_largest_level_meets_itself_across_its_edges_as_smoothly_as_across_its_middle()
    {
        const int Side = CloudNoise.Texels;
        for (var channel = 0; channel < 4; channel++)
        {
            double across = 0;
            double middle = 0;
            for (var z = 0; z < Side; z++)
            {
                across += Math.Abs(At(0, Side, Side - 1, z, channel) - At(0, Side, 0, z, channel));
                middle += Math.Abs(At(0, Side, Side / 2 - 1, z, channel) - At(0, Side, Side / 2, z, channel));
            }

            // A seam would step far more than neighbours step anywhere else.
            Assert.True(
                across < middle * 1.5 + Side,
                $"channel {channel}: across the edge {across}, the middle {middle}"
            );
        }
    }

    [Fact]
    public void Each_channel_spreads_over_its_range()
    {
        const int Side = CloudNoise.Texels;
        for (var channel = 0; channel < 4; channel++)
        {
            int least = 255;
            int most = 0;
            for (var i = channel; i < Side * Side * 4; i += 4)
            {
                least = Math.Min(least, s_texels[i]);
                most = Math.Max(most, s_texels[i]);
            }

            // Over half the range: the gradient noise's three octaves rarely all peak together, so it spans the least.
            Assert.True(most - least > 128, $"channel {channel} spans {least} to {most}");
        }
    }

    private static byte[] Write()
    {
        var texels = new byte[CloudNoise.Bytes];
        CloudNoise.Write(texels);
        return texels;
    }

    private static int At(int mip, int side, int x, int z, int channel) =>
        s_texels[CloudNoise.OffsetOf(mip) + ((z * side) + x) * 4 + channel];
}
