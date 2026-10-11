using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Audio.Files;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Audio;

/// <summary>One xUnit collection for the real OpenAL device's tests: OpenAL's current context belongs to the process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OpenAlCollectionDefinition
{
    public const string Name = "OpenAL";
}

/// <summary>
/// <see cref="OpenAlAudioDevice"/> on OpenAL Soft's loopback device, which mixes into memory
/// (the real device had no test): a source plays its gain above 1 up to
/// <see cref="AudioMixer.MaxGain"/> at the listener (OpenAL held it at 1); an error a
/// call raised is kept for <see cref="IAudioDevice.TakeError"/>, not thrown by the next buffer made; a stereo buffer, and
/// a stereo music track queued on a deck, keep their left and right apart; and loop points play an intro once.
/// </summary>
[Collection(OpenAlCollectionDefinition.Name)]
public sealed class OpenAlLoopbackTests
{
    private const int Rate = 48_000;

    [Fact]
    public void A_gain_above_one_is_heard_at_the_listener_up_to_the_most()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate);
        Assert.NotNull(device);
        var buffer = device.CreateBuffer();
        var tone = new short[Rate];
        Array.Fill(tone, (short)3277);
        device.FillBuffer(buffer, tone, 1, Rate);
        var source = device.CreateSource();
        device.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, 1);

        var one = Level(device, source, buffer, 1);
        var two = Level(device, source, buffer, 2);
        var six = Level(device, source, buffer, 6);

        Assert.InRange(one, 0.05f, 0.15f);
        Assert.InRange(two / one, 1.9f, 2.1f);
        Assert.InRange(six / one, AudioMixer.MaxGain * 0.95f, AudioMixer.MaxGain * 1.05f);
        Assert.Equal(0, device.TakeError());
        device.DeleteSource(source);
        device.DeleteBuffer(buffer);
    }

    [Fact]
    public void An_error_is_kept_for_take_error_and_does_not_fail_the_next_buffer()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate);
        Assert.NotNull(device);

        // A source that does not exist: AL_INVALID_NAME, latched until read.
        device.SetSource(12_345, Vector3.Zero, 1, 1, 1, atListener: false);
        var buffer = device.CreateBuffer();

        Assert.NotEqual(0, device.TakeError());
        Assert.Equal(0, device.TakeError());
        device.DeleteBuffer(buffer);
    }

    /// <summary>
    /// A stereo buffer at the listener keeps its channels apart (direct channels): sound only on its left is heard on the
    /// output's left, and the right stays near silent.
    /// </summary>
    [Fact]
    public void A_stereo_buffer_at_the_listener_keeps_its_left_and_right_apart()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate, channels: 2);
        Assert.NotNull(device);
        var buffer = device.CreateBuffer();
        var frames = Rate / 2;
        var leftOnly = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            leftOnly[2 * i] = (short)(8000 * MathF.Sin(2 * MathF.PI * 440 * i / Rate));
        }

        device.FillBuffer(buffer, leftOnly, 2, Rate);
        var source = device.CreateSource();
        device.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, 1);
        device.SetSource(source, Vector3.Zero, 1, 1, 1, atListener: true);
        device.PlayBuffer(source, buffer);
        var output = new float[2 * 8192];
        device.RenderLoopback(output);

        var left = 0f;
        var right = 0f;
        for (var i = 4096; i < 8192; i++)
        {
            left += MathF.Abs(output[2 * i]);
            right += MathF.Abs(output[2 * i + 1]);
        }

        Assert.InRange(left / 4096, 0.1f, 0.25f);
        Assert.True(right < left * 0.01f, $"right {right / 4096} against left {left / 4096}");
        Assert.Equal(0, device.TakeError());
        device.DeleteSource(source);
        device.DeleteBuffer(buffer);
    }

    /// <summary>
    /// A stereo music track through the decks (buffers queued on a source, with direct channels set for them) keeps its
    /// channels apart: a tone only on its left is heard on the output's left, and the right stays near silent.
    /// </summary>
    [Fact]
    public void A_stereo_music_track_queued_on_a_deck_keeps_its_left_and_right_apart()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate, channels: 2);
        Assert.NotNull(device);
        var music = new MusicPlayer(
            NullLogger.Instance,
            null,
            _ => new FakeDecoder(2, Rate, Rate * 10, LoopPoints.None)
            {
                Wave = (frame, channel) => channel == 0 ? 0.25f * MathF.Sin(2 * MathF.PI * 440 * frame / Rate) : 0,
            }
        );
        var tone = music.Declare("tone", "tone.fake");
        music.ReadTracks();
        var streamer = new MusicStreamer(music.Tracks, music.Feeds, music.Open, NullLogger.Instance);
        using var decks = new MusicDecks(device, music.Exchange, music.Feeds, new AudioStats());
        device.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, 1);
        music.Play(tone, fadeSeconds: 0);
        music.Publish(running: true);
        decks.Pump(0, 1, enabled: true);
        streamer.Pump();
        decks.Pump(0.004, 1, enabled: true);
        var output = new float[2 * 8192];
        device.RenderLoopback(output);

        var left = 0f;
        var right = 0f;
        for (var i = 4096; i < 8192; i++)
        {
            left += MathF.Abs(output[2 * i]);
            right += MathF.Abs(output[2 * i + 1]);
        }

        Assert.InRange(left / 4096, 0.1f, 0.25f);
        Assert.True(right < left * 0.01f, $"right {right / 4096} against left {left / 4096}");
        Assert.Equal(0, device.TakeError());
    }

    /// <summary>
    /// A looping buffer with loop points (AL_SOFT_loop_points) plays the part before them once, then only its loop: a loud
    /// intro is never heard again among the quiet loop's repeats.
    /// </summary>
    [Fact]
    public void A_looping_buffer_plays_its_intro_once_then_only_its_loop()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate);
        Assert.NotNull(device);
        var buffer = device.CreateBuffer();
        var part = Rate / 10;
        var samples = new short[2 * part];
        for (var i = 0; i < samples.Length; i++)
        {
            var amplitude = i < part ? 26_000 : 6_500;
            samples[i] = (short)(amplitude * MathF.Sin(2 * MathF.PI * 400 * i / Rate));
        }

        device.FillBuffer(buffer, samples, 1, Rate);
        device.SetLoopPoints(buffer, part, 2 * part);
        var source = device.CreateSource();
        device.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY, 1);
        device.SetSource(source, Vector3.Zero, 1, 1, 1, atListener: true);
        device.PlayLooping(source, buffer, 0);
        // Half a second: the intro, then four rounds of the loop.
        var output = new float[Rate / 2];
        device.RenderLoopback(output);

        var intro = Peak(output.AsSpan(0, part));
        var loop = Peak(output.AsSpan(part + 1000));
        Assert.InRange(intro, 0.6f, 1f);
        Assert.InRange(loop, 0.1f, 0.3f);
        Assert.True(device.IsPlaying(source));
        Assert.Equal(0, device.TakeError());
        device.DeleteSource(source);
        device.DeleteBuffer(buffer);
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(sample));
        }

        return peak;
    }

    /// <summary>A device with its output reports it there (ALC_CONNECTED is read through OpenAL).</summary>
    [Fact]
    public void A_device_with_its_output_keeps_it()
    {
        using var device = OpenAlAudioDevice.OpenLoopback(Rate);
        Assert.NotNull(device);

        Assert.True(device.KeepConnected());
        Assert.Equal(0, device.TakeError());
    }

    /// <summary>The mean size of the samples a source at the listener plays at <paramref name="gain"/>, once settled.</summary>
    private static float Level(OpenAlAudioDevice device, uint source, uint buffer, float gain)
    {
        device.StopSource(source);
        device.SetSource(source, Vector3.Zero, gain, 1, 1, atListener: true);
        device.PlayBuffer(source, buffer);
        var output = new float[4096];
        device.RenderLoopback(output);
        var sum = 0f;
        for (var i = 2048; i < output.Length; i++)
        {
            sum += MathF.Abs(output[i]);
        }

        return sum / (output.Length - 2048);
    }
}
