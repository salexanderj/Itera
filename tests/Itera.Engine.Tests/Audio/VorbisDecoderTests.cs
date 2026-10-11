using Itera.Engine.Audio.Files;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// The Ogg Vorbis decoder (NVorbis) over the fixtures (<c>Audio/Fixtures</c>, made by <c>tools/sounds/make_sounds.py</c>):
/// their channels, rate, length, and pitch (by zero crossings), their loop tags (LOOPSTART with LOOPLENGTH or LOOPEND),
/// a read after a seek against a straight decode, a file chosen by its first bytes and not its name, and broken files
/// refused or read as far as they go.
/// </summary>
public sealed class VorbisDecoderTests
{
    private const int Rate = 22_050;

    [Fact]
    public void A_mono_tone_reads_with_its_channels_rate_length_and_pitch()
    {
        using var decoder = SoundDecoders.Open(Fixture("tone-mono.ogg"));
        var samples = ReadAll(decoder);

        Assert.IsType<VorbisDecoder>(decoder);
        Assert.Equal(1, decoder.Channels);
        Assert.Equal(Rate, decoder.SampleRate);
        Assert.Equal(Rate / 2, decoder.Frames);
        Assert.Equal(Rate / 2, samples.Length);
        Assert.InRange(Pitch(samples, 1, 0), 435, 445);
        Assert.InRange(samples.Max(), 0.45f, 0.55f);
        Assert.False(decoder.LoopPoints.IsSet);
    }

    [Fact]
    public void A_stereo_tone_keeps_its_channels_apart_and_reads_its_loop_length_tag()
    {
        using var decoder = SoundDecoders.Open(Fixture("tone-stereo-loop.ogg"));
        var samples = ReadAll(decoder);

        Assert.Equal(2, decoder.Channels);
        Assert.InRange(Pitch(samples, 2, 0), 435, 445);
        Assert.InRange(Pitch(samples, 2, 1), 655, 665);
        Assert.Equal(new LoopPoints(2205, 2205 + 8820), decoder.LoopPoints);
        Assert.Null(decoder.Warning);
    }

    [Fact]
    public void A_loop_end_tag_is_the_frame_after_the_loop()
    {
        using var decoder = SoundDecoders.Open(Fixture("tone-loopend.ogg"));

        Assert.Equal(new LoopPoints(1000, 9000), decoder.LoopPoints);
        Assert.InRange(Pitch(ReadAll(decoder), 1, 0), 325, 335);
    }

    /// <summary>Near a file's start, where NVorbis's own seek lands on wrong samples; also back after reading on.</summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10_000)]
    public void A_read_after_a_seek_matches_a_straight_decode(int frame)
    {
        using var straight = SoundDecoders.Open(Fixture("tone-stereo-loop.ogg"));
        var all = ReadAll(straight);

        using var decoder = SoundDecoders.Open(Fixture("tone-stereo-loop.ogg"));
        var block = new float[2 * 1000];
        decoder.Read(block);
        decoder.Seek(frame);
        var read = decoder.Read(block);

        Assert.Equal(Math.Min(1000, 11_025 - frame), read);
        for (var i = 0; i < read * 2; i++)
        {
            Assert.Equal(all[2 * frame + i], block[i], 1e-6f);
        }
    }

    [Fact]
    public void A_file_is_opened_by_what_it_is_not_by_its_name()
    {
        var path = Path.Combine(Path.GetTempPath(), $"itera-{Guid.NewGuid():N}.wav");
        File.Copy(Fixture("tone-mono.ogg"), path);
        try
        {
            using var decoder = SoundDecoders.Open(path);

            Assert.IsType<VorbisDecoder>(decoder);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_ogg_file_that_is_not_vorbis_is_refused_saying_why()
    {
        var bytes = File.ReadAllBytes(Fixture("tone-mono.ogg"));
        // Past the first page's header: the Vorbis identification packet broken.
        for (var i = 28; i < 60; i++)
        {
            bytes[i] = 0x55;
        }

        var error = Assert.Throws<SoundFileException>(() => SoundDecoders.Open(new MemoryStream(bytes)));

        Assert.Contains("Ogg Vorbis", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_cut_short_reads_as_far_as_it_goes_or_says_why_not()
    {
        var bytes = File.ReadAllBytes(Fixture("tone-mono.ogg"));
        var cut = bytes[..(bytes.Length * 2 / 3)];

        try
        {
            using var decoder = SoundDecoders.Open(new MemoryStream(cut));
            var sound = SoundFiles.Decode(decoder, keepStereo: false, maxSeconds: 10);

            Assert.InRange(sound.Frames, 1, Rate / 2 - 1);
        }
        catch (SoundFileException error)
        {
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        }
    }

    /// <summary>The path of the fixture <paramref name="name"/>, copied beside the tests.</summary>
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Audio", "Fixtures", name);

    /// <summary>A channel's frequency from its upward zero crossings over its middle (the fades at its ends left out).</summary>
    private static double Pitch(float[] samples, int channels, int channel)
    {
        var frames = samples.Length / channels;
        var from = frames / 10;
        var to = frames - frames / 10;
        var first = -1;
        var last = -1;
        var crossings = 0;
        for (var i = from + 1; i < to; i++)
        {
            if (samples[(i - 1) * channels + channel] < 0 && samples[i * channels + channel] >= 0)
            {
                first = first < 0 ? i : first;
                last = i;
                crossings++;
            }
        }

        return (crossings - 1) / ((last - first) / (double)Rate);
    }

    private static float[] ReadAll(ISoundDecoder decoder)
    {
        var all = new List<float>();
        var block = new float[1024 * decoder.Channels];
        int read;
        while ((read = decoder.Read(block)) > 0)
        {
            all.AddRange(block.AsSpan(0, read * decoder.Channels).ToArray());
        }

        return [.. all];
    }
}
