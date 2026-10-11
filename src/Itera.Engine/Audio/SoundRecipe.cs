namespace Itera.Engine.Audio;

/// <summary>
/// How a sound is made. Recipes are code: each models a sound from its physics (what gives the energy, what rings, what
/// shapes it on the way out; Farnell, <i>Designing Sound</i>), driven by one number, its level. The game registers them
/// by name in <see cref="SoundRecipes"/>. Two kinds: <see cref="OneShotRecipe"/> and <see cref="StreamRecipe"/>; a sound
/// read from files (<see cref="FileRecipe"/>) is registered and played the same way.
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

/// <summary>
/// A sound read from files (WAV or Ogg Vorbis: <see cref="Files.SoundDecoders"/>) rather than made in code, played by
/// name as a recipe is. Its files are decoded once at start, on the render thread, into OpenAL buffers, each at its own
/// rate. A file that cannot be read is logged at Error and plays nothing; the rest play. A stereo file is mixed to mono
/// and played in the world, unless <see cref="KeepStereo"/>, which keeps its channels and plays it at the listener.
/// </summary>
public abstract class FileRecipe : SoundRecipe
{
    /// <param name="paths">The files; a relative path is from the game's folder (where it runs from).</param>
    protected FileRecipe(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Paths = [.. paths.Select(Resolve)];
    }

    /// <summary>The files, as full paths.</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>
    /// Keeps a stereo file's two channels and plays the sound at the listener (music-like stings, an interface sound, an
    /// ambience); by default a stereo file is mixed to mono, (left + right) / 2, and played where the event says.
    /// </summary>
    public bool KeepStereo { get; init; }

    /// <summary>The longest a file may last, in seconds: it is held whole in memory.</summary>
    public abstract float MostSeconds { get; }

    /// <summary>A full path for <paramref name="path"/>: as it is when rooted, else from the game's folder.</summary>
    public static string Resolve(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }
}

/// <summary>
/// A short sound from files: each file a variation of it (one level), an event playing one at random but never the one it
/// played last, as a one-shot recipe's variations play. A file of at most <see cref="MaxSeconds"/>.
/// </summary>
public sealed class FileOneShotRecipe : FileRecipe
{
    /// <summary>The longest a one-shot's file may last, in seconds.</summary>
    public const float MaxSeconds = 30;

    /// <param name="paths">One file or more (at most 16): its variations.</param>
    public FileOneShotRecipe(params string[] paths)
        : base(paths) { }

    public override float MostSeconds => MaxSeconds;
}

/// <summary>
/// A sound from one file that plays while the game sustains it (<c>AudioService.Sustain</c>, by recipe and key, as a
/// stream recipe's): a waterfall, a machine's hum, an ambience at the listener. It loops the file's loop (a WAV's
/// <c>smpl</c> chunk, an Ogg file's LOOPSTART tags) after playing the part before it once, or the whole file; one with no
/// loop starts at a random place, so two copies are not in step, and fades in. It fades out over 80 ms once no longer
/// sustained. A file of at most <see cref="MaxSeconds"/>.
/// </summary>
public sealed class FileLoopRecipe : FileRecipe
{
    /// <summary>The longest a loop's file may last, in seconds (a stereo minute at 48 kHz holds 11 MB).</summary>
    public const float MaxSeconds = 120;

    /// <param name="path">The file.</param>
    public FileLoopRecipe(string path)
        : base([path]) { }

    public override float MostSeconds => MaxSeconds;
}
