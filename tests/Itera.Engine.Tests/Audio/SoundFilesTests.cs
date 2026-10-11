using Itera.Engine.Audio.Files;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// Sound files read whole as 16-bit samples (<see cref="SoundFiles"/>): a stereo file mixed to mono as the mean of its
/// channels, or kept stereo; its loop points carried over; one longer than the most allowed refused with "stream it as
/// music"; a missing file refused; an Ogg fixture read at its level; float samples past ±1 kept in range.
/// </summary>
public sealed class SoundFilesTests
{
    [Fact]
    public void A_stereo_file_is_mixed_to_mono_as_the_mean_of_its_channels_or_kept_stereo()
    {
        var samples = WavBuilder.Ramp(300, 2);
        var file = new WavBuilder
        {
            Channels = 2,
            Encoding = WavEncoding.IeeeFloat,
            Bits = 32,
        }.Build(samples);

        var mono = SoundFiles.Decode(SoundDecoders.Open(new MemoryStream(file)), keepStereo: false, maxSeconds: 10);
        var stereo = SoundFiles.Decode(SoundDecoders.Open(new MemoryStream(file)), keepStereo: true, maxSeconds: 10);

        Assert.Equal(1, mono.Channels);
        Assert.Equal(300, mono.Frames);
        Assert.Equal(2, stereo.Channels);
        Assert.Equal(600, stereo.Samples.Length);
        for (var i = 0; i < 300; i++)
        {
            Assert.Equal(SoundFiles.ToInt16((samples[2 * i] + samples[2 * i + 1]) * 0.5f), mono.Samples[i]);
            Assert.Equal(SoundFiles.ToInt16(samples[2 * i + 1]), stereo.Samples[2 * i + 1]);
        }
    }

    [Fact]
    public void Its_loop_points_rate_and_length_are_carried_over()
    {
        var file = new WavBuilder { Rate = 44_100, Loop = (50, 249) }.Build(WavBuilder.Ramp(400, 1));

        var sound = SoundFiles.Decode(SoundDecoders.Open(new MemoryStream(file)), keepStereo: false, maxSeconds: 10);

        Assert.Equal(new LoopPoints(50, 250), sound.LoopPoints);
        Assert.Equal(44_100, sound.SampleRate);
        Assert.Equal(400 / 44_100.0, sound.Seconds, 9);
        Assert.Equal(800, sound.Bytes);
    }

    [Fact]
    public void A_file_longer_than_the_most_allowed_is_refused()
    {
        var file = new WavBuilder { Rate = 1000 }.Build(WavBuilder.Ramp(2500, 1));

        var error = Assert.Throws<SoundFileException>(() =>
            SoundFiles.Decode(SoundDecoders.Open(new MemoryStream(file)), keepStereo: false, maxSeconds: 2)
        );

        Assert.Contains("stream it as music", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_refused_saying_so()
    {
        var error = Assert.Throws<SoundFileException>(() =>
            SoundFiles.Decode(
                Path.Combine(AppContext.BaseDirectory, "no-such-sound.wav"),
                keepStereo: false,
                maxSeconds: 1
            )
        );

        Assert.Equal("no such file", error.Message);
    }

    [Fact]
    public void An_ogg_file_reads_whole_at_its_level()
    {
        var sound = SoundFiles.Decode(
            VorbisDecoderTests.Fixture("tone-stereo-loop.ogg"),
            keepStereo: true,
            maxSeconds: 1
        );

        Assert.Equal(2, sound.Channels);
        Assert.Equal(11_025, sound.Frames);
        Assert.InRange(sound.Samples.Max(sample => (int)sample), 14_000, 18_000);
        Assert.Equal(new LoopPoints(2205, 11_025), sound.LoopPoints);
    }

    [Fact]
    public void Samples_past_full_scale_are_kept_in_range()
    {
        Assert.Equal(32767, SoundFiles.ToInt16(1f));
        Assert.Equal(32767, SoundFiles.ToInt16(1.5f));
        Assert.Equal(-32768, SoundFiles.ToInt16(-1f));
        Assert.Equal(-32768, SoundFiles.ToInt16(-3f));
        Assert.Equal(16384, SoundFiles.ToInt16(0.5f));
    }
}
