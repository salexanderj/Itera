using Itera.Engine.Audio;
using Itera.Engine.Audio.Files;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// The music (<see cref="MusicPlayer"/>, <see cref="MusicStreamer"/>, <see cref="MusicDecks"/>) pumped round by round
/// on the in-memory device (<see cref="MusicHarness"/>): a track fades in from its first block, stereo at the listener; a
/// new one cross-fades from the last at equal power (0.71 each halfway); a command during a fade goes on from the gains
/// then; a stop fades out; the music volume and sound off set the gains while the music streams on; a track played once
/// ends and <see cref="MusicPlayer.Current"/> goes back to none; loops go round their loop points, or whole, with no gap
/// (in memory, a WAV file's <c>smpl</c> loop, an Ogg file's tags against the file decoded whole), and one whose start
/// cannot be reached again plays to its end once; the same track again
/// does nothing and a restart plays it from its start, and one that just ended plays again; a track fading in keeps its
/// pace until the next is heard; a stop hurries a slower fade out; a file that cannot be read is named at the start, one
/// that fails while it plays is named once (what is queued plays out), and either way the old track fades out; tracks
/// are declared once by name before the
/// start; the menu's track; with no audio thread the commands still set what plays; and nothing allocates.
/// </summary>
public sealed class MusicTests : IDisposable
{
    private const float Halfway = 0.70710677f;

    private readonly MusicHarness _music = new();

    public void Dispose() => _music.Dispose();

    [Fact]
    public void A_track_fades_in_from_its_first_block_stereo_at_the_listener()
    {
        var a = _music.Track("a", 48_000 * 10);
        _music.Start();
        _music.Music.Play(a);

        _music.StepUntilHeard(a);
        Assert.Equal(0, _music.GainOf(a));
        _music.Run(1);
        Assert.Equal(Halfway, _music.GainOf(a), 3);
        _music.Run(1);

        Assert.Equal(1, _music.GainOf(a));
        var source = _music.SourceOf(a);
        Assert.True(source.AtListener);
        Assert.True(source.Direct);
        Assert.Equal(1, source.Pitch);
        Assert.Equal(a, _music.Music.Current);
        Assert.Equal("a", _music.Music.CurrentName);
    }

    [Fact]
    public void A_new_track_cross_fades_from_the_last_at_equal_power()
    {
        var a = _music.Track("a", 48_000 * 10);
        var b = _music.Track("b", 48_000 * 10);
        _music.Start();
        _music.Music.Play(a, fadeSeconds: 0);
        _music.StepUntilHeard(a);
        Assert.Equal(1, _music.GainOf(a));

        _music.Music.Play(b);
        _music.StepUntilHeard(b);
        Assert.Equal(1, _music.GainOf(a));
        Assert.Equal(0, _music.GainOf(b));
        _music.Run(1);
        Assert.Equal(Halfway, _music.GainOf(a), 3);
        Assert.Equal(Halfway, _music.GainOf(b), 3);
        _music.Run(0.5f);
        var (ga, gb) = (_music.GainOf(a), _music.GainOf(b));
        Assert.Equal(1, ga * ga + gb * gb, 3);
        Assert.InRange(gb, 0.9f, 0.95f);
        _music.Run(0.5f);
        _music.Step();

        Assert.Equal(1, _music.GainOf(b));
        Assert.Equal(-1, _music.DeckOf(a));
        Assert.Equal(1, _music.Device.Playing());
        Assert.Equal(b, _music.Music.Current);
    }

    [Fact]
    public void A_command_during_a_fade_goes_on_from_the_gains_then()
    {
        var a = _music.Track("a", 48_000 * 10);
        var b = _music.Track("b", 48_000 * 10);
        _music.Start();
        _music.Music.Play(a, fadeSeconds: 0);
        _music.StepUntilHeard(a);
        var deckA = _music.DeckOf(a);
        _music.Music.Play(b);
        _music.StepUntilHeard(b);
        _music.Run(1);

        // Back to a, halfway: it fades back in from 0.71, and b out from there, over the half of the fade left.
        _music.Music.Play(a);
        _music.Run(0.5f);
        Assert.InRange(_music.GainOf(a), 0.9f, 0.95f);
        Assert.InRange(_music.GainOf(b), 0.35f, 0.4f);
        _music.Run(0.5f);
        _music.Step();

        Assert.Equal(1, _music.GainOf(a));
        Assert.Equal(-1, _music.DeckOf(b));
        // The same deck all along, never started again.
        Assert.Equal(deckA, _music.DeckOf(a));
        Assert.Equal(1, _music.SourceOf(a).Starts);
        Assert.Equal(a, _music.Music.Current);
    }

    [Fact]
    public void Stop_fades_the_music_out_and_nothing_is_current()
    {
        var a = _music.Track("a", 48_000 * 10);
        _music.Start();
        _music.Music.Play(a, fadeSeconds: 0);
        _music.StepUntilHeard(a);

        _music.Music.Stop(1);
        Assert.Equal(-1, _music.Music.Current);
        _music.Step();
        _music.Run(0.5f);
        Assert.Equal(Halfway, _music.GainOf(a), 3);
        _music.Run(0.5f);
        _music.Step();

        Assert.Equal(-1, _music.DeckOf(a));
        Assert.Equal(0, _music.Device.Playing());
    }

    [Fact]
    public void The_music_volume_and_sound_off_set_the_gains_and_the_music_streams_on()
    {
        var a = _music.Track("a", 48_000 * 10);
        _music.Start();
        _music.Music.Play(a, fadeSeconds: 0);
        _music.StepUntilHeard(a);

        _music.Volume = 0.5f;
        _music.Step();
        Assert.Equal(0.5f, _music.GainOf(a));
        _music.Enabled = false;
        _music.Step();
        Assert.Equal(0, _music.GainOf(a));
        var queued = _music.SourceOf(a).Queued.Count;
        _music.Run(1);
        Assert.True(_music.SourceOf(a).Queued.Count > queued + 48_000 * 2 * 3 / 4);
        _music.Enabled = true;
        _music.Step();

        Assert.Equal(0.5f, _music.GainOf(a));
        Assert.Equal(0, _music.Stats.MusicUnderruns);
    }

    [Fact]
    public void A_track_played_once_ends_and_nothing_is_current()
    {
        var once = _music.Track("once", 24_000, loop: false, channels: 1);
        _music.Start();
        _music.Music.Play(once, fadeSeconds: 0);
        _music.StepUntilHeard(once);
        var source = _music.SourceOf(once);
        Assert.False(source.Direct);

        _music.Run(1);

        Assert.Equal(-1, _music.DeckOf(once));
        Assert.Equal(-1, _music.Music.Current);
        Assert.Equal(Enumerable.Range(0, 24_000).Select(f => FakeDecoder.Sample(f)), source.Queued);
    }

    [Fact]
    public void A_loop_goes_round_its_loop_points_with_no_gap()
    {
        var loop = _music.Track("loop", 30_000, points: new LoopPoints(10_000, 30_000));
        _music.Start();
        _music.Music.Play(loop, fadeSeconds: 0);
        _music.StepUntilHeard(loop);

        _music.Run(2);

        var queued = _music.SourceOf(loop).Queued;
        Assert.True(queued.Count > 2 * 90_000);
        for (var i = 0; i < queued.Count / 2; i++)
        {
            var frame = i < 30_000 ? i : 10_000 + (i - 30_000) % 20_000;
            if (queued[2 * i] != FakeDecoder.Sample(frame) || queued[2 * i + 1] != -FakeDecoder.Sample(frame))
            {
                Assert.Fail($"frame {i} played frame {queued[2 * i] - 1}, not {frame}");
            }
        }

        // The header, the decoder playing, and the one waiting at the loop's start, which take turns.
        Assert.Equal(3, _music.OpensOf("loop.fake"));
        Assert.Equal(0, _music.Stats.MusicUnderruns);
    }

    [Fact]
    public void A_loop_naming_no_points_goes_round_the_whole_sound()
    {
        var loop = _music.Track("loop", 7_000, channels: 1, rate: 22_050);
        _music.Start();
        _music.Music.Play(loop, fadeSeconds: 0);
        _music.StepUntilHeard(loop);

        _music.Run(1);

        var queued = _music.SourceOf(loop).Queued;
        Assert.True(queued.Count > 3 * 7_000);
        Assert.Equal(Enumerable.Range(0, queued.Count).Select(i => FakeDecoder.Sample(i % 7_000)), queued);
    }

    [Fact]
    public void A_wav_file_naming_a_loop_goes_round_it_through_the_streamer()
    {
        var folder = Path.Combine(Path.GetTempPath(), "itera-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "hum.wav");
            var builder = new WavBuilder { Channels = 2, Loop = (2205, 8819) };
            System.IO.File.WriteAllBytes(path, builder.Build(WavBuilder.Ramp(11_025, 2)));
            var whole = SoundFiles.Decode(path, keepStereo: true, 30).Samples;
            var hum = _music.Music.Declare("hum", path);
            _music.Start();
            _music.Music.Play(hum, fadeSeconds: 0);
            _music.StepUntilHeard(hum);

            _music.Run(1);

            AssertLooped(_music.SourceOf(hum).Queued, whole, 2, new LoopPoints(2205, 8820));
            Assert.Equal(new LoopPoints(2205, 8820), _music.Music.Tracks[hum].LoopPoints);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void An_ogg_loop_through_the_streamer_plays_the_file_decoded_whole_round_its_tags()
    {
        var path = VorbisDecoderTests.Fixture("tone-stereo-loop.ogg");
        var whole = SoundFiles.Decode(path, keepStereo: true, 30).Samples;
        var tone = _music.Music.Declare("tone", path);
        _music.Start();
        _music.Music.Play(tone, fadeSeconds: 0);
        _music.StepUntilHeard(tone);

        _music.Run(1.5f);

        var track = _music.Music.Tracks[tone];
        Assert.Equal((2, 22_050, 11_025L), (track.Channels, track.SampleRate, track.Frames));
        AssertLooped(_music.SourceOf(tone).Queued, whole, 2, new LoopPoints(2205, 11_025));
        Assert.DoesNotContain(_music.Log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public void The_same_track_again_does_nothing_and_a_restart_plays_it_from_its_start()
    {
        var a = _music.Track("a", 48_000 * 10);
        _music.Start();
        _music.Music.Play(a, fadeSeconds: 0);
        _music.StepUntilHeard(a);
        var first = _music.DeckOf(a);
        _music.Run(0.5f);

        _music.Music.Play(a);
        _music.Run(0.5f);
        Assert.Equal(1, _music.Device.Playing());
        Assert.Equal(1, _music.GainOf(a));

        _music.Music.Play(a, fadeSeconds: 1, restart: true);
        _music.Step();
        _music.Run(0.2f);
        var second = _music.Device.Get((uint)(1 - first) + 1);
        Assert.True(second.Playing);
        Assert.Equal(FakeDecoder.Sample(0), second.Queued[0]);
        _music.Run(1);

        Assert.False(_music.Device.Get((uint)first + 1).Playing);
        Assert.Equal(1, second.Gain);
        Assert.Equal(a, _music.Music.Current);
    }

    [Fact]
    public void A_track_whose_file_cannot_be_read_is_named_at_the_start_and_the_old_one_fades_out()
    {
        var a = _music.Track("a", 48_000 * 10);
        var gone = _music.Music.Declare("gone", Path.Combine(Path.GetTempPath(), "itera-no-such", "gone.ogg"));
        _music.Start();
        var error = Assert.Single(_music.Log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("gone.ogg", error.Message, StringComparison.Ordinal);
        Assert.Contains("no such file", error.Message, StringComparison.Ordinal);
        Assert.Equal("no such file", _music.Music.Tracks[gone].Error);
        _music.Music.Play(a, fadeSeconds: 0);
        _music.StepUntilHeard(a);

        _music.Music.Play(gone, fadeSeconds: 1);
        Assert.Equal(-1, _music.Music.Current);
        _music.Run(0.5f);
        Assert.InRange(_music.GainOf(a), 0.6f, 0.75f);
        _music.Run(0.6f);

        Assert.Equal(0, _music.Device.Playing());
        Assert.Single(_music.Log.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void A_file_failing_as_its_track_plays_is_named_once_and_the_old_one_fades_out()
    {
        var a = _music.Track("a", 48_000 * 10);
        var bad = _music.Track("bad", 48_000 * 10, failAt: 0);
        _music.Start();
        Assert.DoesNotContain(_music.Log.Entries, entry => entry.Level >= LogLevel.Warning);
        _music.Music.Play(a, fadeSeconds: 0);
        _music.StepUntilHeard(a);

        _music.Music.Play(bad, fadeSeconds: 1);
        _music.Run(0.2f);
        Assert.Equal(-1, _music.Music.Current);
        _music.Run(1);
        _music.Music.Play(bad, fadeSeconds: 0);
        _music.Run(0.2f);

        Assert.Equal(0, _music.Device.Playing());
        Assert.Equal(-1, _music.Music.Current);
        var error = Assert.Single(_music.Log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("music track bad", error.Message, StringComparison.Ordinal);
        Assert.Contains("damaged (fake)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_track_fading_in_keeps_its_pace_until_the_next_one_is_heard_and_then_goes_at_that_ones()
    {
        var a = _music.Track("a", 48_000 * 10);
        var b = _music.Track("b", 48_000 * 10);
        _music.Start();
        _music.Music.Play(a);
        _music.StepUntilHeard(a);
        _music.Run(0.5f);
        var gain = _music.GainOf(a);

        // b's first block comes a round after the command: until then a goes on fading in over its 2 s.
        _music.Music.Play(b, fadeSeconds: 0);
        _music.Step();
        Assert.InRange(_music.GainOf(a), gain, gain + 0.01f);
        _music.Step();

        Assert.Equal(-1, _music.DeckOf(a));
        Assert.Equal(1, _music.GainOf(b));
    }

    [Fact]
    public void A_stop_hurries_a_slower_fade_out_already_going()
    {
        var a = _music.Track("a", 48_000 * 10);
        var b = _music.Track("b", 48_000 * 10);
        _music.Start();
        _music.Music.Play(a, fadeSeconds: 0);
        _music.StepUntilHeard(a);
        _music.Music.Play(b, fadeSeconds: 60);
        _music.StepUntilHeard(b);
        _music.Run(1);

        _music.Music.Stop(0.5f);
        _music.Step();
        _music.Run(0.5f);
        _music.Step();

        Assert.Equal(0, _music.Device.Playing());
    }

    [Fact]
    public void A_track_played_again_as_soon_as_it_ended_plays()
    {
        var once = _music.Track("once", 12_000, loop: false);
        _music.Start();
        _music.Music.Play(once, fadeSeconds: 0);
        _music.StepUntilHeard(once);
        while (_music.DeckOf(once) >= 0)
        {
            _music.Step();
        }

        // Before the next round publishes anything, the end is known.
        Assert.Equal(-1, _music.Music.Current);
        _music.Music.Play(once, fadeSeconds: 0);
        _music.StepUntilHeard(once);
        Assert.Equal(once, _music.Music.Current);
    }

    [Fact]
    public void A_file_failing_after_it_is_heard_plays_out_what_is_queued_then_ends()
    {
        // 11 whole blocks read; the twelfth fails.
        var bad = _music.Track("bad", 48_000 * 10, failAt: 48_000);
        _music.Start();
        _music.Music.Play(bad, fadeSeconds: 0);
        _music.StepUntilHeard(bad);
        var source = _music.SourceOf(bad);

        _music.Run(0.8f);
        Assert.True(source.Playing);
        Assert.Equal(bad, _music.Music.Current);
        _music.Run(0.3f);

        Assert.Equal(-1, _music.Music.Current);
        Assert.False(source.Playing);
        var frames = 11 * MusicFeed.BlockFrames;
        Assert.Equal(
            Enumerable.Range(0, frames).SelectMany(f => new[] { FakeDecoder.Sample(f), (short)-FakeDecoder.Sample(f) }),
            source.Queued
        );
    }

    [Fact]
    public void A_loop_whose_start_cannot_be_reached_again_plays_to_its_end_once_and_stops()
    {
        // The header and the decoder playing open; the one to wait at the loop's start does not.
        var opens = 0;
        _music.File(
            "lost.fake",
            () =>
                ++opens <= 2
                    ? new FakeDecoder(2, 48_000, 30_000, new LoopPoints(10_000, 30_000))
                    : throw new SoundFileException("gone (fake)")
        );
        var lost = _music.Music.Declare("lost", "lost.fake");
        _music.Start();
        _music.Music.Play(lost, fadeSeconds: 0);
        _music.StepUntilHeard(lost);
        var source = _music.SourceOf(lost);

        _music.Run(1);

        Assert.Equal(-1, _music.Music.Current);
        Assert.Equal(0, _music.Device.Playing());
        Assert.Equal(
            Enumerable.Range(0, 30_000).SelectMany(f => new[] { FakeDecoder.Sample(f), (short)-FakeDecoder.Sample(f) }),
            source.Queued
        );
        var error = Assert.Single(_music.Log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("gone (fake)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tracks_are_declared_once_by_name_before_the_start()
    {
        var music = new MusicPlayer(new RecordingLogger());
        var theme = music.Declare("theme", "Music/theme.ogg");

        Assert.Throws<ArgumentException>(() => music.Declare("theme", "Music/other.ogg"));
        Assert.Throws<ArgumentException>(() => music.Play("nope"));
        Assert.True(music.TryFind("theme", out var found));
        Assert.Equal(theme, found);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Music/theme.ogg"), music.Tracks[theme].Path);
        for (var i = 1; i < MusicPlayer.MaxTracks; i++)
        {
            music.Declare("track" + i, "Music/track.ogg");
        }

        Assert.Throws<InvalidOperationException>(() => music.Declare("one more", "Music/track.ogg"));
        var started = new MusicPlayer(new RecordingLogger());
        started.ReadTracks();
        Assert.Throws<InvalidOperationException>(() => started.Declare("late", "Music/late.ogg"));
    }

    [Fact]
    public void The_menu_track_is_the_one_the_option_names_and_one_no_game_declared_is_named_once()
    {
        using var named = new MusicHarness(menuMusic: "theme");
        named.Track("other", 1000);
        var theme = named.Track("theme", 1000);
        named.Start();
        Assert.Equal(theme, named.Music.MenuTrack);

        using var missing = new MusicHarness(menuMusic: "theme");
        missing.Track("other", 1000);
        missing.Start();
        Assert.Equal(-1, missing.Music.MenuTrack);
        Assert.Equal(-1, missing.Music.MenuTrack);
        var error = Assert.Single(missing.Log.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains("MenuMusic names the music track theme", error.Message, StringComparison.Ordinal);
        using var none = new MusicHarness();
        Assert.Equal(-1, none.Music.MenuTrack);
    }

    [Fact]
    public void With_no_audio_thread_the_commands_still_set_what_plays()
    {
        var music = new MusicPlayer(new RecordingLogger());
        var theme = music.Declare("theme", "Music/theme.ogg");

        music.Play(theme);
        music.Publish(running: false);
        Assert.Equal(theme, music.Current);
        music.Stop();
        music.Publish(running: false);
        Assert.Equal(-1, music.Current);
    }

    [Fact]
    public void Playing_looping_and_cross_fading_music_allocates_nothing()
    {
        using var music = new MusicHarness(record: false);
        // Decoders made up front, so opening a file allocates nothing either.
        var decoders = new Stack<ISoundDecoder>();
        for (var i = 0; i < 400; i++)
        {
            decoders.Push(new FakeDecoder(2, 48_000, 20_000, new LoopPoints(5_000, 20_000)));
        }

        music.File("a.fake", decoders.Pop);
        music.File("b.fake", decoders.Pop);
        var a = music.Music.Declare("a", "a.fake");
        var b = music.Music.Declare("b", "b.fake");
        music.Start();
        Cycle(music, a, b);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 4; i++)
        {
            Cycle(music, a, b);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, music.Stats.MusicUnderruns);
    }

    /// <summary>a, then b cross-fading, a back halfway, a stop, a again at once, the music volume and sound off: about 4 s.</summary>
    private static void Cycle(MusicHarness music, int a, int b)
    {
        music.Music.Play(a, fadeSeconds: 0.5f);
        music.Run(1);
        music.Music.Play(b, fadeSeconds: 1);
        music.Run(0.5f);
        music.Music.Play(a, fadeSeconds: 0.5f);
        music.Run(1);
        music.Music.Stop(0.2f);
        music.Run(0.3f);
        music.Music.Play(a, fadeSeconds: 0);
        music.Volume = 0.5f;
        music.Run(0.5f);
        music.Enabled = false;
        music.Run(0.3f);
        music.Enabled = true;
        music.Volume = 1;
        music.Run(0.3f);
    }

    /// <summary>
    /// <paramref name="queued"/> is <paramref name="whole"/> up to the loop's end, then the loop over and over, frame for
    /// frame.
    /// </summary>
    private static void AssertLooped(List<short> queued, short[] whole, int channels, LoopPoints loop)
    {
        var frames = queued.Count / channels;
        Assert.True(frames > loop.End + 2 * loop.Length, $"{frames} frames played");
        for (var i = 0; i < frames; i++)
        {
            var frame = i < loop.End ? i : loop.Start + (i - loop.End) % loop.Length;
            for (var c = 0; c < channels; c++)
            {
                if (queued[i * channels + c] != whole[frame * channels + c])
                {
                    Assert.Fail(
                        $"frame {i} channel {c}: {queued[i * channels + c]}, not frame {frame}'s {whole[frame * channels + c]}"
                    );
                }
            }
        }
    }
}
