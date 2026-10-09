namespace Itera.Engine.Audio.Synthesis;

/// <summary>
/// A small random source for sound: a 32-bit xorshift. Sounds need no determinism across machines, but a seeded source
/// lets a recipe render the same variation twice (the tests pin some) and costs nothing to copy. Never 0 inside.
/// </summary>
public struct SoundRandom
{
    private uint _state;

    public SoundRandom(uint seed)
    {
        _state = seed == 0 ? 0x9E37_79B9 : seed;
    }

    /// <summary>The next 32 random bits.</summary>
    public uint NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return x;
    }

    /// <summary>A number from 0 up to (not including) 1.</summary>
    public float Next() => (NextUInt() >> 8) * (1f / 16_777_216f);

    /// <summary>A number from -1 up to 1.</summary>
    public float NextSigned() => Next() * 2 - 1;

    /// <summary>A number from <paramref name="min"/> up to <paramref name="max"/>.</summary>
    public float Range(float min, float max) => min + (max - min) * Next();

    /// <summary>
    /// The wait before the next of events that come at random at <paramref name="ratePerSecond"/> on average (a Poisson
    /// process, as rain drops and the grains of a crunch): exponentially distributed, in seconds.
    /// </summary>
    public float Interval(float ratePerSecond) => -MathF.Log(1 - Next()) / ratePerSecond;
}
