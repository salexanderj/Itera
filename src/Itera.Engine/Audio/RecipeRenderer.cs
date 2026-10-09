using System.Collections.Concurrent;
using System.Diagnostics;
using Itera.Engine.Audio.Synthesis;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Audio;

/// <summary>A one-shot recipe's variations as samples, ready for buffers; none when its recipe failed.</summary>
/// <param name="Id">The recipe.</param>
/// <param name="Lengths">Each variation's length in samples, level by level.</param>
/// <param name="Samples">Every variation's samples, back to back.</param>
/// <param name="Cached">True when read from the sound cache.</param>
internal sealed record RenderedRecipe(int Id, int[] Lengths, short[] Samples, bool Cached)
{
    /// <summary>True when the recipe threw: it stays silent.</summary>
    public bool Failed => Lengths.Length == 0;
}

/// <summary>
/// Renders the one-shot recipes' variations off the audio thread, in the order they were registered: read from the
/// sound cache when it holds them, or rendered and written there (rendered
/// on the audio thread, every sound waiting would be dropped while they rendered, the menu's clicks too). The audio thread
/// takes each finished recipe (<see cref="TryTake"/>) and puts it into buffers. A recipe that throws is logged and left
/// silent, and the rest go on. <see cref="RenderNext"/> runs on one thread at a
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
            if (_recipes[id] is not OneShotRecipe recipe)
            {
                continue;
            }

            if (_rendered == 0)
            {
                _start = Stopwatch.GetTimestamp();
            }

            var rendered = Render(id, recipe);
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
        Message = "Audio: rendered {Recipes} one-shot recipe(s): {Variations} variation(s), {Kilobytes} KB, {Cached} read from the sound cache, in {Ms:F0} ms"
    )]
    private partial void LogRendered(int recipes, int variations, long kilobytes, int cached, double ms);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audio: sound recipe {Name} failed to render; it stays silent")]
    private partial void LogFailed(Exception exception, string name);
}
