using Itera.Engine.Audio.Synthesis;

namespace Itera.Engine.Audio;

/// <summary>
/// The engine's own recipes: <c>bar</c>, a struck metal bar (a one-shot from a <see cref="ModalBank"/>), and
/// <c>tone</c>, a soft 330 Hz hum (a stream) to hear where a source sits, for checking the sound path without the game;
/// and the menu's clicks, played at the listener: <c>menu-hover</c>, a soft click as
/// the pointer comes onto a button, and <c>menu-press</c>, a fuller click as one is pressed.
/// </summary>
public static class EngineSounds
{
    /// <summary>The menu's click as the pointer comes onto a button.</summary>
    public const string MenuHover = "menu-hover";

    /// <summary>The menu's click as a button is pressed.</summary>
    public const string MenuPress = "menu-press";

    /// <summary>Registers the recipes in <paramref name="recipes"/>.</summary>
    public static void Register(SoundRecipes recipes)
    {
        recipes.Register("bar", new StruckBar());
        recipes.Register("tone", new Tone());
        // Before the game's recipes, so they are rendered first and the menu clicks as soon as they are, while the rest
        // still render.
        recipes.Register(MenuHover, new MenuHoverClick());
        recipes.Register(MenuPress, new MenuPressClick());
    }

    /// <summary>
    /// A free metal bar struck in the middle: the modes of a bending bar (1, 2.756, 5.404, and 8.933 times the lowest;
    /// Farnell, section 4.3), higher ones dying faster, struck harder and shorter at a higher level so the high modes ring
    /// more.
    /// </summary>
    private sealed class StruckBar : OneShotRecipe
    {
        private static readonly float[] s_ratios = [1, 2.756f, 5.404f, 8.933f];

        public override float Seconds => 2.5f;

        public override void Render(Span<float> output, int sampleRate, float level, uint seed)
        {
            var random = new SoundRandom(seed);
            var bank = new ModalBank();
            var fundamental = 440 * random.Range(0.97f, 1.03f);
            for (var i = 0; i < s_ratios.Length; i++)
            {
                bank.Add(fundamental * s_ratios[i], 2.2f / (1 + i * 1.5f), 1f / (1 + i), sampleRate);
            }

            // A harder strike is a shorter contact: 3 ms soft to 0.5 ms hard.
            var contact = (int)(sampleRate * Dsp.Lerp(0.003f, 0.0005f, level));
            var force = Dsp.Lerp(0.2f, 1, level) * 0.02f;
            for (var i = 0; i < output.Length; i++)
            {
                var input = i < contact ? force * Dsp.ContactPulse((i + 0.5f) / contact) : 0;
                output[i] = bank.Process(input);
                if ((i & 511) == 511)
                {
                    bank.Flush();
                }
            }

            Dsp.Normalise(output, Dsp.Lerp(0.3f, 0.9f, level));
        }
    }

    /// <summary>
    /// A soft click: a narrow band of noise near 3.4 kHz that swells in a third of a millisecond and dies in a few, with
    /// a faint ring of a small hard body under it, dulled by a low-pass so it ticks rather than snaps.
    /// </summary>
    private sealed class MenuHoverClick : OneShotRecipe
    {
        public MenuHoverClick()
        {
            // Played at the listener, not dulled by distance: under the player's own footsteps.
            Gain = 0.4f;
            Levels = 1;
            VariationsPerLevel = 4;
        }

        public override float Seconds => 0.04f;

        public override void Render(Span<float> output, int sampleRate, float level, uint seed)
        {
            var random = new SoundRandom(seed);
            var noise = new NoiseSource(seed ^ 0x5BD1_E995);
            var tap = new StateVariableFilter();
            tap.Set(random.Range(3000, 3800), 2.5f, sampleRate);
            var body = new ModalBank();
            body.Add(random.Range(1800, 2300), 0.012f, 1, sampleRate);
            var soften = new OnePole();
            soften.SetCutoff(6000, sampleRate);
            var decay = random.Range(0.0025f, 0.0035f);
            var contact = (int)(sampleRate * 0.001f);
            for (var i = 0; i < output.Length; i++)
            {
                var t = i / (float)sampleRate;
                var push = i < contact ? 0.004f * Dsp.ContactPulse((i + 0.5f) / contact) : 0;
                var click = tap.BandPass(noise.White()) * Swell(t, 0.0003f, decay);
                output[i] = soften.LowPass(click + body.Process(push));
            }

            Dsp.Normalise(output, 0.3f);
        }
    }

    /// <summary>
    /// A fuller click, a button pressed home: a short, hard contact rings a small body's three modes near 1.5 kHz, a
    /// longer push under it thumps a lower body near 180 Hz, a burst of bright noise gives the first millisecond its
    /// edge, and the latch settles with a faint second tick 18 to 28 ms later.
    /// </summary>
    private sealed class MenuPressClick : OneShotRecipe
    {
        private static readonly float[] s_ratios = [1, 2.32f, 4.25f];

        public MenuPressClick()
        {
            // About 7 dB over the hover's click, and about the player's own footsteps.
            Gain = 0.3f;
            Levels = 1;
            VariationsPerLevel = 4;
        }

        public override float Seconds => 0.15f;

        public override void Render(Span<float> output, int sampleRate, float level, uint seed)
        {
            var random = new SoundRandom(seed);
            var noise = new NoiseSource(seed ^ 0x27D4_EB2F);
            var edge = new StateVariableFilter();
            edge.Set(random.Range(4500, 5500), 1.5f, sampleRate);
            var tick = new ModalBank();
            var high = random.Range(1400, 1600);
            for (var i = 0; i < s_ratios.Length; i++)
            {
                tick.Add(high * s_ratios[i], 0.025f / (1 + i * 0.8f), 1f / (1 + i * 0.7f), sampleRate);
            }

            var thump = new ModalBank();
            var low = random.Range(170, 190);
            thump.Add(low, 0.045f, 1, sampleRate);
            thump.Add(low * 2.4f, 0.035f, 0.5f, sampleRate);
            var hard = Math.Max(1, (int)(sampleRate * 0.0004f));
            var soft = Math.Max(1, (int)(sampleRate * 0.0025f));
            var settle = (int)(sampleRate * random.Range(0.018f, 0.028f));
            var settleStrength = random.Range(0.2f, 0.3f);
            for (var i = 0; i < output.Length; i++)
            {
                var t = i / (float)sampleRate;
                var strike = Pulse(i, hard) + settleStrength * Pulse(i - settle, hard);
                var push = Pulse(i, soft);
                var bright = edge.BandPass(noise.White()) * Swell(t, 0.0001f, 0.0008f);
                output[i] = 0.02f * tick.Process(strike) + 0.002f * thump.Process(push) + 0.3f * bright;
                if ((i & 511) == 511)
                {
                    tick.Flush();
                    thump.Flush();
                }
            }

            Dsp.Normalise(output, 0.6f);
        }

        /// <summary>A contact <paramref name="samples"/> long, starting at sample 0, read at sample <paramref name="i"/>.</summary>
        private static float Pulse(int i, int samples) =>
            i < 0 || i >= samples ? 0 : Dsp.ContactPulse((i + 0.5f) / samples);
    }

    /// <summary>
    /// A tap's loudness at <paramref name="t"/> seconds: rising over <paramref name="rise"/>, then falling by e every
    /// <paramref name="decay"/>.
    /// </summary>
    private static float Swell(float t, float rise, float decay) =>
        t <= 0 ? 0 : MathF.Min(1, t / rise) * MathF.Exp(-t / decay);

    /// <summary>A sine at 330 Hz with a little of its octave, at the level's loudness.</summary>
    private sealed class Tone : StreamRecipe
    {
        public override SoundGenerator Create(int sampleRate) => new ToneGenerator(sampleRate);
    }

    private sealed class ToneGenerator(int sampleRate) : SoundGenerator
    {
        private double _phase;
        private float _level;

        public override void Reset(uint seed)
        {
            _phase = 0;
            _level = 0;
        }

        public override void Render(Span<float> output, float level)
        {
            var step = 2 * Math.PI * 330 / sampleRate;
            var from = _level;
            for (var i = 0; i < output.Length; i++)
            {
                var gain = Dsp.Lerp(from, level, (i + 1) / (float)output.Length) * 0.4f;
                output[i] = gain * (float)(Math.Sin(_phase) + 0.25 * Math.Sin(2 * _phase));
                _phase += step;
            }

            _phase %= 2 * Math.PI;
            _level = level;
        }
    }
}
