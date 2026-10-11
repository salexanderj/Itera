using System.Collections.Concurrent;
using System.Diagnostics;
using Itera.Engine.Audio.Files;
using Itera.Engine.Audio.Synthesis;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Audio;

/// <summary>
/// A one-shot recipe's variations (or a sound's files) as samples, ready for buffers; none when its recipe failed or none
/// of its files could be read.
/// </summary>
/// <param name="Id">The recipe.</param>
/// <param name="Lengths">Each variation's length in samples (every channel's), level by level.</param>
/// <param name="Samples">Every variation's samples, back to back.</param>
/// <param name="Cached">True when read from the sound cache.</param>
/// <param name="Rates">Each variation's rate, for a sound from files; null for a recipe's (the mixer's rate).</param>
/// <param name="Channels">Each variation's channels, for a sound from files; null for a recipe's (mono).</param>
internal sealed record RenderedRecipe(
    int Id,
    int[] Lengths,
    short[] Samples,
    bool Cached,
    int[]? Rates = null,
    int[]? Channels = null
)
{
    /// <summary>True when the recipe threw, or none of its files could be read: it stays silent.</summary>
    public bool Failed => Lengths.Length == 0;
}

/// <summary>One file of a sound read from files, as the render thread found it (for the console's <c>sound files</c>).</summary>
/// <param name="Path">The file.</param>
/// <param name="Channels">Its channels as held (1, or 2 when kept stereo); 0 when it could not be read.</param>
/// <param name="SampleRate">Its rate.</param>
/// <param name="Seconds">Its length.</param>
/// <param name="Bytes">The memory its samples take.</param>
/// <param name="LoopPoints">The loop it names, or none.</param>
/// <param name="Problem">Why it could not be read, or what was wrong but did not stop it; null for nothing.</param>
public sealed record SoundFileInfo(
    string Path,
    int Channels,
    int SampleRate,
    double Seconds,
    long Bytes,
    LoopPoints LoopPoints,
    string? Problem
)
{
    /// <summary>True when the file could not be read.</summary>
    public bool Failed => Channels == 0;
}

/// <summary>
/// Renders the one-shot recipes' variations off the audio thread, and reads the sounds from files, in the order they were
/// registered: a recipe's read from the sound cache when it holds them, or rendered and written there (rendered
/// on the audio thread, every sound waiting would be dropped while they rendered, the menu's clicks too). The audio thread
/// takes each finished recipe (<see cref="TryTake"/>) and puts it into buffers. A recipe that throws is logged and left
/// silent, and the rest go on; a file that cannot be read is logged at Error, and another of its sound's files plays in
/// its place (none: the sound is silent). <see cref="RenderNext"/> runs on one thread at a
/// time: the render thread (<see cref="Run"/>), or the audio thread itself when the mixer renders as it pumps (tests).
/// </summary>
internal sealed partial class RecipeRenderer
{
    /// <summary>How long the render thread waits before it looks for new recipes, in milliseconds.</summary>
    private const int IdleMilliseconds = 20;

    private readonly SoundRecipes _recipes;
    private readonly int _sampleRate;
    private readonly SoundCache? _cache;
    private readonly ILogger _logger;
    private readonly ConcurrentQueue<RenderedRecipe> _done = new();
    private readonly ConcurrentDictionary<int, SoundFileInfo[]> _files = new();
    private int _next;
    private long _start;
    private int _rendered;
    private int _variations;
    private long _bytes;
    private int _cached;
    private volatile bool _stopping;

    public RecipeRenderer(SoundRecipes recipes, int sampleRate, SoundCache? cache, ILogger logger)
    {
        _recipes = recipes;
        _sampleRate = sampleRate;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Renders the next one-shot recipe registered and not yet rendered, passing over streams. Returns false when none
    /// waits. Logs the totals each time it catches up with the recipes registered.
    /// </summary>
    public bool RenderNext()
    {
        var count = _recipes.Count;
        while (_next < count)
        {
            var id = _next++;
            if (!SoundRecipes.IsPrepared(_recipes[id]))
            {
                continue;
            }

            if (_rendered == 0)
            {
                _start = Stopwatch.GetTimestamp();
            }

            var rendered = _recipes[id] is FileRecipe files ? Read(id, files) : Render(id, (OneShotRecipe)_recipes[id]);
            _done.Enqueue(rendered);
            _rendered++;
            _variations += rendered.Lengths.Length;
            _bytes += rendered.Samples.Length * sizeof(short);
            if (_next == count && _recipes.Count == count)
            {
                var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
                LogRendered(_rendered, _variations, _bytes / 1024, _cached, ms);
                _rendered = 0;
                _variations = 0;
                _bytes = 0;
                _cached = 0;
                _cache?.Prune(_recipes);
            }

            return true;
        }

        return false;
    }

    /// <summary>A finished recipe, oldest first. Any thread.</summary>
    public bool TryTake(out RenderedRecipe rendered) => _done.TryDequeue(out rendered!);

    /// <summary>The files of the sound <paramref name="id"/> as they were read, or null before they are. Any thread.</summary>
    public IReadOnlyList<SoundFileInfo>? FilesOf(int id) => _files.TryGetValue(id, out var files) ? files : null;

    /// <summary>The render thread: renders each recipe as it appears until <see cref="Stop"/>.</summary>
    public void Run()
    {
        while (!_stopping)
        {
            if (!RenderNext())
            {
                Thread.Sleep(IdleMilliseconds);
            }
        }
    }

    /// <summary>Asks <see cref="Run"/> to end after the recipe it renders now.</summary>
    public void Stop() => _stopping = true;

    private RenderedRecipe Render(int id, OneShotRecipe recipe)
    {
        var name = _recipes.NameOf(id);
        try
        {
            var path = _cache?.PathFor(name, recipe, _sampleRate);
            if (path is not null && _cache!.TryRead(path, recipe, _sampleRate, out var lengths, out var pcm))
            {
                _cached++;
                return new RenderedRecipe(id, lengths, pcm, Cached: true);
            }

            (lengths, pcm) = Render(recipe, _sampleRate);
            if (path is not null)
            {
                _cache!.Write(path, name, recipe, _sampleRate, lengths, pcm);
            }

            return new RenderedRecipe(id, lengths, pcm, Cached: false);
        }
        catch (Exception exception)
        {
            LogFailed(exception, name);
            return new RenderedRecipe(id, [], [], Cached: false);
        }
    }

    /// <summary>
    /// Reads a sound's files whole: each a variation, at its own rate and channels; one that cannot be read is logged and
    /// its place taken by the next good one (in turn), and with none good the sound fails, silent.
    /// </summary>
    private RenderedRecipe Read(int id, FileRecipe recipe)
    {
        var name = _recipes.NameOf(id);
        var paths = recipe.Paths;
        var sounds = new DecodedSound?[paths.Count];
        var infos = new SoundFileInfo[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            try
            {
                var sound = SoundFiles.Decode(paths[i], recipe.KeepStereo, recipe.MostSeconds);
                sounds[i] = sound;
                infos[i] = new SoundFileInfo(
                    paths[i],
                    sound.Channels,
                    sound.SampleRate,
                    sound.Seconds,
                    sound.Bytes,
                    sound.LoopPoints,
                    sound.Warning
                );
                if (sound.Warning is { } warning)
                {
                    LogFileWarning(name, paths[i], warning);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var reason = exception is SoundFileException ? exception.Message : exception.ToString();
                infos[i] = new SoundFileInfo(paths[i], 0, 0, 0, 0, LoopPoints.None, reason);
                LogFileFailed(name, paths[i], reason);
            }
        }

        _files[id] = infos;
        var good = sounds.Where(sound => sound is not null).Select(sound => sound!).ToArray();
        if (good.Length == 0)
        {
            return new RenderedRecipe(id, [], [], Cached: false);
        }

        // A file that could not be read: the next good one plays in its place.
        var next = 0;
        var chosen = new DecodedSound[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            chosen[i] = sounds[i] ?? good[next++ % good.Length];
        }

        var lengths = new int[paths.Count];
        var rates = new int[paths.Count];
        var channels = new int[paths.Count];
        var samples = new short[chosen.Sum(sound => (long)sound.Samples.Length)];
        var offset = 0;
        for (var i = 0; i < chosen.Length; i++)
        {
            var sound = chosen[i];
            lengths[i] = sound.Samples.Length;
            rates[i] = sound.SampleRate;
            channels[i] = sound.Channels;
            sound.Samples.CopyTo(samples, offset);
            offset += sound.Samples.Length;
        }

        return new RenderedRecipe(id, lengths, samples, Cached: false, rates, channels);
    }

    /// <summary>Renders every variation of <paramref name="recipe"/>, level by level: their lengths and samples.</summary>
    internal static (int[] Lengths, short[] Samples) Render(OneShotRecipe recipe, int sampleRate)
    {
        var total = recipe.Levels * recipe.VariationsPerLevel;
        var scratch = new float[recipe.SampleCount(sampleRate)];
        var lengths = new int[total];
        var pcm = new short[total * scratch.Length];
        var offset = 0;
        for (var level = 0; level < recipe.Levels; level++)
        {
            for (var variation = 0; variation < recipe.VariationsPerLevel; variation++)
            {
                var length = recipe.RenderVariation(scratch, sampleRate, level, variation);
                Dsp.ToPcm16(scratch.AsSpan(0, length), pcm.AsSpan(offset, length));
                lengths[(level * recipe.VariationsPerLevel) + variation] = length;
                offset += length;
            }
        }

        return (lengths, pcm[..offset]);
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Audio: rendered {Recipes} one-shot recipe(s) and sound(s) from files: {Variations} variation(s), {Kilobytes} KB, {Cached} read from the sound cache, in {Ms:F0} ms"
    )]
    private partial void LogRendered(int recipes, int variations, long kilobytes, int cached, double ms);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audio: sound recipe {Name} failed to render; it stays silent")]
    private partial void LogFailed(Exception exception, string name);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Audio: sound {Name}: the file {Path} cannot be read ({Reason}); it plays nothing"
    )]
    private partial void LogFileFailed(string name, string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio: sound {Name}: the file {Path}: {Problem}")]
    private partial void LogFileWarning(string name, string path, string problem);
}
