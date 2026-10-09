namespace Itera.Engine.Audio;

/// <summary>
/// How a sound is made. Recipes are code: each models a sound from its physics (what gives the energy, what rings, what
/// shapes it on the way out; Farnell, <i>Designing Sound</i>), driven by one number, its level. The game registers them
/// by name in <see cref="SoundRecipes"/>. Two kinds: <see cref="OneShotRecipe"/> and <see cref="StreamRecipe"/>.
/// </summary>
public abstract class SoundRecipe
{
    /// <summary>
    /// The distance, in metres, within which the sound plays at its full gain; past it the gain falls as the reciprocal
    /// of the distance (OpenAL's clamped inverse distance). Larger for loud sources.
    /// </summary>
    public float ReferenceMetres { get; init; } = 2;

    /// <summary>
    /// The gain of the sound at <see cref="ReferenceMetres"/> and nearer, before the event's own gain: the recipe's place
    /// in the mix.
    /// </summary>
    public float Gain { get; init; } = 1;
}

/// <summary>
/// A short sound (a hit, a step, a dig) rendered ahead of time: at start the audio thread renders
/// <see cref="Levels"/> times <see cref="VariationsPerLevel"/> variations, each at its level (0 to 1, spread evenly) and
/// its own seed, into OpenAL buffers; an event picks the level nearest its own and one of that level's variations, not
/// the one it played last (Farnell's hybrid of synthesis and samples, section 22.5: the variety of synthesis at the cost
/// of playing a recording). <see cref="Render"/> must use nothing but its arguments: it runs on the audio thread, and on
/// the main thread for <c>sound wav</c>.
/// </summary>
public abstract class OneShotRecipe : SoundRecipe
{
    /// <summary>The longest the sound can last, in seconds: the length of the buffer <see cref="Render"/> fills.</summary>
    public abstract float Seconds { get; }

    /// <summary>How many levels are rendered, spread evenly from 0 to 1.</summary>
    public int Levels { get; init; } = 4;

    /// <summary>How many variations of each level are rendered.</summary>
    public int VariationsPerLevel { get; init; } = 4;

    /// <summary>
    /// Writes one variation into <paramref name="output"/> (<see cref="Seconds"/> long, zeroed): the sound at
    /// <paramref name="level"/> (0 to 1: how hard, how big) and <paramref name="seed"/>. Keep samples within ±1; the
    /// end is trimmed where it falls silent.
    /// </summary>
    public abstract void Render(Span<float> output, int sampleRate, float level, uint seed);

    /// <summary>The number of samples <see cref="Render"/> fills at <paramref name="sampleRate"/>.</summary>
    public int SampleCount(int sampleRate) => Math.Max(1, (int)MathF.Ceiling(Seconds * sampleRate));

    /// <summary>The level (0 to 1) rendered for level index <paramref name="level"/>.</summary>
    public float LevelValue(int level) => Levels == 1 ? 1 : level / (float)(Levels - 1);

    /// <summary>
    /// Renders variation <paramref name="variation"/> of level index <paramref name="level"/> into
    /// <paramref name="scratch"/> (at least <see cref="SampleCount"/> long) with its fixed seed, trims its silent end with
    /// a short fade, and returns the samples kept.
    /// </summary>
    public int RenderVariation(Span<float> scratch, int sampleRate, int level, int variation)
    {
        var samples = scratch[..SampleCount(sampleRate)];
        samples.Clear();
        var seed = (uint)(level + 1) * 0x9E37_79B1u ^ (uint)(variation + 1) * 0x85EB_CA77u;
        Render(samples, sampleRate, LevelValue(level), seed);
        var length = Synthesis.Dsp.TrimmedLength(samples, TrimThreshold);
        Synthesis.Dsp.FadeOut(samples[..length], sampleRate / 200);
        return length;
    }

    /// <summary>Samples below this (about two steps of 16-bit sound) count as silence at the end.</summary>
    private const float TrimThreshold = 6e-5f;
}

/// <summary>
/// A sound that lasts while the game keeps it going (a beam, wind, fire), made live on the audio thread from
/// <see cref="SoundGenerator"/>s, block by block, following the level the game gives it each frame.
/// </summary>
public abstract class StreamRecipe : SoundRecipe
{
    /// <summary>A new generator for one playing sound. Called on the audio thread (or the main thread for <c>sound wav</c>).</summary>
    public abstract SoundGenerator Create(int sampleRate);
}

/// <summary>
/// One playing stream's state: its filters, phases, and timers. The audio thread keeps them and reuses them
/// (<see cref="Reset"/>), so <see cref="Render"/> must not allocate.
/// </summary>
public abstract class SoundGenerator
{
    /// <summary>Starts again as a new sound with <paramref name="seed"/>.</summary>
    public abstract void Reset(uint seed);

    /// <summary>
    /// Writes the next block into <paramref name="output"/>, with the level going from its value in the last block to
    /// <paramref name="level"/> (0 to 1) across this one, so changes do not click. Keep samples within ±1.
    /// </summary>
    public abstract void Render(Span<float> output, float level);
}
