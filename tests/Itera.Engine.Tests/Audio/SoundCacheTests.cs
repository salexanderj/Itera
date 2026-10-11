using System.Numerics;
using Itera.Engine.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// <see cref="SoundCache"/> in a temporary folder: a written recipe reads back sample for sample, the key follows the
/// rate, the levels, and the name, a damaged or short file is deleted, only the newest versions of a recipe are kept
/// (and "step" never takes "step-soft"'s), and a mixer with a cache renders a recipe once: a second mixer reads the
/// same samples without rendering. A stream's first generator is made while the recipes render. The key holds the
/// build's sound code, a failed write leaves reads working, and pruning removes removed recipes' and crashed writes' files.
/// </summary>
public sealed class SoundCacheTests : IDisposable
{
    private const int Rate = 48_000;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "itera-sound-cache-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>A short ramp whose height follows its level and seed, counting its renders.</summary>
    private sealed class CountingRecipe : OneShotRecipe
    {
        public CountingRecipe(int levels = 2, int variations = 2)
        {
            Levels = levels;
            VariationsPerLevel = variations;
        }

        public int Renders { get; private set; }

        public override float Seconds => 0.01f;

        public override void Render(Span<float> output, int sampleRate, float level, uint seed)
        {
            Renders++;
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = (0.1f + 0.5f * level + (seed % 7) * 0.01f) * (i / (float)output.Length);
            }
        }
    }

    private sealed class HumRecipe : StreamRecipe
    {
        public int Created { get; private set; }

        public override SoundGenerator Create(int sampleRate)
        {
            Created++;
            return new HumGenerator();
        }
    }

    private sealed class HumGenerator : SoundGenerator
    {
        public override void Reset(uint seed) { }

        public override void Render(Span<float> output, float level) => output.Fill(0.5f * level);
    }

    private SoundCache Cache() => new(_folder, NullLogger.Instance);

    [Fact]
    public void A_written_recipe_reads_back_sample_for_sample()
    {
        var cache = Cache();
        var recipe = new CountingRecipe();
        var path = cache.PathFor("ramp", recipe, Rate);
        int[] lengths = [480, 100, 480, 7];
        var samples = new short[lengths.Sum()];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)((i * 37) - 20_000);
        }

        cache.Write(path, "ramp", recipe, Rate, lengths, samples);

        Assert.True(cache.TryRead(path, recipe, Rate, out var readLengths, out var readSamples));
        Assert.Equal(lengths, readLengths);
        Assert.Equal(samples, readSamples);
    }

    [Fact]
    public void The_key_follows_the_rate_the_levels_and_the_name()
    {
        var recipe = new CountingRecipe();
        var key = SoundCache.Key("ramp", recipe, Rate);

        Assert.Equal(key, SoundCache.Key("ramp", new CountingRecipe(), Rate));
        Assert.NotEqual(key, SoundCache.Key("ramp", recipe, 44_100));
        Assert.NotEqual(key, SoundCache.Key("ramp", new CountingRecipe(levels: 3), Rate));
        Assert.NotEqual(key, SoundCache.Key("ramp", new CountingRecipe(variations: 3), Rate));
        Assert.NotEqual(key, SoundCache.Key("slope", recipe, Rate));
    }

    [Fact]
    public void A_damaged_or_short_file_is_deleted()
    {
        var cache = Cache();
        var recipe = new CountingRecipe();
        var path = cache.PathFor("ramp", recipe, Rate);
        int[] lengths = [480, 480, 480, 480];
        var samples = new short[1920];
        cache.Write(path, "ramp", recipe, Rate, lengths, samples);
        var bytes = File.ReadAllBytes(path);

        File.WriteAllBytes(path, bytes[..^2]);
        Assert.False(cache.TryRead(path, recipe, Rate, out _, out _));
        Assert.False(File.Exists(path));

        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.False(cache.TryRead(path, recipe, Rate, out _, out _));
        Assert.False(File.Exists(path));

        // Read with another recipe's levels: the header does not match.
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.False(cache.TryRead(path, new CountingRecipe(levels: 1, variations: 4), Rate, out _, out _));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Only_the_newest_versions_of_a_recipe_are_kept()
    {
        var cache = Cache();
        var recipe = new CountingRecipe();
        int[] lengths = [1, 1, 1, 1];
        short[] samples = [1, 2, 3, 4];
        var other = cache.PathFor("step-soft", recipe, Rate);
        cache.Write(other, "step-soft", recipe, Rate, lengths, samples);

        // Older versions of "step": files with other keys, written earlier.
        var paths = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var old = Path.Combine(_folder, $"step-{i:X32}.sound");
            File.Copy(other, old);
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(i - 10));
            paths.Add(old);
        }

        var path = cache.PathFor("step", recipe, Rate);
        cache.Write(path, "step", recipe, Rate, lengths, samples);

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(paths[2]));
        Assert.False(File.Exists(paths[1]));
        Assert.False(File.Exists(paths[0]));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void A_mixer_with_a_cache_renders_a_recipe_once()
    {
        var (first, firstRecipe) = RenderAll(Cache());
        var (second, secondRecipe) = RenderAll(Cache());

        Assert.Equal(4, firstRecipe.Renders);
        Assert.Equal(0, secondRecipe.Renders);
        Assert.Equal(first.FilledSamples, second.FilledSamples);
        Assert.Equal(first.FilledHash, second.FilledHash);
        Assert.Single(Directory.GetFiles(_folder, "*.sound"));
    }

    [Fact]
    public void A_mixer_without_a_cache_renders_every_time_and_writes_nothing()
    {
        var (_, recipe) = RenderAll(null);
        var (_, again) = RenderAll(null);

        Assert.Equal(4, recipe.Renders);
        Assert.Equal(4, again.Renders);
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void Every_one_shot_is_counted_as_rendered_and_a_stream_has_its_first_generator()
    {
        var recipes = new SoundRecipes();
        var hum = new HumRecipe();
        recipes.Register("hum", hum);
        recipes.Register("ramp", new CountingRecipe());
        var stats = new AudioStats();
        using var mixer = new AudioMixer(
            new FakeAudioDevice(),
            recipes,
            stats,
            new AudioExchange(),
            8,
            Rate,
            NullLogger.Instance,
            Cache()
        );

        Assert.Equal(1, recipes.PreparedCount);
        mixer.Pump(0);

        Assert.Equal(recipes.PreparedCount, stats.RenderedRecipes);
        Assert.Equal(1, hum.Created);
    }

    /// <summary>
    /// The key holds the sound code the build wrote for the engine, not its module version id, which the commit's hash
    /// changes; an assembly without the metadata (a mod's, this test's) falls back to its module version id.
    /// </summary>
    [Fact]
    public void The_key_holds_the_sound_code_not_the_module_version_id()
    {
        var engine = typeof(SoundCache).Assembly;
        var code = SoundCache.SoundCodeOf(engine);
        var recipes = new SoundRecipes();
        EngineSounds.Register(recipes);
        recipes.TryFind("bar", out var bar);

        var text = SoundCache.KeyText("bar", (OneShotRecipe)recipes[bar], Rate);

        Assert.Matches("^[0-9A-F]{32}$", code);
        Assert.Contains(code, text, StringComparison.Ordinal);
        Assert.DoesNotContain(engine.ManifestModule.ModuleVersionId.ToString("N"), text, StringComparison.Ordinal);
        Assert.Equal(
            SoundCache.Key("bar", (OneShotRecipe)recipes[bar], Rate),
            SoundCache.Key("bar", (OneShotRecipe)recipes[bar], Rate)
        );
        var tests = typeof(SoundCacheTests).Assembly;
        Assert.Equal(tests.ManifestModule.ModuleVersionId.ToString("N"), SoundCache.SoundCodeOf(tests));
    }

    /// <summary>
    /// A write that fails stops writes but not reads (one failed write stopped both).
    /// </summary>
    [Fact]
    public void A_failed_write_leaves_reads_working()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cache = Cache();
        var first = new CountingRecipe();
        var firstPath = cache.PathFor("first", first, Rate);
        var (lengths, samples) = RecipeRenderer.Render(first, Rate);
        cache.Write(firstPath, "first", first, Rate, lengths, samples);
        var second = new CountingRecipe(levels: 3);
        var secondPath = cache.PathFor("second", second, Rate);
        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            cache.Write(secondPath, "second", second, Rate, lengths, samples);

            Assert.False(File.Exists(secondPath));
            Assert.True(cache.TryRead(firstPath, first, Rate, out var read, out _));
            Assert.Equal(lengths, read);
        }
        finally
        {
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// Once the recipes are rendered, files of recipes no longer registered and temporary files over an hour old go;
    /// the registered recipes' files, other files, and a fresh temporary file stay.
    /// </summary>
    [Fact]
    public void Pruning_deletes_removed_recipes_files_and_old_temporary_files()
    {
        var cache = Cache();
        var recipes = new SoundRecipes();
        var kept = new CountingRecipe();
        recipes.Register("kept", kept);
        var (lengths, samples) = RecipeRenderer.Render(kept, Rate);
        var keptPath = cache.PathFor("kept", kept, Rate);
        cache.Write(keptPath, "kept", kept, Rate, lengths, samples);
        var gonePath = cache.PathFor("gone", kept, Rate);
        cache.Write(gonePath, "gone", kept, Rate, lengths, samples);
        var oldTemporary = Path.Combine(_folder, "kept-x.sound.1234.tmp");
        var newTemporary = Path.Combine(_folder, "kept-y.sound.5678.tmp");
        var other = Path.Combine(_folder, "notes.txt");
        File.WriteAllText(oldTemporary, "half");
        File.SetLastWriteTimeUtc(oldTemporary, DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(newTemporary, "half");
        File.WriteAllText(other, "mine");

        cache.Prune(recipes);

        Assert.True(File.Exists(keptPath));
        Assert.False(File.Exists(gonePath));
        Assert.False(File.Exists(oldTemporary));
        Assert.True(File.Exists(newTemporary));
        Assert.True(File.Exists(other));
    }

    private static (FakeAudioDevice Device, CountingRecipe Recipe) RenderAll(SoundCache? cache)
    {
        var device = new FakeAudioDevice();
        var recipes = new SoundRecipes();
        var recipe = new CountingRecipe();
        recipes.Register("ramp", recipe);
        var exchange = new AudioExchange();
        using var mixer = new AudioMixer(
            device,
            recipes,
            new AudioStats(),
            exchange,
            8,
            Rate,
            NullLogger.Instance,
            cache
        );
        exchange.Post([], [], new ListenerPose(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY), MixLevels.Full);
        mixer.Pump(0);
        return (device, recipe);
    }
}
