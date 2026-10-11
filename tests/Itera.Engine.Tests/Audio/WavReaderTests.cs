using Itera.Engine.Audio.Files;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// The WAV reader over files written in memory (<see cref="WavBuilder"/>): every encoding it takes reads back its
/// samples (16-bit exactly), plain and extensible, mono and stereo; what it refuses says why; chunks it does not use are
/// skipped (an odd-sized one with its pad byte); a smpl loop is read before or after the data (its end made exclusive)
/// and dropped with a warning when it reaches past the sound; an unknown data size reads to the end; data cut short
/// reads what is there with a warning; RF64; reads in blocks and after a seek match the whole; reading allocates
/// nothing.
/// </summary>
public sealed class WavReaderTests
{
    private const int Frames = 600;

    [Theory]
    [InlineData(WavEncoding.Pcm, 8, 1, false)]
    [InlineData(WavEncoding.Pcm, 16, 1, false)]
    [InlineData(WavEncoding.Pcm, 16, 2, false)]
    [InlineData(WavEncoding.Pcm, 24, 2, false)]
    [InlineData(WavEncoding.Pcm, 32, 1, false)]
    [InlineData(WavEncoding.IeeeFloat, 32, 2, false)]
    [InlineData(WavEncoding.IeeeFloat, 64, 1, false)]
    [InlineData(WavEncoding.Pcm, 24, 2, true)]
    [InlineData(WavEncoding.IeeeFloat, 32, 2, true)]
    public void Every_encoding_reads_back_its_samples(WavEncoding encoding, int bits, int channels, bool extensible)
    {
        var samples = WavBuilder.Ramp(Frames, channels);
        var file = new WavBuilder
        {
            Encoding = encoding,
            Bits = bits,
            Channels = channels,
            Extensible = extensible,
        }.Build(samples);

        using var decoder = SoundDecoders.Open(new MemoryStream(file));
        var read = ReadAll(decoder);

        Assert.IsType<WavDecoder>(decoder);
        Assert.Equal(channels, decoder.Channels);
        Assert.Equal(22_050, decoder.SampleRate);
        Assert.Equal(Frames, decoder.Frames);
        Assert.Equal(samples.Length, read.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            var expected = encoding == WavEncoding.IeeeFloat ? samples[i] : WavBuilder.Quantised(samples[i], bits);
            Assert.Equal(expected, read[i], 1e-7f);
        }

        Assert.Null(decoder.Warning);
    }

    [Fact]
    public void A_16_bit_file_reads_back_bit_for_bit_as_16_bit_samples()
    {
        var samples = WavBuilder.Ramp(Frames, 2);
        var file = new WavBuilder { Channels = 2 }.Build(samples);

        using var decoder = SoundDecoders.Open(new MemoryStream(file));
        var sound = SoundFiles.Decode(decoder, keepStereo: true, maxSeconds: 10);

        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal((short)Math.Clamp((int)MathF.Round(samples[i] * 32768), -32768, 32767), sound.Samples[i]);
        }
    }

    [Theory]
    [InlineData("adpcm", "0x0002 is not PCM or IEEE float")]
    [InlineData("subformat", "subformat is not PCM or IEEE float")]
    [InlineData("channels", "3 channels: only mono and stereo are read")]
    [InlineData("bits", "12-bit PCM samples are not read")]
    [InlineData("float16", "16-bit float samples are not read")]
    [InlineData("not riff", "not a WAV or Ogg Vorbis file")]
    public void What_it_cannot_read_it_refuses_saying_why(string what, string why)
    {
        var samples = WavBuilder.Ramp(100, 1);
        var file = what switch
        {
            "adpcm" => new WavBuilder { FormatTag = 2 }.Build(samples),
            "subformat" => new WavBuilder { Extensible = true, SubFormat = 2 }.Build(samples),
            "channels" => new WavBuilder { Channels = 3 }.Build(WavBuilder.Ramp(100, 3)),
            "bits" => new WavBuilder { Bits = 12 }.Build([]),
            "float16" => new WavBuilder { Encoding = WavEncoding.IeeeFloat, Bits = 16 }.Build([]),
            _ => "JUNKJUNKJUNKJUNK"u8.ToArray(),
        };

        var error = Assert.Throws<SoundFileException>(() => SoundDecoders.Open(new MemoryStream(file)));

        Assert.Contains(why, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_data_chunk_is_refused()
    {
        var file = new WavBuilder().Build(WavBuilder.Ramp(10, 1));
        // The data chunk's id renamed: the reader skips it as a chunk it does not use.
        var at = IndexOf(file, "data"u8);
        "dada"u8.CopyTo(file.AsSpan(at));

        var error = Assert.Throws<SoundFileException>(() => SoundDecoders.Open(new MemoryStream(file)));

        Assert.Contains("no data chunk", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_odd_sized_chunk_before_the_data_is_skipped_with_its_pad_byte()
    {
        var samples = WavBuilder.Ramp(Frames, 1);
        var file = new WavBuilder { OddChunkBeforeData = true }.Build(samples);

        using var decoder = SoundDecoders.Open(new MemoryStream(file));

        Assert.Equal(WavBuilder.Quantised(samples[0], 16), ReadAll(decoder)[0]);
        Assert.Equal(Frames, decoder.Frames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_smpl_loop_is_read_with_its_end_made_exclusive(bool beforeData)
    {
        var file = new WavBuilder { Loop = (100, 499), LoopBeforeData = beforeData }.Build(WavBuilder.Ramp(Frames, 1));

        using var decoder = SoundDecoders.Open(new MemoryStream(file));

        Assert.Equal(new LoopPoints(100, 500), decoder.LoopPoints);
        Assert.Null(decoder.Warning);
    }

    [Fact]
    public void A_loop_reaching_past_the_sound_is_dropped_with_a_warning()
    {
        var file = new WavBuilder { Loop = (100, 5000) }.Build(WavBuilder.Ramp(Frames, 1));

        using var decoder = SoundDecoders.Open(new MemoryStream(file));

        Assert.False(decoder.LoopPoints.IsSet);
        Assert.Contains("reach past the sound", decoder.Warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xFFFF_FFFFu)]
    [InlineData(0u)]
    public void An_unknown_data_size_reads_to_the_end_of_the_file(uint size)
    {
        var file = new WavBuilder { DataSize = size }.Build(WavBuilder.Ramp(Frames, 1));

        using var decoder = SoundDecoders.Open(new MemoryStream(file));

        Assert.Equal(Frames, decoder.Frames);
        Assert.Equal(Frames, ReadAll(decoder).Length);
    }

    [Fact]
    public void Data_cut_short_reads_what_is_there_with_a_warning()
    {
        var file = new WavBuilder { CutBytes = 201 }.Build(WavBuilder.Ramp(Frames, 1));

        using var decoder = SoundDecoders.Open(new MemoryStream(file));

        Assert.Equal(Frames - 101, decoder.Frames);
        Assert.Equal(Frames - 101, ReadAll(decoder).Length);
        Assert.Contains("only", decoder.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_rf64_file_reads_its_data_size_from_its_ds64_chunk()
    {
        var samples = WavBuilder.Ramp(Frames, 2);
        var file = new WavBuilder
        {
            Rf64 = true,
            Channels = 2,
            Loop = (10, 20),
        }.Build(samples);

        using var decoder = SoundDecoders.Open(new MemoryStream(file));

        Assert.Equal(Frames, decoder.Frames);
        Assert.Equal(new LoopPoints(10, 21), decoder.LoopPoints);
        Assert.Equal(WavBuilder.Quantised(samples[^1], 16), ReadAll(decoder)[^1]);
    }

    [Fact]
    public void Reads_in_blocks_and_after_a_seek_match_the_whole()
    {
        var file = new WavBuilder { Channels = 2, Bits = 24 }.Build(WavBuilder.Ramp(Frames, 2));
        using var whole = SoundDecoders.Open(new MemoryStream(file));
        var all = ReadAll(whole);

        using var blocks = SoundDecoders.Open(new MemoryStream(file));
        var pieces = new List<float>();
        var block = new float[2 * 7];
        int read;
        while ((read = blocks.Read(block)) > 0)
        {
            pieces.AddRange(block.AsSpan(0, read * 2).ToArray());
        }

        Assert.Equal(all, pieces);

        blocks.Seek(250);
        var after = new float[2 * 50];
        Assert.Equal(50, blocks.Read(after));
        Assert.Equal(all.AsSpan(500, 100).ToArray(), after);
    }

    [Fact]
    public void Reading_allocates_nothing()
    {
        var file = new WavBuilder { Channels = 2, Bits = 24 }.Build(WavBuilder.Ramp(20_000, 2));
        using var decoder = SoundDecoders.Open(new MemoryStream(file));
        var block = new float[2 * 512];
        decoder.Read(block);

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 30; i++)
        {
            decoder.Read(block);
        }

        decoder.Seek(0);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
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

    private static int IndexOf(byte[] bytes, ReadOnlySpan<byte> pattern) => bytes.AsSpan().IndexOf(pattern);
}
