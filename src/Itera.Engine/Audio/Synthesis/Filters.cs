namespace Itera.Engine.Audio.Synthesis;

/// <summary>
/// A one-pole low-pass filter (a leaky average): the cheapest way to dull a sound or smooth a control. Its high-pass
/// output is the input less the low-pass.
/// </summary>
public struct OnePole
{
    private float _coefficient;
    private float _state;

    /// <summary>Sets the cut-off, in hertz.</summary>
    public void SetCutoff(float hertz, int sampleRate)
    {
        _coefficient = 1 - MathF.Exp(-2 * MathF.PI * Math.Clamp(hertz, 1, sampleRate * 0.49f) / sampleRate);
    }

    /// <summary>Filters one sample and returns the low-pass output.</summary>
    public float LowPass(float input)
    {
        _state += _coefficient * (input - _state);
        return _state;
    }

    /// <summary>Filters one sample and returns the high-pass output.</summary>
    public float HighPass(float input) => input - LowPass(input);

    /// <summary>Sets the state below <see cref="Dsp.Floor"/> to 0 (once a block).</summary>
    public void Flush() => _state = Dsp.Flush(_state);

    /// <summary>Forgets the past.</summary>
    public void Reset() => _state = 0;
}

/// <summary>
/// A state variable filter (Andrew Simper's trapezoidal form): low-pass, band-pass, and high-pass at once, stable while
/// its cut-off and resonance change every sample. The band-pass with a high Q is a resonance: a body, a cavity, a
/// formant.
/// </summary>
public struct StateVariableFilter
{
    private float _a1;
    private float _a2;
    private float _a3;
    private float _k;
    private float _ic1;
    private float _ic2;

    /// <summary>The last sample's low-pass output.</summary>
    public float Low { get; private set; }

    /// <summary>The last sample's band-pass output (unity gain at the centre when normalised by <c>k</c>).</summary>
    public float Band { get; private set; }

    /// <summary>The last sample's high-pass output.</summary>
    public float High { get; private set; }

    /// <summary>Sets the centre or cut-off in hertz and the resonance Q (0.5 is soft, 10 rings).</summary>
    public void Set(float hertz, float q, int sampleRate)
    {
        var g = MathF.Tan(MathF.PI * Math.Clamp(hertz, 10, sampleRate * 0.45f) / sampleRate);
        _k = 1 / MathF.Max(q, 0.05f);
        _a1 = 1 / (1 + g * (g + _k));
        _a2 = g * _a1;
        _a3 = g * _a2;
    }

    /// <summary>Filters one sample; read <see cref="Low"/>, <see cref="Band"/>, or <see cref="High"/>.</summary>
    public void Process(float input)
    {
        var v3 = input - _ic2;
        var v1 = _a1 * _ic1 + _a2 * v3;
        var v2 = _ic2 + _a2 * _ic1 + _a3 * v3;
        _ic1 = 2 * v1 - _ic1;
        _ic2 = 2 * v2 - _ic2;
        Low = v2;
        Band = v1;
        High = input - _k * v1 - v2;
    }

    /// <summary>Filters one sample and returns the band-pass output, scaled to unity gain at the centre.</summary>
    public float BandPass(float input)
    {
        Process(input);
        return Band * _k;
    }

    /// <summary>Filters one sample and returns the low-pass output.</summary>
    public float LowPass(float input)
    {
        Process(input);
        return Low;
    }

    /// <summary>Filters one sample and returns the high-pass output.</summary>
    public float HighPass(float input)
    {
        Process(input);
        return High;
    }

    /// <summary>Sets the states below <see cref="Dsp.Floor"/> to 0 (once a block).</summary>
    public void Flush()
    {
        _ic1 = Dsp.Flush(_ic1);
        _ic2 = Dsp.Flush(_ic2);
    }

    /// <summary>Forgets the past.</summary>
    public void Reset()
    {
        _ic1 = 0;
        _ic2 = 0;
    }
}

/// <summary>
/// Noise of three colours from one <see cref="SoundRandom"/>: white (every frequency alike: hiss), pink (equal energy per
/// octave: rain, wind's body; Paul Kellet's economy filter), and brown (falling 6 dB an octave: rumble, a leaky sum of
/// white).
/// </summary>
public struct NoiseSource
{
    private SoundRandom _random;
    private float _p0;
    private float _p1;
    private float _p2;
    private float _brown;

    public NoiseSource(uint seed)
    {
        _random = new SoundRandom(seed);
    }

    /// <summary>White noise from -1 to 1.</summary>
    public float White() => _random.NextSigned();

    /// <summary>Pink noise, about -1 to 1.</summary>
    public float Pink()
    {
        var white = _random.NextSigned();
        _p0 = 0.99765f * _p0 + white * 0.0990460f;
        _p1 = 0.96300f * _p1 + white * 0.2965164f;
        _p2 = 0.57000f * _p2 + white * 1.0526913f;
        return (_p0 + _p1 + _p2 + white * 0.1848f) * 0.15f;
    }

    /// <summary>Brown noise, about -1 to 1.</summary>
    public float Brown()
    {
        _brown = 0.995f * _brown + _random.NextSigned() * 0.1f;
        return _brown * 0.5f;
    }

    /// <summary>A random number from 0 up to 1, from the same source as the noise.</summary>
    public float Next() => _random.Next();

    /// <summary>A random wait for events at <paramref name="ratePerSecond"/> (<see cref="SoundRandom.Interval"/>).</summary>
    public float Interval(float ratePerSecond) => _random.Interval(ratePerSecond);

    /// <summary>Sets the states below <see cref="Dsp.Floor"/> to 0 (once a block).</summary>
    public void Flush()
    {
        _p0 = Dsp.Flush(_p0);
        _p1 = Dsp.Flush(_p1);
        _p2 = Dsp.Flush(_p2);
        _brown = Dsp.Flush(_brown);
    }
}
