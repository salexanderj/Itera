using System.Globalization;
using NVorbis;

namespace Itera.Engine.Audio.Files;

/// <summary>
/// An Ogg Vorbis file read as floats through NVorbis (<c>VorbisReader</c>, its samples clipped within ±1). Its loop is
/// named by the tags <c>LOOPSTART</c> with <c>LOOPLENGTH</c> (as RPG Maker writes them) or <c>LOOPEND</c> (the frame
/// after the loop). NVorbis allocates as it decodes (about 53 KB a second of 48 kHz stereo), so a decoder runs on a
/// thread of its own or at the run's start, never on the audio thread; one thread at a time. <see cref="Seek"/> goes back
/// to the start and decodes up to the frame: NVorbis 1.0.0-rc.2's own seek lands on wrong samples within about the
/// first 0.6 to 2 s of a file (measured on files from libvorbis and libsndfile; exact further in, and from the start),
/// and decoding runs at about 350 times real time here, so a seek 30 s in costs about 85 ms.
/// </summary>
public sealed class VorbisDecoder : ISoundDecoder
{
    private readonly VorbisReader _reader;
    private float[]? _skip;
    private bool _disposed;

    /// <param name="stream">The file, at its start.</param>
    /// <param name="closeStream">Dispose <paramref name="stream"/> with the decoder.</param>
    public VorbisDecoder(Stream stream, bool closeStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            _reader = new VorbisReader(stream, closeStream);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (closeStream)
            {
                stream.Dispose();
            }

            throw new SoundFileException($"not a readable Ogg Vorbis file ({exception.Message})", exception);
        }

        if (_reader.Channels is not (1 or 2))
        {
            var channels = _reader.Channels;
            _reader.Dispose();
            throw new SoundFileException($"{channels} channels: only mono and stereo are read");
        }

        _reader.ClipSamples = true;
        Frames = _reader.TotalSamples;
        LoopPoints = ReadLoop(out var warning).Within(Frames, out var problem);
        Warning = warning ?? problem;
    }

    public int Channels => _reader.Channels;

    public int SampleRate => _reader.SampleRate;

    public long Frames { get; }

    public LoopPoints LoopPoints { get; }

    public string? Warning { get; }

    public int Read(Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channels = _reader.Channels;
        var samples = output.Length / channels * channels;
        try
        {
            return _reader.ReadSamples(output[..samples]) / channels;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new SoundFileException($"the Ogg Vorbis data cannot be decoded ({exception.Message})", exception);
        }
    }

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = Math.Clamp(frame, 0, Math.Max(0, Frames));
        try
        {
            _reader.SeekTo(0, SeekOrigin.Begin);
            var channels = _reader.Channels;
            _skip ??= new float[4096 * channels];
            var left = target;
            while (left > 0)
            {
                var read = _reader.ReadSamples(_skip.AsSpan(0, (int)Math.Min(left, 4096) * channels)) / channels;
                if (read == 0)
                {
                    break;
                }

                left -= read;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new SoundFileException($"cannot seek to frame {frame} ({exception.Message})", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reader.Dispose();
    }

    /// <summary>The loop the tags name, or none; a tag that is not a frame number is reported.</summary>
    private LoopPoints ReadLoop(out string? warning)
    {
        warning = null;
        var tags = _reader.Tags;
        var startText = tags.GetTagSingle("LOOPSTART");
        if (string.IsNullOrWhiteSpace(startText))
        {
            return LoopPoints.None;
        }

        var lengthText = tags.GetTagSingle("LOOPLENGTH");
        var endText = tags.GetTagSingle("LOOPEND");
        if (!long.TryParse(startText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start))
        {
            warning = $"LOOPSTART '{startText}' is not a frame number: no loop";
            return LoopPoints.None;
        }

        if (!string.IsNullOrWhiteSpace(lengthText))
        {
            if (long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            {
                return new LoopPoints(start, start + length);
            }

            warning = $"LOOPLENGTH '{lengthText}' is not a frame count: no loop";
            return LoopPoints.None;
        }

        if (long.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end))
        {
            return new LoopPoints(start, end);
        }

        // A start alone loops from it to the end.
        return new LoopPoints(start, Frames);
    }
}
