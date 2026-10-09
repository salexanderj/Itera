using Itera.Engine.Maths;
using Itera.Engine.Voxels.Generation;

namespace Itera.Engine.Tests.Voxels.Generation;

/// <summary><see cref="FractalNoise"/>: its range, its continuity, its octaves, and a pinned hash of its values.</summary>
public sealed class FractalNoiseTests
{
    // Four octaves of the shape a generator's hills use: cells in centimetres, amplitudes falling by half or more.
    private static readonly int[] s_cells = [20_000, 8000, 3000, 1000];
    private static readonly double[] s_amplitudes = [1.0, 0.45, 0.2, 0.08];

    [Fact]
    public void Values_stay_within_minus_one_and_one()
    {
        var noises = new[]
        {
            new FractalNoise(s_cells, s_amplitudes, 0x68696C6C),
            new FractalNoise([700], [3.0], 5),
            new FractalNoise([100, 150, 230], [1, 1, 1], 9),
        };
        var random = new Random(1);
        foreach (var noise in noises)
        {
            double lowest = 0;
            double highest = 0;
            for (var i = 0; i < 30_000; i++)
            {
                var value = noise.Value(random.Next(-60_000, 60_000), random.Next(-60_000, 60_000), (ulong)(i % 5));
                Assert.InRange(value, -1.0, 1.0);
                lowest = Math.Min(lowest, value);
                highest = Math.Max(highest, value);
            }

            // It is not flat: the values spread over a good part of the range.
            Assert.True(lowest < -0.2 && highest > 0.2, $"range {lowest} to {highest}");
        }
    }

    [Fact]
    public void Neighbouring_points_have_neighbouring_values()
    {
        var noise = new FractalNoise(s_cells, s_amplitudes, 0x68696C6C);
        var random = new Random(2);
        var smallestCell = s_cells[^1];
        for (var i = 0; i < 20_000; i++)
        {
            var x = random.Next(-60_000, 60_000);
            var z = random.Next(-60_000, 60_000);
            var here = noise.Value(x, z, 3);
            var slope = 8.0 / smallestCell;
            Assert.InRange(Math.Abs(noise.Value(x + 1, z, 3) - here), 0, slope);
            Assert.InRange(Math.Abs(noise.Value(x, z + 1, 3) - here), 0, slope);
        }
    }

    [Fact]
    public void A_single_octave_is_the_gradient_noise_with_its_salt()
    {
        var noise = new FractalNoise([2500], [1.0], 41);
        for (var i = 0; i < 200; i++)
        {
            var (x, z) = (i * 733 - 70_000, i * 1291 - 90_000);
            Assert.Equal(DeterministicMath.GradientNoise(x, z, 2500, 6, 41), noise.Value(x, z, 6));
        }
    }

    [Fact]
    public void Octaves_are_summed_by_amplitude_with_the_salt_raised_by_the_octave()
    {
        var noise = new FractalNoise([4000, 900], [3, 1], 100);
        for (var i = 0; i < 200; i++)
        {
            var (x, z) = (i * 311 - 20_000, i * 977 - 30_000);
            var expected =
                (
                    3 * DeterministicMath.GradientNoise(x, z, 4000, 8, 100)
                    + 1 * DeterministicMath.GradientNoise(x, z, 900, 8, 101)
                ) / 4;
            Assert.Equal(expected, noise.Value(x, z, 8), 12);
        }
    }

    [Fact]
    public void The_hills_formula_of_a_generator_comes_out_bit_for_bit()
    {
        // A generator's rolling hills written out by hand: the sum divided by the total amplitude, doubled, and clipped.
        const ulong salt = 0x68696C6C;
        const double sum = 1.0 + 0.45 + 0.2 + 0.08;
        var noise = new FractalNoise(s_cells, s_amplitudes, salt);
        for (var i = 0; i < 500; i++)
        {
            var (x, z) = (i * 457 - 50_000, i * 619 - 40_000);
            double total = 0;
            for (var octave = 0; octave < s_cells.Length; octave++)
            {
                total +=
                    s_amplitudes[octave]
                    * DeterministicMath.GradientNoise(x, z, s_cells[octave], 7, salt + (ulong)octave);
            }

            var t = Math.Clamp(total / sum * 2.0, -1, 1);
            Assert.Equal(t * (1.5 - 0.5 * t * t), FractalNoise.SoftClip(noise.Value(x, z, 7) * 2.0));
        }
    }

    [Fact]
    public void The_soft_clip_is_odd_flat_at_its_ends_and_rises_everywhere()
    {
        Assert.Equal(0, FractalNoise.SoftClip(0));
        Assert.Equal(1, FractalNoise.SoftClip(1));
        Assert.Equal(-1, FractalNoise.SoftClip(-1));
        Assert.Equal(1, FractalNoise.SoftClip(7));
        Assert.Equal(-1, FractalNoise.SoftClip(-7));
        var before = -1.0;
        for (var t = -1.0; t <= 1.0; t += 0.01)
        {
            var value = FractalNoise.SoftClip(t);
            Assert.True(value >= before);
            Assert.Equal(-value, FractalNoise.SoftClip(-t), 12);
            before = value;
        }

        // Slope 0 at the ends, 1.5 in the middle.
        Assert.InRange(FractalNoise.SoftClip(1) - FractalNoise.SoftClip(0.999), 0, 1e-5);
        Assert.Equal(1.5, FractalNoise.SoftClip(0.0001) / 0.0001, 3);
    }

    [Fact]
    public void Wrong_octave_lists_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new FractalNoise([], [], 1));
        Assert.Throws<ArgumentException>(() => new FractalNoise([100, 200], [1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FractalNoise([0], [1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FractalNoise([100], [0], 1));
    }

    /// <summary>
    /// Pinned on its first run. A change to the noise's values means every generator that uses it must raise its
    /// <c>Version</c>, since the same seed would give other terrain.
    /// </summary>
    [Fact]
    public void Values_over_a_fixed_set_of_points_hash_to_the_pinned_value()
    {
        var noise = new FractalNoise(s_cells, s_amplitudes, 0x68696C6C);
        ulong hash = 0;
        for (var z = -50_000; z <= 50_000; z += 3777)
        {
            for (var x = -50_000; x <= 50_000; x += 4099)
            {
                hash = DeterministicMath.Mix(hash ^ BitConverter.DoubleToUInt64Bits(noise.Value(x, z, 1)));
                hash = DeterministicMath.Mix(hash ^ BitConverter.DoubleToUInt64Bits(noise.Value(x, z, 7)));
            }
        }

        Assert.Equal(4988316741831227368UL, hash);
    }
}
