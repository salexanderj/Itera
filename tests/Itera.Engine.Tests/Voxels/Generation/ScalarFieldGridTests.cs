using Itera.Engine.Maths;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary><see cref="ScalarFieldGrid"/>: a field cached on a grid fixed to the world, blended between its points.</summary>
public sealed class ScalarFieldGridTests
{
    // A smooth field with no relation to the grid's cell.
    private static double Field(int x, int z) =>
        0.5 + 0.3 * DeterministicMath.GradientNoise(x, z, 1500, 3, 9) + 0.0001 * x - 0.00007 * z;

    [Fact]
    public void At_a_grid_point_the_value_is_the_field()
    {
        var grid = new ScalarFieldGrid(400, Field);
        grid.Fill(-1000, 1000, -1000, 1000);
        for (var z = -1200; z <= 1200; z += 400)
        {
            for (var x = -1200; x <= 1200; x += 400)
            {
                Assert.Equal(Field(x, z), grid.At(x, z));
            }
        }
    }

    [Fact]
    public void Between_grid_points_the_value_is_blended_bilinearly()
    {
        var grid = new ScalarFieldGrid(400, Field);
        grid.Fill(-2000, 2000, -2000, 2000);
        var random = new Random(8);
        for (var i = 0; i < 2000; i++)
        {
            var x = random.Next(-2000, 2000);
            var z = random.Next(-2000, 2000);
            var cellX = DeterministicMath.FloorDivide(x, 400);
            var cellZ = DeterministicMath.FloorDivide(z, 400);
            var fx = (x - cellX * 400) / 400.0;
            var fz = (z - cellZ * 400) / 400.0;
            var near = Field(cellX * 400, cellZ * 400) * (1 - fx) + Field((cellX + 1) * 400, cellZ * 400) * fx;
            var far =
                Field(cellX * 400, (cellZ + 1) * 400) * (1 - fx) + Field((cellX + 1) * 400, (cellZ + 1) * 400) * fx;
            Assert.Equal(near * (1 - fz) + far * fz, grid.At(x, z), 9);
        }
    }

    [Fact]
    public void A_linear_field_is_read_exactly_everywhere()
    {
        var grid = new ScalarFieldGrid(250, (x, z) => 2.0 + 0.003 * x - 0.002 * z);
        grid.Fill(-777, 1234, -4321, -100);
        for (var z = -4321; z <= -100; z += 97)
        {
            for (var x = -777; x <= 1234; x += 89)
            {
                Assert.Equal(2.0 + 0.003 * x - 0.002 * z, grid.At(x, z), 9);
            }
        }
    }

    [Fact]
    public void A_point_gets_the_same_value_whatever_box_holds_it()
    {
        var random = new Random(9);
        var points = new List<(int X, int Z)>();
        for (var i = 0; i < 50; i++)
        {
            points.Add((random.Next(-300, 300), random.Next(-300, 300)));
        }

        var reference = new ScalarFieldGrid(400, Field);
        reference.Fill(-300, 300, -300, 300);
        var grid = new ScalarFieldGrid(400, Field);

        // A tight box about each point, then wide boxes of other shapes, in turn so the array is reused and regrown.
        foreach (var (x, z) in points)
        {
            grid.Fill(x, x, z, z);
            Assert.Equal(reference.At(x, z), grid.At(x, z));
            grid.Fill(x - 5000, x + 7000, z - 100, z + 100);
            Assert.Equal(reference.At(x, z), grid.At(x, z));
            grid.Fill(x - 399, x + 401, z - 6000, z + 6000);
            Assert.Equal(reference.At(x, z), grid.At(x, z));
        }
    }

    [Fact]
    public void Filling_asks_the_field_once_for_each_grid_point_the_box_needs()
    {
        var calls = 0;
        var grid = new ScalarFieldGrid(
            100,
            (x, z) =>
            {
                calls++;
                return x + z;
            }
        );

        // Cells -2 to 2 along x and 0 to 1 along z, and one point more each way for the blend.
        grid.Fill(-150, 250, 0, 120);
        Assert.Equal(6 * 3, calls);
        Assert.Equal(0.0, grid.At(0, 0));
        Assert.Equal(-30.0, grid.At(-150, 120), 9);
    }

    [Fact]
    public void Wrong_arguments_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScalarFieldGrid(0, Field));
        Assert.Throws<ArgumentNullException>(() => new ScalarFieldGrid(400, null!));
    }
}
