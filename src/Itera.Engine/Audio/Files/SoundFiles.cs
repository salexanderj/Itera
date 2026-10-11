namespace Itera.Engine.Audio.Files;

/// <summary>
/// A sound file read whole into memory as 16-bit samples, as OpenAL's buffers take them: mono, or stereo interleaved,
/// at the file's own rate (OpenAL resamples it), with the file's loop points.
/// </summary>
/// <param name="Samples">The samples, the channels interleaved.</param>
/// <param name="Channels">1 or 2.</param>
/// <param name="SampleRate">Frames a second.</param>
/// <param name="Frames">The frames held.</param>
/// <param name="LoopPoints">The loop the file names, or none.</param>
/// <param name="Warning">What was wrong but did not stop the file being read; null for nothing.</param>
public sealed record DecodedSound(
    short[] Samples,
    int Channels,
    int SampleRate,
    long Frames,
    LoopPoints LoopPoints,
    string? Warning
)
{
    /// <summary>Its length in seconds.</summary>
    public double Seconds => Frames / (double)SampleRate;

    /// <summary>The memory its samples take.</summary>
    public long Bytes => Samples.LongLength * sizeof(short);
}

/// <summary>Reads sound files whole (for short sounds and loops; music streams through <see cref="ISoundDecoder"/>).</summary>
public static class SoundFiles
{
    private const int BlockFrames = 4096;

    /// <summary>
    /// Reads the file at <paramref name="path"/> whole: its two channels kept when <paramref name="keepStereo"/>, else
    /// mixed to mono, (left + right) / 2. Throws <see cref="SoundFileException"/> saying why when it cannot be read or
    /// lasts more than <paramref name="maxSeconds"/>.
    /// </summary>
    public static DecodedSound Decode(string path, bool keepStereo, float maxSeconds)
    {
        using var decoder = SoundDecoders.Open(path);
        return Decode(decoder, keepStereo, maxSeconds);
    }

    /// <summary>Reads <paramref name="decoder"/> from where it is to its end, as <see cref="Decode(string, bool, float)"/> does.</summary>
    public static DecodedSound Decode(ISoundDecoder decoder, bool keepStereo, float maxSeconds)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        var channels = decoder.Channels;
        var rate = decoder.SampleRate;
        var most = (long)Math.Ceiling(maxSeconds * (double)rate);
        if (decoder.Frames > most)
        {
            throw TooLong(decoder.Frames / (double)rate, maxSeconds);
        }

        var outChannels = keepStereo ? channels : 1;
        var block = new float[BlockFrames * channels];
        var samples = new short[(int)Math.Max(BlockFrames, decoder.Frames) * outChannels];
        var frames = 0L;
        while (true)
        {
            var read = decoder.Read(block);
            if (read == 0)
            {
                break;
            }

            if (frames + read > most)
            {
                throw TooLong((frames + read) / (double)rate, maxSeconds);
            }

            var needed = (frames + read) * outChannels;
            if (needed > samples.Length)
            {
                Array.Resize(ref samples, (int)Math.Max(needed, samples.LongLength * 2));
            }

            var destination = samples.AsSpan((int)(frames * outChannels), read * outChannels);
            if (channels == 2 && !keepStereo)
            {
                for (var i = 0; i < read; i++)
                {
                    destination[i] = ToInt16((block[2 * i] + block[2 * i + 1]) * 0.5f);
                }
            }
            else
            {
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = ToInt16(block[i]);
                }
            }

            frames += read;
        }

        if (samples.LongLength != frames * outChannels)
        {
            Array.Resize(ref samples, (int)(frames * outChannels));
        }

        var loop = decoder.LoopPoints.Within(frames, out var problem);
        var warning =
            decoder.Warning is null ? problem
            : problem is null ? decoder.Warning
            : decoder.Warning + "; " + problem;
        return new DecodedSound(samples, outChannels, rate, frames, loop, warning);
    }

    /// <summary>A sample within ±1 as 16 bits: times 32,768, rounded and kept in range, so a 16-bit file reads back exactly.</summary>
    public static short ToInt16(float sample) => (short)Math.Clamp((int)MathF.Round(sample * 32768f), -32768, 32767);

    private static SoundFileException TooLong(double seconds, float maxSeconds) =>
        new($"{seconds:0.#} s long, where at most {maxSeconds:0.#} s is read whole: stream it as music");
}
