namespace Itera.Engine.Audio.Synthesis;

/// <summary>
/// Small helpers the recipes share. Sound is not simulation: it need not match across machines, so <c>MathF</c>'s
/// functions are allowed here (unlike in world generators).
/// </summary>
public static class Dsp
{
    /// <summary>
    /// Filter and resonator states smaller than this are set to 0. A float that decays towards 0 becomes denormal below
    /// about 1e-38, and arithmetic on denormals is tens of times slower on x64; .NET has no switch to flush them, so each
    /// state is flushed by hand once a block (a long decay or a quiet filter would otherwise slow the audio thread down).
    /// </summary>
    public const float Floor = 1e-20f;

    /// <summary><paramref name="value"/>, or 0 when it is below <see cref="Floor"/> either way.</summary>
    public static float Flush(float value) => MathF.Abs(value) < Floor ? 0 : value;

    /// <summary>Gain from decibels.</summary>
    public static float DecibelsToGain(float decibels) => MathF.Pow(10, decibels / 20);

    /// <summary>
    /// A soft limit to about ±1: nearly straight for small values, bending smoothly towards ±1 (a rational fit of tanh).
    /// </summary>
    public static float SoftClip(float x)
    {
        if (x > 3)
        {
            return 1;
        }

        if (x < -3)
        {
            return -1;
        }

        var x2 = x * x;
        return x * (27 + x2) / (27 + 9 * x2);
    }

    /// <summary>The factor a value decays by each sample to fall to 1/1000 (-60 dB) in <paramref name="seconds"/>.</summary>
    public static float DecayPerSample(float seconds, int sampleRate) =>
        seconds <= 0 ? 0 : MathF.Pow(0.001f, 1f / (seconds * sampleRate));

    /// <summary>Linear interpolation.</summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// A smooth pulse over 0 to 1, 0 at both ends and 1 in the middle: half a sine wave squared. It is the force of a
    /// short contact (a hit, a heel landing): the shorter it is, the brighter the sound it excites.
    /// </summary>
    public static float ContactPulse(float t) => t is <= 0 or >= 1 ? 0 : Square(MathF.Sin(MathF.PI * t));

    /// <summary><paramref name="x"/> squared.</summary>
    public static float Square(float x) => x * x;

    /// <summary>Scales <paramref name="samples"/> so their largest magnitude is <paramref name="peak"/>; silence stays silent.</summary>
    public static void Normalise(Span<float> samples, float peak)
    {
        var most = 0f;
        foreach (var s in samples)
        {
            most = MathF.Max(most, MathF.Abs(s));
        }

        if (most <= 0)
        {
            return;
        }

        var scale = peak / most;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= scale;
        }
    }

    /// <summary>
    /// The length of <paramref name="samples"/> without its quiet end: up to the last sample above
    /// <paramref name="threshold"/>, at least 1.
    /// </summary>
    public static int TrimmedLength(ReadOnlySpan<float> samples, float threshold)
    {
        for (var i = samples.Length - 1; i > 0; i--)
        {
            if (MathF.Abs(samples[i]) > threshold)
            {
                return i + 1;
            }
        }

        return 1;
    }

    /// <summary>
    /// Fades the last <paramref name="fadeSamples"/> of <paramref name="samples"/> to 0 so a trimmed sound ends without a
    /// click.
    /// </summary>
    public static void FadeOut(Span<float> samples, int fadeSamples)
    {
        var count = Math.Min(fadeSamples, samples.Length);
        var start = samples.Length - count;
        for (var i = 0; i < count; i++)
        {
            samples[start + i] *= 1 - (i + 1) / (float)count;
        }
    }

    /// <summary>Converts to 16-bit samples, clamped to their range.</summary>
    public static void ToPcm16(ReadOnlySpan<float> source, Span<short> destination)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var s = Math.Clamp(source[i], -1f, 1f);
            destination[i] = (short)MathF.Round(s * 32767);
        }
    }
}
