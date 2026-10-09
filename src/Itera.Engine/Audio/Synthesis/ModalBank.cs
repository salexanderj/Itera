using System.Runtime.CompilerServices;

namespace Itera.Engine.Audio.Synthesis;

/// <summary>
/// A bank of up to <see cref="MaxModes"/> resonant modes: each a sine wave that rings at its own frequency and dies away
/// at its own rate when the input strikes it. A struck solid sounds as its modes ringing down (van den Doel and Pai's
/// modal synthesis; Farnell, chapters 29 to 31), so a bank tuned to a material and size is that object's sound, and the
/// force of the hit (<see cref="Dsp.ContactPulse"/>) is its input. Each mode is a rotating phasor (a complex number
/// turned and shrunk each sample), which stays stable at any frequency. Allocates nothing.
/// </summary>
public struct ModalBank
{
    /// <summary>The most modes a bank holds.</summary>
    public const int MaxModes = 24;

    private Modes _cos;
    private Modes _sin;
    private Modes _gain;
    private Modes _re;
    private Modes _im;
    private int _count;

    /// <summary>Modes set.</summary>
    public readonly int Count => _count;

    /// <summary>Removes every mode and silences the bank.</summary>
    public void Clear() => _count = 0;

    /// <summary>
    /// Adds a mode at <paramref name="hertz"/> that falls by 60 dB in <paramref name="decaySeconds"/>, with
    /// <paramref name="gain"/>. Modes above 0.45 of the sample rate (which would fold back as wrong pitches) and past
    /// <see cref="MaxModes"/> are left out.
    /// </summary>
    public void Add(float hertz, float decaySeconds, float gain, int sampleRate)
    {
        if (_count == MaxModes || hertz <= 0 || hertz >= sampleRate * 0.45f || decaySeconds <= 0)
        {
            return;
        }

        var radius = Dsp.DecayPerSample(decaySeconds, sampleRate);
        var angle = 2 * MathF.PI * hertz / sampleRate;
        _cos[_count] = radius * MathF.Cos(angle);
        _sin[_count] = radius * MathF.Sin(angle);
        _gain[_count] = gain;
        _re[_count] = 0;
        _im[_count] = 0;
        _count++;
    }

    /// <summary>Strikes the bank with one sample of force and returns one sample of its ringing.</summary>
    public float Process(float input)
    {
        var sum = 0f;
        for (var i = 0; i < _count; i++)
        {
            var re = _re[i];
            var im = _im[i];
            var c = _cos[i];
            var s = _sin[i];
            var nextRe = re * c - im * s + input * _gain[i];
            var nextIm = re * s + im * c;
            _re[i] = nextRe;
            _im[i] = nextIm;
            sum += nextIm;
        }

        return sum;
    }

    /// <summary>Sets the modes' states below <see cref="Dsp.Floor"/> to 0 (once a block).</summary>
    public void Flush()
    {
        for (var i = 0; i < _count; i++)
        {
            _re[i] = Dsp.Flush(_re[i]);
            _im[i] = Dsp.Flush(_im[i]);
        }
    }

    [InlineArray(MaxModes)]
    private struct Modes
    {
        private float _element;
    }
}
