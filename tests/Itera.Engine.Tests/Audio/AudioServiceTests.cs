using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// <see cref="AudioService"/>: with no device it starts no thread and its calls do nothing; with one, a played sound
/// reaches the audio thread and plays; queueing and publishing allocate nothing; a wrong recipe id throws; WAV files
/// hold every variation; a thread that does not stop in time leaves the device open; a device failing on the audio thread
/// leaves the game going, silent; events past an iteration's most are counted; loops are held for their time.
/// </summary>
public sealed class AudioServiceTests
{
    private static AudioService Create()
    {
        var options = new EngineOptions();
        options.Audio.MaxVoices = 8;
        // Never the real data folder's sound cache.
        options.Audio.Cache = false;
        return new AudioService(Options.Create(options), NullLogger<AudioService>.Instance);
    }

    private static SoundEvent Bar(AudioService audio)
    {
        audio.Recipes.TryFind("bar", out var bar);
        return new SoundEvent { Recipe = bar, Position = new Vector3(0, 0, -1) };
    }

    /// <summary>
    /// When the match's origin moves, the sounds queued this iteration, the loops sustained and held, and what waits for
    /// the audio thread (its events and listener) move the other way, and the move waits for the audio thread's next take.
    /// </summary>
    [Fact]
    public void A_move_of_the_origin_moves_the_sounds_queued_sustained_held_and_waiting()
    {
        using var audio = Create();
        audio.Initialize(new NullAudioDevice());
        audio.QueueWithoutThread = true;
        audio.Recipes.TryFind("bar", out var bar);
        audio.Recipes.TryFind("tone", out var tone);
        var listener = new ListenerPose(new Vector3(4, 0, 0), -Vector3.UnitZ, Vector3.UnitY);
        audio.Exchange.Post(
            [new SoundEvent { Recipe = bar, Position = new Vector3(5, 0, 0) }],
            [],
            in listener,
            MixLevels.Full
        );
        audio.Play(new SoundEvent { Recipe = bar, Position = new Vector3(1, 0, 0) });
        audio.Sustain(
            new SoundLoop
            {
                Recipe = tone,
                Key = 1,
                Position = new Vector3(2, 0, 0),
            }
        );
        audio.SustainFor(
            new SoundLoop
            {
                Recipe = tone,
                Key = 2,
                Position = new Vector3(3, 0, 0),
            },
            0,
            5
        );
        var by = new Vector3(300, -20, -512);

        audio.Shift(by);

        Assert.Equal(new Vector3(1, 0, 0) - by, audio.Queued[0].Position);
        Assert.Equal(new Vector3(2, 0, 0) - by, audio.Sustained[0].Position);
        Assert.Equal(new Vector3(3, 0, 0) - by, audio.Held[0].Position);
        var events = new SoundEvent[AudioExchange.MaxEvents];
        var loops = new SoundLoop[AudioExchange.MaxLoops];
        audio.Exchange.Take(events, out var count, loops, out _, out var taken, out _, out var shift);
        Assert.Equal((1, new Vector3(5, 0, 0) - by), (count, events[0].Position));
        Assert.Equal((by, new Vector3(4, 0, 0) - by), (shift, taken.Position));
        audio.Exchange.Take(events, out _, loops, out _, out _, out _, out var again);
        Assert.Equal(Vector3.Zero, again);
    }

    [Fact]
    public void With_no_device_nothing_starts_and_calls_do_nothing()
    {
        using var audio = Create();
        audio.Initialize(new NullAudioDevice());
        audio.Start();

        audio.Play(Bar(audio));
        audio.Publish(ListenerPose.Default, 0);

        Assert.False(audio.IsRunning);
        Assert.Equal(0, audio.Stats.Played);
        Assert.Equal("none (silent)", audio.DeviceName);
    }

    [Fact]
    public void A_played_sound_reaches_the_audio_thread_and_plays()
    {
        using var audio = Create();
        var device = new FakeAudioDevice();
        audio.Initialize(device);
        audio.Start();
        // Sounds asked for while their recipe renders are late and dropped, and those asked for in several rounds before
        // it is ready all play once it is: so wait for every recipe, then ask once.
        var wait = Stopwatch.StartNew();
        while (!audio.AllRendered && wait.Elapsed < TestTime.Limit(20))
        {
            audio.Publish(ListenerPose.Default, 0);
            Thread.Sleep(50);
        }

        audio.Play(Bar(audio));
        while (audio.Stats.Played == 0 && wait.Elapsed < TestTime.Limit(20))
        {
            audio.Publish(ListenerPose.Default, 0);
            Thread.Sleep(50);
        }

        audio.Stop();
        Assert.Equal(1, audio.Stats.Played);
        Assert.Equal(audio.Recipes.PreparedCount, audio.Stats.RenderedRecipes);
        // The thread freed its sources on the way out.
        Assert.Equal(8, device.DeletedSources);
    }

    /// <summary>
    /// A track played reaches the music thread, which decodes it, and the audio thread, which plays it on a deck at full
    /// gain (no fade); at the stop both threads end and the decks' sources are freed with the pool's.
    /// </summary>
    [Fact]
    public void A_track_played_reaches_the_music_and_audio_threads_and_plays()
    {
        using var audio = Create();
        var device = new FakeAudioDevice();
        audio.Initialize(device);
        var theme = audio.Music.Declare("theme", VorbisDecoderTests.Fixture("tone-stereo-loop.ogg"));
        audio.Start();
        audio.Music.Play(theme, fadeSeconds: 0);

        var wait = Stopwatch.StartNew();
        while (!Heard(audio.Stats, theme) && wait.Elapsed < TestTime.Limit(20))
        {
            audio.Publish(ListenerPose.Default, 0);
            Thread.Sleep(20);
        }

        Assert.True(Heard(audio.Stats, theme));
        Assert.True(audio.MusicDecodedSeconds > 0);
        audio.Stop();
        Assert.Equal(10, device.DeletedSources);
    }

    /// <summary>A deck plays <paramref name="track"/> at full gain.</summary>
    private static bool Heard(AudioStats stats, int track) =>
        (stats.MusicDeckTrack(0) == track && stats.MusicDeckGain(0) == 1)
        || (stats.MusicDeckTrack(1) == track && stats.MusicDeckGain(1) == 1);

    /// <summary>
    /// An audio thread stuck in the device (rendering a recipe) when the game stops: <see cref="AudioService.Stop"/>
    /// says so and leaves the device open, so the loop's shutdown and the container do not close it under the thread.
    /// </summary>
    [Fact]
    public void A_thread_that_does_not_stop_in_time_leaves_the_device_open()
    {
        using var hold = new ManualResetEventSlim(false);
        var device = new FakeAudioDevice { Hold = hold };
        using var audio = Create();
        audio.StopTimeout = TimeSpan.FromMilliseconds(50);
        audio.Initialize(device);
        audio.Start();
        Assert.True(device.Holding.Wait(TestTime.Limit(20)), "The audio thread never made a buffer.");

        var stopped = audio.Stop();
        hold.Set();

        Assert.False(stopped);
        Assert.True(device.LeftOpen);
    }

    [Fact]
    public void Queueing_and_publishing_allocate_nothing()
    {
        using var audio = Create();
        audio.Initialize(new FakeAudioDevice());
        audio.Start();
        var sound = Bar(audio);
        audio.Recipes.TryFind("tone", out var tone);
        var loop = new SoundLoop { Recipe = tone, Key = 1 };
        for (var i = 0; i < 100; i++)
        {
            audio.Play(in sound);
            audio.Sustain(in loop);
            audio.Publish(ListenerPose.Default, i);
        }

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            audio.Play(in sound);
            audio.Sustain(in loop);
            audio.Publish(ListenerPose.Default, i);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// A device that fails on the audio thread ends sound, not the game: the thread logs it and stops, sound counts as
    /// rendered so loading does not wait, and calls go on doing nothing.
    /// </summary>
    [Fact]
    public void A_device_that_fails_on_the_audio_thread_leaves_the_game_going_silent()
    {
        using var audio = Create();
        audio.Initialize(new FakeAudioDevice { FailSources = true });
        audio.Start();
        var wait = Stopwatch.StartNew();
        while (audio.IsRunning && wait.Elapsed < TestTime.Limit(10))
        {
            Thread.Sleep(5);
        }

        Assert.False(audio.IsRunning);
        Assert.True(audio.AllRendered);
        audio.Play(Bar(audio));
        audio.Publish(ListenerPose.Default, 0);
        Assert.True(audio.Stop());
    }

    /// <summary>
    /// Past the most events in one iteration, the rest are dropped and counted.
    /// </summary>
    [Fact]
    public void Past_the_most_events_in_an_iteration_the_rest_are_dropped_and_counted()
    {
        using var audio = Create();
        audio.Initialize(new FakeAudioDevice());
        audio.Start();
        var sound = Bar(audio);

        for (var i = 0; i < AudioService.MaxEventsPerIteration + 20; i++)
        {
            audio.Play(in sound);
        }

        Assert.Equal(20, audio.DroppedEvents);
    }

    /// <summary>
    /// A sustained loop is held for <see cref="AudioService.HoldSeconds"/> after the last iteration that sustained it,
    /// one held for a test for its own time, and none while sound is off.
    /// </summary>
    [Fact]
    public void Loops_are_held_for_their_time_and_dropped_while_off()
    {
        using var audio = Create();
        audio.Initialize(new FakeAudioDevice());
        audio.Start();
        audio.Recipes.TryFind("tone", out var tone);
        var loop = new SoundLoop { Recipe = tone, Key = 1 };

        audio.Sustain(in loop);
        audio.Publish(ListenerPose.Default, 0);
        audio.Publish(ListenerPose.Default, AudioService.HoldSeconds * 0.5);
        Assert.Equal(1, audio.HeldLoops);
        audio.Publish(ListenerPose.Default, AudioService.HoldSeconds * 1.5);
        Assert.Equal(0, audio.HeldLoops);

        var test = loop with { Key = 2 };
        audio.SustainFor(in test, 10, 4);
        audio.Publish(ListenerPose.Default, 13.9);
        Assert.Equal(1, audio.HeldLoops);
        audio.Enabled = false;
        audio.Publish(ListenerPose.Default, 13.95);
        Assert.Equal(0, audio.HeldLoops);
    }

    [Fact]
    public void A_recipe_that_does_not_exist_throws()
    {
        using var audio = Create();
        audio.Initialize(new FakeAudioDevice());
        audio.Start();

        Assert.Throws<ArgumentOutOfRangeException>(() => audio.Play(new SoundEvent { Recipe = 99 }));
    }

    [Fact]
    public void A_wav_file_holds_every_variation()
    {
        using var audio = Create();
        audio.Initialize(new NullAudioDevice());
        var folder = Path.Combine(Path.GetTempPath(), "itera-sound-" + Guid.NewGuid().ToString("N"));
        try
        {
            audio.Recipes.TryFind("tone", out var tone);
            var path = audio.WriteWav(tone, folder);
            // 8 s of 16-bit mono at 48 kHz, whole blocks, and the header.
            var blocks = (int)(8f * 48_000 / AudioMixer.BlockSamples);
            Assert.Equal(44 + blocks * AudioMixer.BlockSamples * 2, new FileInfo(path).Length);
            Assert.Equal("tone.wav", Path.GetFileName(path));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
