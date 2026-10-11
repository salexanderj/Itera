using System.Numerics;
using Itera.Engine.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// Sounds from files sustained in a loop (<see cref="FileLoopRecipe"/>) through the mixer on the in-memory device: a file
/// naming a loop has its loop points on its buffer and plays from its start at full gain, round the loop; one naming none
/// starts at a random frame and fades in over 80 ms; two keys share one buffer; one no longer sustained fades out over
/// 80 ms and stops; loops are never cut short for a louder one-shot; a loop in the world moves with the origin, its fade
/// kept; one at the listener, or kept stereo, plays there, as a stream at the listener does; sustaining them allocates
/// nothing.
/// </summary>
public sealed class FileLoopTests : IDisposable
{
    private const int Rate = 48_000;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "itera-loops-" + Guid.NewGuid().ToString("N"));
    private readonly FakeAudioDevice _device = new();
    private readonly SoundRecipes _recipes = new();
    private readonly AudioStats _stats = new();
    private readonly AudioExchange _exchange = new();
    private readonly AudioMixer _mixer;
    private double _now;

    public FileLoopTests()
    {
        Directory.CreateDirectory(_folder);
        _mixer = new AudioMixer(_device, _recipes, _stats, _exchange, 4, Rate, NullLogger.Instance, renderOnPump: true);
    }

    public void Dispose()
    {
        _mixer.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_file_naming_a_loop_plays_its_intro_once_then_round_its_loop_at_full_gain()
    {
        // 0.5 s at 22,050 Hz; the loop from 0.1 s to 0.4 s.
        var hum = Loop("hum", new WavBuilder { Loop = (2205, 8819) }, 11_025, gain: 0.5f);
        var at = new Vector3(0, 0, -3);

        Sustain(0.008f, Hum(hum, at));
        var source = _device.PlayingAt(at)!;

        Assert.True(source.Looping);
        Assert.Equal(0, source.StartFrame);
        Assert.Equal(0.5f, source.Gain, 5);
        Assert.Equal((2205, 8820), _device.LoopOf(source.Static));

        Sustain(1, Hum(hum, at));
        Assert.True(source.Playing);
        Assert.InRange(source.Laps, 2, 4);
        Assert.InRange(source.At, 0.1f, 0.4f);
        Assert.Equal(1, _stats.FileLoops);
    }

    [Fact]
    public void A_file_naming_no_loop_starts_at_a_random_frame_and_fades_in()
    {
        var hum = Loop("hum", new WavBuilder(), 11_025);
        var at = new Vector3(2, 0, 0);

        Sustain(0.008f, Hum(hum, at));
        var source = _device.PlayingAt(at)!;
        Assert.InRange(source.StartFrame, 0, 11_024);
        Assert.Equal(0f, source.Gain);

        Sustain(0.04f, Hum(hum, at));
        Assert.InRange(source.Gain, 0.4f, 0.6f);

        Sustain(0.06f, Hum(hum, at));
        Assert.Equal(1f, source.Gain);
    }

    [Fact]
    public void Two_keys_share_one_buffer_and_one_no_longer_sustained_fades_out_and_stops()
    {
        var hum = Loop("hum", new WavBuilder { Loop = (0, 11_024) }, 11_025);
        var left = new Vector3(-2, 0, 0);
        var right = new Vector3(2, 0, 0);

        Sustain(0.1f, Hum(hum, left), Hum(hum, right, key: 2));
        var a = _device.PlayingAt(left)!;
        var b = _device.PlayingAt(right)!;
        Assert.Equal(a.Static, b.Static);
        Assert.NotSame(a, b);

        // The right one is no longer sustained: it fades out over 80 ms, then stops.
        Sustain(0.04f, Hum(hum, left));
        Assert.InRange(b.Gain, 0.4f, 0.6f);
        Sustain(0.06f, Hum(hum, left));
        Assert.False(b.Playing);
        Assert.True(a.Playing);
        Assert.Equal(1, _stats.FileLoops);
    }

    [Fact]
    public void Loops_are_never_cut_short_for_a_louder_one_shot()
    {
        var hum = Loop("hum", new WavBuilder { Loop = (0, 11_024) }, 11_025);
        var click = _recipes.Register("click", new FileOneShotRecipe(Wav("click", new WavBuilder(), 2205)));
        var loops = Enumerable.Range(0, 4).Select(i => Hum(hum, new Vector3(i * 3 + 20, 0, 0), key: i)).ToArray();
        Sustain(0.1f, loops);

        Post([new SoundEvent { Recipe = click, Gain = 4 }], loops);

        Assert.Equal(4, _stats.FileLoops);
        Assert.Equal(1, _stats.Refused);
    }

    [Fact]
    public void A_loop_in_the_world_moves_with_the_origin_its_fade_kept()
    {
        var hum = Loop("hum", new WavBuilder(), 11_025, gain: 0.8f);
        var at = new Vector3(5, 0, 0);
        Sustain(0.04f, Hum(hum, at));
        var gain = _device.PlayingAt(at)!.Gain;
        var by = new Vector3(100, 0, 0);

        // The game sustains it at its new local place; its fade goes on from where it was, one round further in.
        _exchange.Shift(by);
        Post([], [Hum(hum, at - by)]);

        var moved = _device.PlayingAt(at - by);
        Assert.NotNull(moved);
        Assert.InRange(moved.Gain, gain, gain + 0.05f * 0.8f + 1e-4f);
    }

    [Fact]
    public void A_loop_at_the_listener_or_kept_stereo_plays_there_as_a_stream_at_the_listener_does()
    {
        var ambience = Loop("wind", new WavBuilder(), 11_025);
        var stereo = _recipes.Register(
            "rain",
            new FileLoopRecipe(Wav("rain", new WavBuilder { Channels = 2 }, 11_025)) { KeepStereo = true }
        );
        var tone = EngineTone();

        Sustain(
            0.1f,
            new SoundLoop
            {
                Recipe = ambience,
                Key = 1,
                AtListener = true,
            },
            new SoundLoop
            {
                Recipe = stereo,
                Key = 2,
                Position = new Vector3(9, 0, 0),
            },
            new SoundLoop
            {
                Recipe = tone,
                Key = 3,
                AtListener = true,
                Level = 1,
            }
        );

        var atListener = 0;
        for (uint i = 1; i <= _device.Sources; i++)
        {
            atListener += _device.Get(i).Playing && _device.Get(i).AtListener ? 1 : 0;
        }

        Assert.Equal(3, atListener);
        Assert.Equal(2, _stats.FileLoops);
        Assert.Equal(1, _stats.Streams);
    }

    [Fact]
    public void Sustaining_loops_allocates_nothing()
    {
        var hum = Loop("hum", new WavBuilder(), 11_025);
        var loops = new[] { Hum(hum, Vector3.UnitX), Hum(hum, Vector3.UnitY, key: 2) };
        Sustain(0.5f, loops);

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 500; i++)
        {
            _device.Advance(0.004f);
            _now += 0.004;
            // Every other round one is not sustained: it fades, then starts again.
            Post([], i % 50 < 25 ? loops : loops.AsSpan(0, 1));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private int Loop(string name, WavBuilder builder, int frames, float gain = 1) =>
        _recipes.Register(name, new FileLoopRecipe(Wav(name, builder, frames)) { Gain = gain });

    private string Wav(string name, WavBuilder builder, int frames)
    {
        var path = Path.Combine(_folder, name + ".wav");
        File.WriteAllBytes(path, builder.Build(WavBuilder.Ramp(frames, builder.Channels)));
        return path;
    }

    private int EngineTone()
    {
        EngineSounds.Register(_recipes);
        _recipes.TryFind("tone", out var tone);
        return tone;
    }

    private static SoundLoop Hum(int recipe, Vector3 at, int key = 1) =>
        new()
        {
            Recipe = recipe,
            Key = key,
            Position = at,
            Level = 1,
        };

    /// <summary>Sustains <paramref name="loops"/> for <paramref name="seconds"/> in rounds 4 ms apart (the first rounds read the files).</summary>
    private void Sustain(float seconds, params SoundLoop[] loops)
    {
        for (var t = 0f; t < seconds; t += 0.004f)
        {
            Post([], loops);
            _device.Advance(0.004f);
            _now += 0.004;
        }
    }

    private void Post(ReadOnlySpan<SoundEvent> events, ReadOnlySpan<SoundLoop> loops)
    {
        _exchange.Post(events, loops, ListenerPose.Default, MixLevels.Full);
        _mixer.Pump(_now);
    }
}
