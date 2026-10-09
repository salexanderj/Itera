using System.Numerics;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// The ground's height where the world's densities put it (<see cref="VoxelWorld.ColumnTops"/>,
/// <see cref="VoxelWorld.SurfaceOver"/>): on sloped ground of 10 cm voxels the heights at the columns' centres, blended
/// between them, lie within a centimetre of the surface the mesher draws (on a 30 degree slope the columns' own heights
/// were 3.6 cm off it, and the share of each column below the ground 2.9 cm, measured 2026-10-07); at the materials'
/// default densities a column's surface is its top.
/// </summary>
public sealed class VoxelSurfaceTests
{
    [Theory]
    [InlineData(10.0, 0.0)]
    [InlineData(30.0, 0.0)]
    [InlineData(30.0, 20.0)]
    [InlineData(44.0, 0.0)]
    public void Heights_blended_between_the_columns_lie_on_the_drawn_surface(double degrees, double degreesZ)
    {
        var grid = new VoxelGrid(10);
        var generator = new SlopedGroundGenerator(
            grid,
            Math.Tan(degrees * Math.PI / 180),
            Math.Tan(degreesZ * Math.PI / 180)
        );
        using var world = TestWorlds.Load(generator, new Vector3(1.6f, 2, 1.6f), radiusMetres: 3);
        var samples = new byte[SurfaceNets.SampleCount];
        var densities = new byte[SurfaceNets.SampleCount];
        var mesher = new SurfaceNets();
        var buffers = new MeshBuffers();
        var heights = Heights(world, -1, -1, 35, 35);
        var worst = 0.0;
        var points = 0;
        for (var by = 0; by < 8; by++)
        {
            if (!world.TryReadBlock(0, 0, by, 0, samples, densities) || mesher.Mesh(samples, densities, buffers) == 0)
            {
                continue;
            }

            for (var px = 4.0; px < 28; px += 0.37)
            {
                for (var pz = 4.0; pz < 28; pz += 1.13)
                {
                    if (!MeshHeight(buffers, (float)px, (float)pz, out var meshY))
                    {
                        continue;
                    }

                    worst = Math.Max(worst, Math.Abs(Blend(heights, 35, -1, -1, px, pz) - (by * 32 + meshY)));
                    points++;
                }
            }
        }

        Assert.True(points > 1000, $"Only {points} points of the slope were drawn.");
        Assert.True(worst < 0.1, $"The blended heights lie up to {worst * 10:F2} cm from the drawn surface.");
    }

    [Fact]
    public void At_the_default_densities_a_columns_surface_is_its_top()
    {
        using var world = TestWorlds.Load(new GridShapesGenerator(new VoxelGrid(10)), new Vector3(0, 2, 0), 3);
        var tops = new int[16 * 16];
        world.ColumnTops(0, 0, 16, 16, 0, 40, default, tops);
        foreach (var top in tops)
        {
            // The floor's top is the 10 cm line above 137 cm.
            Assert.Equal(14, top);
        }

        Assert.Equal(14.0, world.SurfaceOver(3, 14, 5, default));
    }

    [Fact]
    public void A_column_s_top_is_found_at_every_place_in_its_brick()
    {
        using var world = TestWorlds.Load(new GridShapesGenerator(new VoxelGrid(10)), new Vector3(0, 2, 0), 3);
        // The stone box from 223 to 467 cm on x, 137 to 311 on y, 413 to 609 on z: its voxels 22 to 46, 13 to 31, 41 to 61
        // (sampled at their middles: 225 is in, 465 in, 135 out, 305 in, 415 in, 605 in).
        var tops = new int[30 * 30];
        world.ColumnTops(20, 39, 30, 30, 0, 60, default, tops);
        for (var z = 39; z < 69; z++)
        {
            for (var x = 20; x < 50; x++)
            {
                var inBox = x is >= 22 and <= 46 && z is >= 41 and <= 60;
                Assert.Equal(inBox ? 31 : 14, tops[x - 20 + 30 * (z - 39)]);
            }
        }
    }

    /// <summary>The surface height of each column of a window, from y 0 to 60, NaN where nothing blocks.</summary>
    private static double[] Heights(VoxelWorld world, int x0, int z0, int width, int depth)
    {
        var tops = new int[width * depth];
        world.ColumnTops(x0, z0, width, depth, 0, 60, default, tops);
        var heights = new double[width * depth];
        for (var i = 0; i < heights.Length; i++)
        {
            heights[i] =
                tops[i] == int.MinValue
                    ? double.NaN
                    : world.SurfaceOver(x0 + i % width, tops[i], z0 + i / width, default);
        }

        return heights;
    }

    /// <summary>The heights at the four column centres round (x, z), blended.</summary>
    private static double Blend(double[] heights, int width, int x0, int z0, double x, double z)
    {
        var cx = (int)Math.Floor(x - 0.5);
        var cz = (int)Math.Floor(z - 0.5);
        double fx = x - 0.5 - cx,
            fz = z - 0.5 - cz;
        double At(int ax, int az) => heights[ax - x0 + width * (az - z0)];
        return (At(cx, cz) * (1 - fx) + At(cx + 1, cz) * fx) * (1 - fz)
            + (At(cx, cz + 1) * (1 - fx) + At(cx + 1, cz + 1) * fx) * fz;
    }

    /// <summary>The highest drawn triangle's height over (x, z), in the block's voxels.</summary>
    private static bool MeshHeight(MeshBuffers buffers, float x, float z, out float y)
    {
        y = float.NegativeInfinity;
        for (var t = 0; t < buffers.IndexCount; t += 3)
        {
            var a = MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[t]]);
            var b = MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[t + 1]]);
            var c = MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[t + 2]]);
            var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (Math.Abs(d) < 1e-9f)
            {
                continue;
            }

            var u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d;
            var v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d;
            var w = 1 - u - v;
            if (u >= -1e-5f && v >= -1e-5f && w >= -1e-5f)
            {
                y = Math.Max(y, u * a.Y + v * b.Y + w * c.Y);
            }
        }

        return float.IsFinite(y);
    }
}
