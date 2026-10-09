using Itera.Engine.Maths;

namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// Octaves of <see cref="DeterministicMath.GradientNoise(int, int, int, ulong, ulong)"/> summed: each octave has its own
/// cell size (in centimetres) and amplitude, and its own salt, the salt given plus its index. The sum is divided by the
/// total amplitude, so <see cref="Value"/> stays within -1 to 1 whatever the amplitudes. Large cells make broad shapes
/// and small cells detail. Immutable and thread safe. A change to the cells, the amplitudes, or the salt changes every
/// value, so a generator that uses it must raise its version.
/// </summary>
public sealed class FractalNoise
{
    private readonly int[] _cells;
    private readonly double[] _amplitudes;
    private readonly double _amplitudeSum;

    /// <summary>Makes the noise from one cell size and one amplitude for each octave, and the first octave's salt.</summary>
    public FractalNoise(ReadOnlySpan<int> cells, ReadOnlySpan<double> amplitudes, ulong salt)
    {
        if (cells.Length == 0 || cells.Length != amplitudes.Length)
        {
            throw new ArgumentException(
                "Give one cell size and one amplitude for each octave, and at least one octave."
            );
        }

        _cells = cells.ToArray();
        _amplitudes = amplitudes.ToArray();
        Salt = salt;
        foreach (var cell in _cells)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cell);
        }

        foreach (var amplitude in _amplitudes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amplitude);
            _amplitudeSum += amplitude;
        }
    }

    /// <summary>The salt of the first octave; octave <c>i</c> uses <c>Salt + i</c>.</summary>
    public ulong Salt { get; }

    /// <summary>How many octaves are summed.</summary>
    public int Octaves => _cells.Length;

    /// <summary>
    /// The noise at (<paramref name="x"/>, <paramref name="z"/>) in centimetres: the octaves' sum over the total
    /// amplitude, from -1 to 1. Most values lie well inside that range, since the octaves seldom peak together.
    /// </summary>
    public double Value(int x, int z, ulong seed)
    {
        double sum = 0;
        for (var octave = 0; octave < _cells.Length; octave++)
        {
            sum +=
                _amplitudes[octave] * DeterministicMath.GradientNoise(x, z, _cells[octave], seed, Salt + (ulong)octave);
        }

        return sum / _amplitudeSum;
    }

    /// <summary>
    /// A cubic soft clip: <paramref name="value"/> clamped to -1 to 1, then <c>t (1.5 - 0.5 t²)</c>. It is 0 at 0, flat
    /// at both ends (the slope is 0 at -1 and 1), and rises steepest in the middle, so a stretched <see cref="Value"/>
    /// reaches its limits without a corner. Maps to a height with <c>middle + half * SoftClip(Value * contrast)</c>.
    /// </summary>
    public static double SoftClip(double value)
    {
        var t = Math.Clamp(value, -1, 1);
        return t * (1.5 - 0.5 * t * t);
    }
}
