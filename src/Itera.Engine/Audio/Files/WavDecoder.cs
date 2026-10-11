using System.Buffers.Binary;

namespace Itera.Engine.Audio.Files;

/// <summary>
/// A WAV file read as floats (<see cref="WavReader"/> reads its header): 8-bit samples centred on 128, 16, 24, and
/// 32-bit integers scaled by their range (a 16-bit sample s reads as s / 32768, exactly), floats as they are (64-bit
/// narrowed to 32). It reads through one scratch buffer made when it opens, so <see cref="Read"/> allocates nothing.
/// </summary>
public sealed class WavDecoder : ISoundDecoder
{
    private const int ScratchBytes = 16 * 1024;

    private readonly Stream _stream;
    private readonly bool _closeStream;
    private readonly WavFormat _format;
    private readonly byte[] _scratch;
    private long _frame;
    private bool _disposed;

    /// <param name="stream">The file, seekable, at its start.</param>
    /// <param name="closeStream">Dispose <paramref name="stream"/> with the decoder.</param>
    public WavDecoder(Stream stream, bool closeStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _closeStream = closeStream;
        _format = WavReader.ReadHeader(stream);
        _scratch = new byte[ScratchBytes - ScratchBytes % _format.BlockAlign];
        Seek(0);
    }

    /// <summary>The header read.</summary>
    public WavFormat Format => _format;

    public int Channels => _format.Channels;

    public int SampleRate => _format.SampleRate;

    public long Frames => _format.Frames;

    public LoopPoints LoopPoints => _format.LoopPoints;

    public string? Warning => _format.Warning;

    public int Read(Span<float> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channels = _format.Channels;
        var blockAlign = _format.BlockAlign;
        var frames = (int)Math.Min(output.Length / channels, _format.Frames - _frame);
        var done = 0;
        while (done < frames)
        {
            var want = Math.Min(frames - done, _scratch.Length / blockAlign);
            var bytes = ReadFully(_scratch.AsSpan(0, want * blockAlign));
            var got = bytes / blockAlign;
            if (got == 0)
            {
                break;
            }

            Convert(_scratch.AsSpan(0, got * blockAlign), output.Slice(done * channels, got * channels));
            done += got;
            if (got < want)
            {
                break;
            }
        }

        _frame += done;
        return done;
    }

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frame = Math.Clamp(frame, 0, _format.Frames);
        _stream.Position = _format.DataOffset + _frame * _format.BlockAlign;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_closeStream)
        {
            _stream.Dispose();
        }
    }

    private int ReadFully(Span<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = _stream.Read(buffer[done..]);
            if (read == 0)
            {
                break;
            }

            done += read;
        }

        return done;
    }

    /// <summary>The samples in <paramref name="source"/> as floats in <paramref name="destination"/>, by the file's encoding.</summary>
    private void Convert(ReadOnlySpan<byte> source, Span<float> destination)
    {
        switch (_format.Encoding, _format.BitsPerSample)
        {
            case (WavEncoding.Pcm, 8):
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (source[i] - 128) / 128f;
                }

                break;
            case (WavEncoding.Pcm, 16):
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = BinaryPrimitives.ReadInt16LittleEndian(source[(i * 2)..]) / 32768f;
                }

                break;
            case (WavEncoding.Pcm, 24):
                for (var i = 0; i < destination.Length; i++)
                {
                    var at = i * 3;
                    var value = source[at] | (source[at + 1] << 8) | (source[at + 2] << 16);
                    destination[i] = ((value << 8) >> 8) / 8_388_608f;
                }

                break;
            case (WavEncoding.Pcm, _):
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = BinaryPrimitives.ReadInt32LittleEndian(source[(i * 4)..]) / 2_147_483_648f;
                }

                break;
            case (WavEncoding.IeeeFloat, 32):
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = BinaryPrimitives.ReadSingleLittleEndian(source[(i * 4)..]);
                }

                break;
            default:
                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(source[(i * 8)..]);
                }

                break;
        }
    }
}
