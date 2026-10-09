using Itera.Engine.Audio;
using Itera.Engine.Audio.Synthesis;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// The sound building blocks: a mode rings at its frequency and falls 60 dB in its decay time, the state variable
/// filter passes its centre at unity and cuts far from it, noise stays in range, flushing clears tiny states, the soft
/// clip bends towards ±1, Poisson waits average their rate, the engine's recipes render finite sound within ±1, the
/// same for the same seed, and the menu's press click is longer, louder, and lower than its hover click.
/// </summary>
public sealed class SynthesisTests
{
    private const int Rate = 48_000;

    [Fact]
    public void A_mode_rings_at_its_frequency_and_falls_60_dB_in_its_decay_time()
    {
        var bank = new ModalBank();
        bank.Add(440, 0.5f, 1, Rate);
        var samples = new float[Rate];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = bank.Process(i == 0 ? 1 : 0);
        }

        // Zero crossings upwards over the first 0.1 s: 44 cycles.
        var crossings = 0;
        for (var i = 1; i < Rate / 10; i++)
        {
            crossings += samples[i - 1] < 0 && samples[i] >= 0 ? 1 : 0;
        }

        Assert.InRange(crossings, 43, 45);
        var start = Peak(samples.AsSpan(0, 1000));
        var atDecay = Peak(samples.AsSpan(Rate / 2 - 500, 1000));
        Assert.InRange(atDecay / start, 0.0008f, 0.0013f);
    }

    [Fact]
    public void A_bank_leaves_out_modes_above_the_folding_frequency_and_past_its_size()
    {
        var bank = new ModalBank();
        bank.Add(30_000, 1, 1, Rate);
        Assert.Equal(0, bank.Count);
        for (var i = 0; i < ModalBank.MaxModes + 4; i++)
        {
            bank.Add(100 + i * 10, 1, 1, Rate);
        }

        Assert.Equal(ModalBank.MaxModes, bank.Count);
    }

    [Fact]
    public void The_band_pass_passes_its_centre_at_unity_and_cuts_far_from_it()
    {
        Assert.InRange(BandGain(1000, 1000), 0.95f, 1.05f);
        Assert.True(BandGain(1000, 8000) < 0.2f);
        Assert.True(BandGain(1000, 100) < 0.2f);
    }

    [Fact]
    public void Flushing_sets_tiny_states_to_zero()
    {
        var filter = new StateVariableFilter();
        filter.Set(500, 5, Rate);
        filter.Process(1);
        for (var i = 0; i < 200_000; i++)
        {
            filter.Process(0);
            if ((i & 511) == 511)
            {
                filter.Flush();
            }
        }

        filter.Process(0);
        Assert.Equal(0, filter.Low);
        Assert.Equal(0, filter.Band);
        Assert.Equal(0f, Dsp.Flush(1e-25f));
        Assert.Equal(1e-3f, Dsp.Flush(1e-3f));
    }

    [Fact]
    public void Noise_stays_in_range()
    {
        var noise = new NoiseSource(7);
        for (var i = 0; i < 100_000; i++)
        {
            Assert.InRange(noise.White(), -1f, 1f);
            Assert.InRange(noise.Pink(), -1.5f, 1.5f);
            Assert.InRange(noise.Brown(), -1.5f, 1.5f);
        }
    }

    [Fact]
    public void The_soft_clip_bends_towards_one()
    {
        Assert.Equal(0, Dsp.SoftClip(0));
        Assert.InRange(Dsp.SoftClip(0.1f), 0.099f, 0.1f);
        Assert.Equal(1, Dsp.SoftClip(5));
        Assert.Equal(-1, Dsp.SoftClip(-5));
        Assert.InRange(Dsp.SoftClip(2), 0.9f, 1f);
    }

    [Fact]
    public void Poisson_waits_average_their_rate()
    {
        var random = new SoundRandom(3);
        var total = 0.0;
        for (var i = 0; i < 20_000; i++)
        {
            total += random.Interval(50);
        }

        Assert.InRange(total / 20_000, 0.019, 0.021);
    }

    [Fact]
    public void The_engine_recipes_render_finite_sound_within_one_the_same_for_the_same_seed()
    {
        var recipes = new SoundRecipes();
        EngineSounds.Register(recipes);
        Assert.True(recipes.TryFind("bar", out var bar));
        var oneShot = (OneShotRecipe)recipes[bar];
        var first = new float[oneShot.SampleCount(Rate)];
        var second = new float[first.Length];

        var length = oneShot.RenderVariation(first, Rate, oneShot.Levels - 1, 2);
        oneShot.RenderVariation(second, Rate, oneShot.Levels - 1, 2);

        Assert.True(length > Rate / 2);
        Assert.Equal(first, second);
        AssertSound(first.AsSpan(0, length));
        Assert.Equal(0, first[length - 1]);

        Assert.True(recipes.TryFind("tone", out var tone));
        var generator = ((StreamRecipe)recipes[tone]).Create(Rate);
        generator.Reset(1);
        var block = new float[512];
        for (var i = 0; i < 100; i++)
        {
            generator.Render(block, 1);
            AssertSound(block);
        }
    }

    [Fact]
    public void The_menus_press_click_is_longer_louder_and_lower_than_its_hover_click()
    {
        var recipes = new SoundRecipes();
        EngineSounds.Register(recipes);
        var hover = Measure(recipes, EngineSounds.MenuHover);
        var press = Measure(recipes, EngineSounds.MenuPress);

        // Clicks: over in a few tens of milliseconds.
        Assert.InRange(hover.Seconds, 0.01f, 0.04f);
        Assert.InRange(press.Seconds, hover.Seconds, 0.1f);
        // About 7 dB more energy at the listener, with the gains, and a body under it below 400 Hz.
        Assert.InRange(press.Energy / hover.Energy, 3f, 10f);
        Assert.True(press.LowShare > 0.25f && hover.LowShare < 0.1f);
    }

    [Fact]
    public void The_table_refuses_a_name_twice_and_values_out_of_range()
    {
        var recipes = new SoundRecipes();
        EngineSounds.Register(recipes);

        Assert.Throws<InvalidOperationException>(() => recipes.Register("bar", new BadLength(1)));
        Assert.Throws<ArgumentException>(() => recipes.Register("long", new BadLength(20)));
        Assert.Equal(4, recipes.Count);
    }

    private sealed class BadLength(float seconds) : OneShotRecipe
    {
        public override float Seconds => seconds;

        public override void Render(Span<float> output, int sampleRate, float level, uint seed) { }
    }

    /// <summary>
    /// Every variation of one-shot <paramref name="name"/> rendered and checked as sound; the longest's length, and the
    /// mean energy (with the recipe's gain) and share of it below 400 Hz.
    /// </summary>
    private static (float Seconds, float Energy, float LowShare) Measure(SoundRecipes recipes, string name)
    {
        Assert.True(recipes.TryFind(name, out var id));
        var recipe = (OneShotRecipe)recipes[id];
        var samples = new float[recipe.SampleCount(Rate)];
        var longest = 0;
        var energy = 0.0;
        var low = 0.0;
        for (var level = 0; level < recipe.Levels; level++)
        {
            for (var variation = 0; variation < recipe.VariationsPerLevel; variation++)
            {
                var length = recipe.RenderVariation(samples, Rate, level, variation);
                AssertSound(samples.AsSpan(0, length));
                longest = Math.Max(longest, length);
                var filter = new OnePole();
                filter.SetCutoff(400, Rate);
                foreach (var s in samples.AsSpan(0, length))
                {
                    var g = s * recipe.Gain;
                    var l = filter.LowPass(g);
                    energy += g * g;
                    low += l * l;
                }
            }
        }

        var count = recipe.Levels * recipe.VariationsPerLevel;
        return (longest / (float)Rate, (float)(energy / count), (float)(low / energy));
    }

    private static void AssertSound(ReadOnlySpan<float> samples)
    {
        foreach (var s in samples)
        {
            Assert.True(float.IsFinite(s));
            Assert.InRange(s, -1f, 1f);
        }
    }

    private static float BandGain(float centre, float tone)
    {
        var filter = new StateVariableFilter();
        filter.Set(centre, 2, Rate);
        var peak = 0f;
        for (var i = 0; i < Rate / 2; i++)
        {
            var output = filter.BandPass(MathF.Sin(2 * MathF.PI * tone * i / Rate));
            if (i > Rate / 4)
            {
                peak = MathF.Max(peak, MathF.Abs(output));
            }
        }

        return peak;
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(s));
        }

        return peak;
    }
}
