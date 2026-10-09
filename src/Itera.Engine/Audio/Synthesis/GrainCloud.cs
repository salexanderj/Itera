using System.Runtime.CompilerServices;

namespace Itera.Engine.Audio.Synthesis;

/// <summary>
/// Up to <see cref="MaxGrains"/> short grains ringing at once: each a sine at its own pitch that starts at full
/// strength and dies away within milliseconds (a Gabor grain). Many of them at random times and pitches are gravel,
/// crumbling stone, crunching leaves, or crackle (Farnell, chapters 21 and 38). A new grain past the limit takes the
/// oldest one's place. Allocates nothing.
/// </summary>
public struct GrainCloud
{
    /// <summary>The most grains that ring at once.</summary>
    public const int MaxGrains = 24;

    private Values _cos;
    private Values _sin;
    private Values _re;
    private Values _im;
    private int _next;

    /// <summary>
    /// Starts a grain at <paramref name="hertz"/>, of <paramref name="amplitude"/>, that falls by 60 dB in
    /// <paramref name="decaySeconds"/>. A grain starts at the peak of its sine, a click as a hit's is, unless
    /// <paramref name="soft"/>: then it starts at 0 and swells over a quarter of a cycle, a knock with no click (grains
    /// that start at their peak, many together, crackle).
    /// </summary>
    public void Spawn(float hertz, float amplitude, float decaySeconds, int sampleRate, bool soft = false)
    {
        var radius = Dsp.DecayPerSample(decaySeconds, sampleRate);
        var angle = 2 * MathF.PI * Math.Clamp(hertz, 20, sampleRate * 0.45f) / sampleRate;
        var i = _next;
        _next = (_next + 1) % MaxGrains;
        _cos[i] = radius * MathF.Cos(angle);
        _sin[i] = radius * MathF.Sin(angle);
        _re[i] = soft ? amplitude : 0;
        _im[i] = soft ? 0 : amplitude;
    }

    /// <summary>The next sample of every grain, summed.</summary>
    public float Process()
    {
        var sum = 0f;
        for (var i = 0; i < MaxGrains; i++)
        {
            var re = _re[i];
            var im = _im[i];
            var c = _cos[i];
            var s = _sin[i];
            _re[i] = re * c - im * s;
            _im[i] = re * s + im * c;
            sum += im;
        }

        return sum;
    }

    /// <summary>Sets the grains' states below <see cref="Dsp.Floor"/> to 0 (once a block).</summary>
    public void Flush()
    {
        for (var i = 0; i < MaxGrains; i++)
        {
            _re[i] = Dsp.Flush(_re[i]);
            _im[i] = Dsp.Flush(_im[i]);
        }
    }

    [InlineArray(MaxGrains)]
    private struct Values
    {
        private float _element;
    }
}
