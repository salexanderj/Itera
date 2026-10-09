using Itera.Engine.Maths;

namespace Itera.Engine.Tests.Maths;

public sealed class DeterministicMathTests
{
    private const ulong Seed = 1;
    private const ulong Salt = 0x74657374;

    [Fact]
    public void Gradient_noise_stays_within_one_and_uses_most_of_the_range()
    {
        var random = new Random(5);
        double min = 0;
        double max = 0;
        for (var i = 0; i < 200_000; i++)
        {
            var value = DeterministicMath.GradientNoise(
                random.Next(-50_000, 50_000),
                random.Next(-50_000, 50_000),
                1000,
                Seed,
                Salt
            );
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        Assert.InRange(min, -1.0, -0.6);
        Assert.InRange(max, 0.6, 1.0);
    }

    [Fact]
    public void Gradient_noise_is_zero_at_lattice_points_and_continuous_between()
    {
        Assert.Equal(0.0, DeterministicMath.GradientNoise(3000, -7000, 1000, Seed, Salt));
        Assert.Equal(0.0, DeterministicMath.GradientNoise(-1000, 0, 1000, Seed, Salt));
        for (var x = -2000; x < 2000; x++)
        {
            var step =
                DeterministicMath.GradientNoise(x + 1, 250, 1000, Seed, Salt)
                - DeterministicMath.GradientNoise(x, 250, 1000, Seed, Salt);
            Assert.True(Math.Abs(step) < 0.01, $"Step of {step} at x = {x}.");
        }
    }

    [Fact]
    public void One_dimensional_noise_stays_within_one()
    {
        double min = 0;
        double max = 0;
        for (var x = -100_000; x < 100_000; x += 7)
        {
            var value = DeterministicMath.GradientNoise(x, 800, Seed, Salt);
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        Assert.InRange(min, -1.0, -0.6);
        Assert.InRange(max, 0.6, 1.0);
    }

    [Fact]
    public void Noise_gives_the_same_bits_on_every_run()
    {
        var hash = 14695981039346656037UL;
        for (var i = -500; i < 500; i++)
        {
            var value2D = DeterministicMath.GradientNoise(i * 37, i * -53, 3000, Seed, Salt);
            var value1D = DeterministicMath.GradientNoise(i * 41, 800, Seed, Salt);
            hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(value2D)) * 1099511628211UL;
            hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(value1D)) * 1099511628211UL;
        }

        // Pinned. A change means the noise changed; every generator that uses it must raise its Version.
        Assert.Equal(0xE6E7AF1C7ED7D6EDUL, hash);
    }

    [Fact]
    public void Turns_and_cos_sin_match_the_platform_within_a_ten_billionth()
    {
        var random = new Random(5);
        var hash = 14695981039346656037UL;
        for (var i = 0; i < 100_000; i++)
        {
            var x = random.NextDouble() * 2 - 1;
            var z = random.NextDouble() * 2 - 1;
            var expected = Math.Atan2(z, x) / (2 * Math.PI);
            expected += expected < 0 ? 1 : 0;
            var turns = DeterministicMath.Turns(x, z);
            var error = Math.Abs(turns - expected);
            Assert.True(Math.Min(error, 1 - error) < 1e-10, $"Turns of ({x}, {z}): {turns}, not {expected}.");
            Assert.True(turns >= 0 && turns < 1, $"Turns of ({x}, {z}): {turns}.");

            var (cos, sin) = DeterministicMath.CosSin(turns);
            Assert.True(Math.Abs(cos - Math.Cos(turns * 2 * Math.PI)) < 1e-12, $"Cos of {turns}: {cos}.");
            Assert.True(Math.Abs(sin - Math.Sin(turns * 2 * Math.PI)) < 1e-12, $"Sin of {turns}: {sin}.");
            hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(turns)) * 1099511628211UL;
            hash = (hash ^ (ulong)BitConverter.DoubleToInt64Bits(cos)) * 1099511628211UL;
        }

        foreach (
            var (x, z, expected) in new[] { (1.0, 0.0, 0.0), (0.0, 1.0, 0.25), (-1.0, 0.0, 0.5), (0.0, -1.0, 0.75) }
        )
        {
            Assert.Equal(expected, DeterministicMath.Turns(x, z), 12);
        }

        Assert.Equal(0, DeterministicMath.Turns(0, 0));
        Assert.Equal(0, DeterministicMath.Turns(1, -0.0));
        Assert.Equal((1.0, 0.0), DeterministicMath.CosSin(-1));

        // Pinned. A change moves every shape built from these angles; generators that use them must raise their Version.
        Assert.Equal(0xFEAE2628EB2C8834UL, hash);
    }

    [Fact]
    public void Square_root_is_exact()
    {
        var random = new Random(9);
        for (var i = 0; i < 100_000; i++)
        {
            var value = random.NextInt64(0, 1L << 50);
            long root = DeterministicMath.SquareRoot(value);
            Assert.True(root * root <= value && (root + 1) * (root + 1) > value, $"Root {root} of {value}.");
        }

        Assert.Equal(0, DeterministicMath.SquareRoot(-5));
    }

    [Theory]
    [InlineData(7, 2, 3)]
    [InlineData(-7, 2, -4)]
    [InlineData(-8, 2, -4)]
    [InlineData(7, -2, -4)]
    [InlineData(0, 5, 0)]
    public void Floor_divide_rounds_down(int value, int divisor, int expected)
    {
        Assert.Equal(expected, DeterministicMath.FloorDivide(value, divisor));
        Assert.Equal(expected, DeterministicMath.FloorDivide((long)value, divisor));
    }

    /// <summary>
    /// Each draw takes one step of the sequence, and the same state gives the same values.
    /// </summary>
    [Fact]
    public void Draws_take_one_step_each_and_give_the_same_values()
    {
        ulong state = 0x1234_5678_9ABC_DEF0;
        var copy = state;
        var unit = DeterministicMath.NextUnit(ref state);
        Assert.Equal(DeterministicMath.Unit(DeterministicMath.Next(ref copy)), unit);
        Assert.Equal(copy, state);
        Assert.InRange(unit, 0, 1 - double.Epsilon);
        var between = DeterministicMath.NextBetween(ref state, -2.5, 4);
        Assert.Equal(-2.5 + 6.5 * DeterministicMath.Unit(DeterministicMath.Next(ref copy)), between);
        var range = DeterministicMath.NextRange(ref state, -3, 9);
        Assert.Equal(DeterministicMath.Range(DeterministicMath.Next(ref copy), -3, 9), range);
        Assert.InRange(range, -3, 9);
        Assert.Equal(copy, state);
        // Pinned: a change to the draws moves every generated thing.
        Assert.Equal(0x3FB61922C645CE50UL, BitConverter.DoubleToUInt64Bits(unit));
    }

    [Theory]
    [InlineData(1000, 150, 0, 999, -1, 1)]
    [InlineData(1000, 150, -2000, -1001, -3, -1)]
    [InlineData(1000, 0, 0, 0, 0, 0)]
    [InlineData(400, 50, 351, 449, 0, 1)]
    public void A_cell_range_covers_every_cell_whose_things_reach_the_span(
        int cell,
        int reach,
        int min,
        int max,
        int first,
        int last
    )
    {
        DeterministicMath.CellRange(cell, reach, min, max, out var actualFirst, out var actualLast);
        Assert.Equal((first, last), (actualFirst, actualLast));
    }
}
