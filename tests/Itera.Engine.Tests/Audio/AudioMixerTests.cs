using System.Numerics;
using Itera.Engine.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// <see cref="AudioMixer"/> on <see cref="FakeAudioDevice"/>: one-shots play where they are and free their voices,
/// sounds too quiet at the listener are never started, a sound played at the listener plays at its full gain, a louder
/// sound takes the quietest voice and a quieter one is refused, a level never plays the same variation twice running,
/// streams keep their queues full, fade out when no longer sustained, and reuse their generators, a one-shot whose recipe
/// is still rendering is dropped as late while the rest play, a recipe or stream that throws leaves the rest playing, a
/// device error is counted, loudness stops at the most gain, off stops everything, disposing frees what was made, and a
/// pump allocates nothing.
/// </summary>
public sealed class AudioMixerTests
{
    private const int Rate = 48_000;

    private sealed class Mixer
    {
        public Mixer(int voices = 8, bool renderOnPump = true)
        {
            Recipes.Register("click", new ClickRecipe());
            Recipes.Register("hum", Hum);
            Engine = new AudioMixer(
                Device,
                Recipes,
                Stats,
                Exchange,
                voices,
                Rate,
                NullLogger.Instance,
                renderOnPump: renderOnPump
            );
            if (!renderOnPump)
            {
                // As the render thread would: the click, then the mixer takes it into buffers.
                Engine.Renderer.RenderNext();
            }

            // The first rounds render the click (one recipe a round) and pass over the stream.
            Engine.Pump(0);
            Engine.Pump(0);
        }

        public FakeAudioDevice Device { get; } = new();

        public SoundRecipes Recipes { get; } = new();

        public AudioStats Stats { get; } = new();

        public AudioExchange Exchange { get; } = new();

        public HumRecipe Hum { get; } = new();

        public AudioMixer Engine { get; }

        public double Now { get; private set; }

        public Vector3 Listener { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>The effects' volume posted with each round.</summary>
        public float Effects { get; set; } = 1;

        /// <summary>Posts <paramref name="events"/> and <paramref name="loops"/> and pumps once.</summary>
        public void Pump(SoundEvent[]? events = null, SoundLoop[]? loops = null)
        {
            Exchange.Post(
                events ?? [],
                loops ?? [],
                new ListenerPose(Listener, -Vector3.UnitZ, Vector3.UnitY),
                MixLevels.Full with
                {
                    Effects = Effects,
                    Enabled = Enabled,
                }
            );
            Engine.Pump(Now);
        }

        /// <summary>Moves time on by <paramref name="seconds"/> in pumps 4 ms apart, sustaining <paramref name="loops"/>.</summary>
        public void Run(float seconds, SoundLoop[]? loops = null)
        {
            for (var t = 0f; t < seconds; t += 0.004f)
            {
                Device.Advance(0.004f);
                Now += 0.004;
                Pump(loops: loops);
            }
        }
    }

    /// <summary>A 0.1 s click whose loudness follows its level.</summary>
    private sealed class ClickRecipe : OneShotRecipe
    {
        public ClickRecipe()
        {
            Levels = 2;
            VariationsPerLevel = 3;
        }

        public override float Seconds => 0.1f;

        public override void Render(Span<float> output, int sampleRate, float level, uint seed)
        {
            for (var i = 0; i < output.Length; i++)
            {
                output[i] = (0.2f + 0.6f * level) * (1 - i / (float)output.Length) * ((i & 1) == 0 ? 1 : -1);
            }
        }
    }

    /// <summary>A click whose rendering waits for <c>gate</c>: a recipe that takes long.</summary>
    private sealed class SlowRecipe(ManualResetEventSlim gate) : OneShotRecipe
    {
        public override float Seconds => 0.1f;

        public override void Render(Span<float> output, int sampleRate, float level, uint seed)
        {
            gate.Wait();
            output.Fill(0.1f);
        }
    }

    private sealed class ThrowingRecipe : OneShotRecipe
    {
        public override float Seconds => 0.1f;

        public override void Render(Span<float> output, int sampleRate, float level, uint seed) =>
            throw new InvalidOperationException("broken recipe");
    }

    /// <summary>A stream whose generators throw from their tenth block.</summary>
    private sealed class FailingStream : StreamRecipe
    {
        public int Created { get; private set; }

        public override SoundGenerator Create(int sampleRate)
        {
            Created++;
            return new FailingGenerator();
        }
    }

    private sealed class FailingGenerator : SoundGenerator
    {
        private int _blocks;

        public override void Reset(uint seed) { }

        public override void Render(Span<float> output, float level)
        {
            if (++_blocks > 10)
            {
                throw new InvalidOperationException("broken stream");
            }

            output.Fill(0.2f * level);
        }
    }

    internal sealed class HumRecipe : StreamRecipe
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

    private static SoundEvent Click(Vector3 position, float level = 1, float gain = 1) =>
        new()
        {
            Recipe = 0,
            Position = position,
            Level = level,
            Gain = gain,
        };

    private static SoundLoop Hum(Vector3 position, int key = 1) =>
        new()
        {
            Recipe = 1,
            Key = key,
            Position = position,
            Level = 1,
        };

    [Fact]
    public void A_one_shot_plays_where_it_is_and_frees_its_voice_when_it_ends()
    {
        var mixer = new Mixer();
        var at = new Vector3(1, 0, 0);

        mixer.Pump([Click(at)]);

        Assert.NotNull(mixer.Device.PlayingAt(at));
        Assert.Equal(1, mixer.Stats.OneShots);
        Assert.Equal(1, mixer.Stats.Played);
        Assert.Equal(1, mixer.Stats.RenderedRecipes);

        mixer.Run(0.2f);

        Assert.Equal(0, mixer.Device.Playing());
        Assert.Equal(0, mixer.Stats.OneShots);
    }

    /// <summary>
    /// When the match's origin moves, a one-shot playing in the world moves the other way (heard from where it was), one
    /// at the listener stays, and an event still waiting for the mixer plays where it belongs.
    /// </summary>
    [Fact]
    public void A_move_of_the_origin_moves_the_one_shots_playing_and_waiting()
    {
        var mixer = new Mixer();
        var at = new Vector3(1, 0, 2);
        var menu = Click(Vector3.Zero) with { AtListener = true };
        mixer.Pump([Click(at), menu]);
        var by = new Vector3(300, -20, -512);
        mixer.Exchange.Post([Click(new Vector3(3, 0, 0))], [], ListenerPose.Default, MixLevels.Full);

        mixer.Exchange.Shift(by);
        mixer.Listener -= by;
        mixer.Pump();

        Assert.NotNull(mixer.Device.PlayingAt(at - by));
        Assert.Null(mixer.Device.PlayingAt(at));
        Assert.NotNull(mixer.Device.PlayingAt(Vector3.Zero));
        Assert.NotNull(mixer.Device.PlayingAt(new Vector3(3, 0, 0) - by));
        Assert.Equal(-by, mixer.Device.ListenerPosition);
    }

    /// <summary>
    /// The effects volume scales the gain of every voice, one-shots and streams, and sets the voices playing again when
    /// it changes. The master volume stays the listener's gain.
    /// </summary>
    [Fact]
    public void The_effects_volume_scales_every_voice_and_sets_them_again_when_it_changes()
    {
        var mixer = new Mixer();
        var hum = new SoundLoop
        {
            Recipe = 1,
            Key = 3,
            Position = new Vector3(0, 0, -2),
            Level = 1,
            Gain = 0.8f,
        };
        mixer.Pump([Click(new Vector3(1, 0, 0), gain: 0.5f)], [hum]);
        Assert.Equal(0.5f, mixer.Device.PlayingAt(new Vector3(1, 0, 0))!.Gain, 5);
        Assert.Equal(0.8f, mixer.Device.PlayingAt(hum.Position)!.Gain, 5);

        mixer.Effects = 0.25f;
        mixer.Pump(loops: [hum]);

        Assert.Equal(0.125f, mixer.Device.PlayingAt(new Vector3(1, 0, 0))!.Gain, 5);
        Assert.Equal(0.2f, mixer.Device.PlayingAt(hum.Position)!.Gain, 5);
        // The master volume is the listener's gain, apart from the effects'.
        Assert.Equal(1f, mixer.Device.ListenerGain);
    }

    [Fact]
    public void A_move_of_the_origin_keeps_the_effects_volume()
    {
        var mixer = new Mixer { Effects = 0.5f };
        var at = new Vector3(3, 0, 0);
        mixer.Pump([Click(at)]);
        var by = new Vector3(40, 0, 0);

        mixer.Exchange.Shift(by);
        mixer.Listener -= by;
        mixer.Pump();

        Assert.Equal(0.5f, mixer.Device.PlayingAt(at - by)!.Gain, 5);
    }

    /// <summary>A stream fading out after its last round, when the origin moves, is heard from where it was.</summary>
    [Fact]
    public void A_move_of_the_origin_moves_a_stream_fading_out()
    {
        var mixer = new Mixer();
        var at = new Vector3(2, 0, 1);
        mixer.Run(
            0.1f,
            [
                new SoundLoop
                {
                    Recipe = 1,
                    Key = 7,
                    Position = at,
                    Level = 1,
                },
            ]
        );
        // Not sustained this round: it fades out over 80 ms.
        mixer.Pump();
        var by = new Vector3(300, -20, -512);

        mixer.Exchange.Shift(by);
        mixer.Listener -= by;
        mixer.Pump();

        Assert.NotNull(mixer.Device.PlayingAt(at - by));
        Assert.Null(mixer.Device.PlayingAt(at));
    }

    [Fact]
    public void A_sound_too_quiet_where_the_listener_is_is_never_started()
    {
        var mixer = new Mixer();

        // The recipe's reference distance is 2 m: at 2 km the gain is 0.001, under the cull.
        mixer.Pump([Click(new Vector3(2000, 0, 0))]);

        Assert.Equal(0, mixer.Device.Playing());
        Assert.Equal(1, mixer.Stats.Culled);
    }

    [Fact]
    public void A_sound_at_the_listener_plays_at_its_full_gain_however_far_its_position_is()
    {
        var mixer = new Mixer(voices: 1);
        mixer.Listener = new Vector3(5000, 0, 0);
        // 5 km from the listener: placed in the world, it would be culled.
        var menu = Click(Vector3.Zero);
        menu.AtListener = true;

        mixer.Pump([menu]);

        var source = mixer.Device.Get(1);
        Assert.True(source.Playing);
        Assert.True(source.AtListener);
        Assert.Equal(1, mixer.Stats.Played);
        Assert.Equal(0, mixer.Stats.Culled);

        // Its voice goes back into the world for its next sound.
        mixer.Run(0.2f);
        mixer.Pump([Click(new Vector3(4999, 0, 0))]);
        Assert.True(source.Playing);
        Assert.False(source.AtListener);
    }

    [Fact]
    public void A_louder_sound_takes_the_quietest_voice_and_a_quieter_one_is_refused()
    {
        var mixer = new Mixer(voices: 8);
        var events = new SoundEvent[8];
        for (var i = 0; i < 8; i++)
        {
            // The first is the quietest: 20 m away.
            events[i] = Click(new Vector3(i == 0 ? 20 : 1, 0, i));
        }

        mixer.Pump(events);
        Assert.Equal(8, mixer.Device.Playing());

        var loud = new Vector3(0, 0, 0.5f);
        mixer.Pump([Click(loud)]);
        Assert.NotNull(mixer.Device.PlayingAt(loud));
        Assert.Null(mixer.Device.PlayingAt(new Vector3(20, 0, 0)));
        Assert.Equal(1, mixer.Stats.Stolen);

        mixer.Pump([Click(new Vector3(50, 0, 0))]);
        Assert.Equal(1, mixer.Stats.Refused);
        Assert.Equal(8, mixer.Device.Playing());
    }

    [Fact]
    public void A_level_never_plays_the_same_variation_twice_running()
    {
        var mixer = new Mixer();
        var used = new HashSet<uint>();
        uint last = 0;
        for (var i = 0; i < 40; i++)
        {
            mixer.Pump([Click(Vector3.Zero, level: 0)]);
            var buffer = mixer.Device.PlayingAt(Vector3.Zero)!.Static;
            Assert.NotEqual(last, buffer);
            used.Add(buffer);
            last = buffer;
            mixer.Run(0.2f);
        }

        // All three of level 0's variations, and none of level 1's.
        Assert.Equal(3, used.Count);
    }

    [Fact]
    public void A_sustained_stream_keeps_playing_then_fades_out_and_its_generator_is_reused()
    {
        var mixer = new Mixer();
        SoundLoop[] loops = [Hum(new Vector3(0, 0, -3))];

        mixer.Run(1, loops);

        var source = mixer.Device.PlayingAt(new Vector3(0, 0, -3));
        Assert.NotNull(source);
        Assert.Equal(1, mixer.Stats.Streams);
        Assert.Equal(0, mixer.Stats.Underruns);
        Assert.True(source.Queue.Count - source.Processed >= AudioMixer.QueueDepth - 1);

        // No longer sustained: it fades out over the release and its voice is freed.
        mixer.Run(0.3f);
        Assert.Equal(0, mixer.Device.Playing());
        Assert.Equal(0, mixer.Stats.Streams);

        mixer.Run(0.1f, loops);
        Assert.Equal(1, mixer.Stats.Streams);
        Assert.Equal(1, mixer.Hum.Created);
    }

    /// <summary>
    /// A one-shot whose recipe is still rendering is dropped as late, and the others play meanwhile
    /// (every sound was dropped while any recipe rendered).
    /// </summary>
    [Fact]
    public void A_one_shot_whose_recipe_is_still_rendering_is_dropped_and_the_rest_play()
    {
        var mixer = new Mixer(renderOnPump: false);
        var later = mixer.Recipes.Register("later", new ClickRecipe());

        mixer.Pump([Click(new Vector3(1, 0, 0)), Click(new Vector3(2, 0, 0)) with { Recipe = later }]);
        Assert.Equal(1, mixer.Device.Playing());
        Assert.Equal(1, mixer.Stats.Late);

        mixer.Engine.Renderer.RenderNext();
        mixer.Pump();
        Assert.Equal(2, mixer.Stats.RenderedRecipes);
        mixer.Pump([Click(new Vector3(2, 0, 0)) with { Recipe = later }]);
        Assert.Equal(2, mixer.Device.Playing());
    }

    /// <summary>Sounds play while the render thread renders a slow recipe.</summary>
    [Fact]
    public void Sounds_play_while_the_render_thread_renders_a_slow_recipe()
    {
        var mixer = new Mixer(renderOnPump: false);
        using var gate = new ManualResetEventSlim(false);
        var slow = mixer.Recipes.Register("slow", new SlowRecipe(gate));
        var thread = new Thread(mixer.Engine.Renderer.Run) { IsBackground = true };
        thread.Start();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                mixer.Pump([Click(new Vector3(i, 0, 0))]);
            }

            Assert.Equal(5, mixer.Device.Playing());
            Assert.Equal(1, mixer.Stats.RenderedRecipes);
        }
        finally
        {
            gate.Set();
        }

        var deadline = TestTime.Deadline(10);
        while (mixer.Stats.RenderedRecipes < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the slow recipe never arrived");
            Thread.Sleep(1);
            mixer.Pump();
        }

        mixer.Engine.Renderer.Stop();
        Assert.True(thread.Join(TestTime.Limit(5)));
        mixer.Pump([Click(new Vector3(9, 0, 0)) with { Recipe = slow }]);
        Assert.Equal(6, mixer.Device.Playing());
    }

    /// <summary>
    /// A recipe that throws stays silent and counts as done, and the recipes after it render
    /// (the throw ended all sound).
    /// </summary>
    [Fact]
    public void A_recipe_that_throws_stays_silent_and_the_rest_render()
    {
        var mixer = new Mixer();
        var broken = mixer.Recipes.Register("broken", new ThrowingRecipe());
        var after = mixer.Recipes.Register("after", new ClickRecipe());

        mixer.Pump();
        mixer.Pump();
        mixer.Pump([
            Click(new Vector3(1, 0, 0)) with
            {
                Recipe = broken,
            },
            Click(new Vector3(2, 0, 0)) with
            {
                Recipe = after,
            },
        ]);

        Assert.Equal(3, mixer.Stats.RenderedRecipes);
        Assert.Equal(1, mixer.Device.Playing());
        Assert.NotNull(mixer.Device.PlayingAt(new Vector3(2, 0, 0)));
        Assert.Equal(0, mixer.Stats.Late);
    }

    /// <summary>
    /// A stream whose generator throws is ended and never started again, and the other sounds play on.
    /// </summary>
    [Fact]
    public void A_stream_whose_generator_throws_is_ended_and_not_started_again()
    {
        var mixer = new Mixer();
        var failing = new FailingStream();
        var stream = mixer.Recipes.Register("failing", failing);
        mixer.Pump();
        var loop = new SoundLoop
        {
            Recipe = stream,
            Key = 1,
            Position = Vector3.Zero,
            Level = 1,
        };

        mixer.Run(0.1f, [loop, Hum(Vector3.UnitX)]);
        Assert.Equal(1, mixer.Stats.Streams);
        var made = failing.Created;
        mixer.Run(0.1f, [loop, Hum(Vector3.UnitX)]);
        mixer.Pump([Click(Vector3.UnitY)]);

        Assert.Equal(made, failing.Created);
        Assert.Equal(1, mixer.Stats.Streams);
        Assert.NotNull(mixer.Device.PlayingAt(Vector3.UnitY));
    }

    /// <summary>
    /// The mixer asks the device to keep an output once a second, and follows its going and coming back.
    /// </summary>
    [Fact]
    public void The_output_is_checked_each_second_and_its_loss_and_return_followed()
    {
        var mixer = new Mixer();
        var asked = mixer.Device.ConnectionChecks;
        mixer.Device.Output = false;

        mixer.Run(1.05f);
        Assert.False(mixer.Stats.Connected);
        Assert.Equal(asked + 1, mixer.Device.ConnectionChecks);

        mixer.Device.Output = true;
        mixer.Run(1.0f);
        Assert.True(mixer.Stats.Connected);
        Assert.Equal(asked + 2, mixer.Device.ConnectionChecks);
    }

    /// <summary>An error the device reports is counted each round it comes.</summary>
    [Fact]
    public void A_device_error_is_counted()
    {
        var mixer = new Mixer();
        mixer.Device.PendingError = 0xA001;

        mixer.Pump();
        mixer.Pump();

        Assert.Equal(1, mixer.Stats.DeviceErrors);
    }

    /// <summary>
    /// Loudness is at most <see cref="AudioMixer.MaxGain"/>, as OpenAL plays it, so a gain past it ranks no louder: a
    /// second sound takes the only voice from one of a larger gain that has begun to fade.
    /// </summary>
    [Fact]
    public void Loudness_is_at_most_the_most_gain()
    {
        var mixer = new Mixer(voices: 1);
        mixer.Pump([Click(Vector3.Zero, gain: 8)]);
        mixer.Run(0.02f);

        mixer.Pump([Click(Vector3.UnitX * 0.1f, gain: 5)]);

        Assert.Equal(1, mixer.Stats.Stolen);
        Assert.NotNull(mixer.Device.PlayingAt(Vector3.UnitX * 0.1f));
    }

    [Fact]
    public void Two_keys_of_one_recipe_are_two_streams()
    {
        var mixer = new Mixer();

        mixer.Run(0.1f, [Hum(new Vector3(1, 0, 0), key: 1), Hum(new Vector3(-1, 0, 0), key: 2)]);

        Assert.Equal(2, mixer.Stats.Streams);
        Assert.Equal(2, mixer.Device.Playing());
    }

    [Fact]
    public void Off_stops_every_sound()
    {
        var mixer = new Mixer();
        SoundLoop[] loops = [Hum(Vector3.Zero)];
        mixer.Run(0.1f, loops);
        mixer.Pump([Click(Vector3.One)], loops);
        Assert.Equal(2, mixer.Device.Playing());

        mixer.Enabled = false;
        mixer.Pump([Click(Vector3.One)], loops);

        Assert.Equal(0, mixer.Device.Playing());
        Assert.Equal(0, mixer.Stats.OneShots + mixer.Stats.Streams);
    }

    [Fact]
    public void Disposing_frees_every_source_and_buffer()
    {
        var mixer = new Mixer(voices: 8);
        mixer.Run(0.1f, [Hum(Vector3.Zero)]);

        mixer.Engine.Dispose();
        mixer.Engine.Dispose();

        Assert.Equal(8, mixer.Device.DeletedSources);
        Assert.Equal(mixer.Device.Buffers, mixer.Device.DeletedBuffers);
    }

    [Fact]
    public void A_pump_allocates_nothing()
    {
        var mixer = new Mixer(voices: 16);
        SoundLoop[] loops = [Hum(new Vector3(0, 0, -2)), Hum(new Vector3(0, 0, 2), key: 2)];
        SoundEvent[] events = [Click(new Vector3(1, 0, 0)), Click(new Vector3(-1, 0, 0), level: 0)];
        // The streams' generators and every buffer are made during the warm-up.
        for (var i = 0; i < 200; i++)
        {
            mixer.Device.Advance(0.004f);
            mixer.Pump(i % 10 == 0 ? events : null, loops);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            mixer.Device.Advance(0.004f);
            mixer.Pump(i % 10 == 0 ? events : null, loops);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
