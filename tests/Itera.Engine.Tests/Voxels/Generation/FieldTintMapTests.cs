using System.Numerics;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary><see cref="FieldTintMap"/> and the CPU's read of any tint map, <see cref="VoxelTintSampler"/>.</summary>
public sealed class FieldTintMapTests
{
    private const int Size = IVoxelTintMap.Size;
    private const int OriginX = -65_536;
    private const int OriginZ = -65_536;
    private const int Texel = 512;

    // A smooth field from 0.1 to 0.9 across the map.
    private static double Field(int x, int z) =>
        0.1 + 0.4 * (x - OriginX) / (Size * Texel) + 0.4 * (z - OriginZ) / (Size * Texel);

    private static byte Quantised(double value) => (byte)Math.Round(value * 255);

    private static (int X, int Z) Centre(int texelX, int texelZ) =>
        (OriginX + texelX * Texel + Texel / 2, OriginZ + texelZ * Texel + Texel / 2);

    [Fact]
    public void The_texels_follow_the_field_at_their_centres()
    {
        var map = new FieldTintMap(Field, OriginX, OriginZ);
        Assert.Equal(9, map.TexelShift);
        Assert.Equal(OriginX, map.OriginX);
        Assert.Equal(OriginZ, map.OriginZ);

        var texels = new byte[Size * Size];
        map.Write(texels);
        for (var z = 0; z < Size; z++)
        {
            for (var x = 0; x < Size; x++)
            {
                var (centreX, centreZ) = Centre(x, z);
                Assert.Equal(Quantised(Field(centreX, centreZ)), texels[z * Size + x]);
            }
        }

        // The field rises along both axes, so the corners differ.
        Assert.True(texels[0] < texels[Size * Size - 1]);
    }

    [Fact]
    public void Values_outside_zero_to_one_are_clamped_and_a_smaller_texel_is_honoured()
    {
        var map = new FieldTintMap((x, _) => x < 0 ? -3 : 7, 0, 0, texelShift: 4);
        Assert.Equal(4, map.TexelShift);
        var texels = new byte[Size * Size];
        map.Write(texels);

        // Texels of 16 cm from x = 0: all of them centred at x >= 8.
        Assert.All(texels, texel => Assert.Equal(255, texel));

        var low = new FieldTintMap((x, z) => x + z < -4096 ? -3 : 0.5, -(Size * 16), -(Size * 16), texelShift: 4);
        low.Write(texels);
        Assert.Equal(0, texels[0]);
        Assert.Equal(Quantised(0.5), texels[Size * Size - 1]);
    }

    [Fact]
    public void The_field_is_asked_when_the_map_is_written_not_before()
    {
        var calls = 0;
        var map = new FieldTintMap(
            (_, _) =>
            {
                calls++;
                return 0.25;
            },
            0,
            0
        );
        Assert.Equal(0, calls);
        map.Write(new byte[Size * Size]);
        Assert.Equal(Size * Size, calls);
    }

    [Fact]
    public void Wrong_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new FieldTintMap(null!, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FieldTintMap(Field, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FieldTintMap(Field, 0, 0, 21));
    }

    [Fact]
    public void The_sampler_returns_the_fields_values_at_the_texel_centres()
    {
        var sampler = new VoxelTintSampler(new FieldTintMap(Field, OriginX, OriginZ));
        for (var z = 0; z < Size; z += 5)
        {
            for (var x = 0; x < Size; x += 3)
            {
                var (centreX, centreZ) = Centre(x, z);
                var metres = new Vector3(centreX / 100f, 0, centreZ / 100f);
                Assert.Equal(Quantised(Field(centreX, centreZ)) / 255.0, sampler.At(metres), 0.001);
            }
        }
    }

    [Fact]
    public void The_sampler_blends_between_centres_and_repeats_the_edge_outside()
    {
        var sampler = new VoxelTintSampler(new FieldTintMap(Field, OriginX, OriginZ));

        // Halfway between two neighbouring centres is the mean of their texels.
        var (ax, az) = Centre(100, 80);
        var (bx, _) = Centre(101, 80);
        var middle = new Vector3((ax + bx) / 2f / 100f, 0, az / 100f);
        var mean = (Quantised(Field(ax, az)) + Quantised(Field(bx, az))) / 2.0 / 255.0;
        Assert.Equal(mean, sampler.At(middle), 0.001);

        // Past the map's edge the nearest edge texel repeats, however far.
        var (cornerX, cornerZ) = Centre(0, 0);
        Assert.Equal(Quantised(Field(cornerX, cornerZ)) / 255.0, sampler.At(new Vector3(-9000, 0, -9000)), 0.001);
        var (farX, farZ) = Centre(Size - 1, Size - 1);
        Assert.Equal(Quantised(Field(farX, farZ)) / 255.0, sampler.At(new Vector3(9000, 5, 9000)), 0.001);
    }

    [Fact]
    public void The_sampler_reads_a_map_once_when_it_is_made()
    {
        var value = 0.2;
        var sampler = new VoxelTintSampler(new FieldTintMap((_, _) => value, OriginX, OriginZ));
        value = 0.9;
        Assert.Equal(Quantised(0.2) / 255.0, sampler.At(new Vector3(0, 0, 0)), 0.001);
    }

    [Fact]
    public void The_default_map_tints_nothing()
    {
        var sampler = new VoxelTintSampler(new NoVoxelTintMap());
        Assert.Equal(0, sampler.At(new Vector3(12, 3, -40)));
    }
}
