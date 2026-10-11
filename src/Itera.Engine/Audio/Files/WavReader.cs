using System.Buffers.Binary;
using System.Globalization;

namespace Itera.Engine.Audio.Files;

/// <summary>How a WAV file's samples are stored.</summary>
public enum WavEncoding
{
    /// <summary>Integers: 8-bit unsigned, or 16, 24, or 32-bit signed, little-endian.</summary>
    Pcm,

    /// <summary>IEEE floats of 32 or 64 bits.</summary>
    IeeeFloat,
}

/// <summary>
/// What a WAV file's header says: how its samples are stored, its channels and rate, where its data lies, and the loop
/// its <c>smpl</c> chunk names.
/// </summary>
/// <param name="Encoding">Integers or floats.</param>
/// <param name="BitsPerSample">Bits a sample takes in the file (the container's, for extensible files).</param>
/// <param name="Channels">1 or 2.</param>
/// <param name="SampleRate">Frames a second.</param>
/// <param name="BlockAlign">Bytes a frame takes.</param>
/// <param name="DataOffset">Where the samples start in the file.</param>
/// <param name="DataBytes">The bytes of samples there are.</param>
/// <param name="LoopPoints">The first forward loop of the <c>smpl</c> chunk, or none.</param>
/// <param name="Warning">What was wrong but did not stop the file being read; null for nothing.</param>
public sealed record WavFormat(
    WavEncoding Encoding,
    int BitsPerSample,
    int Channels,
    int SampleRate,
    int BlockAlign,
    long DataOffset,
    long DataBytes,
    LoopPoints LoopPoints,
    string? Warning
)
{
    /// <summary>The frames of samples there are.</summary>
    public long Frames => DataBytes / BlockAlign;
}

/// <summary>
/// Reads a WAV file's header (RIFF, or RF64 for files past 4 GB): walks its chunks, skipping those it does not use
/// (<c>LIST</c>, <c>cue </c>, <c>bext</c>, <c>fact</c>, <c>JUNK</c>, and any other) and the pad byte after an odd-sized
/// one, reads <c>fmt </c> (PCM of 8, 16, 24, or 32 bits; IEEE float of 32 or 64; either as
/// <c>WAVE_FORMAT_EXTENSIBLE</c>), finds <c>data</c> (a size of 0 or 0xFFFFFFFF, as streaming writers leave it, means
/// to the file's end; data cut short is read as far as it goes, with a warning), and reads the first forward loop of a
/// <c>smpl</c> chunk, also one after the data. Mono and stereo only.
/// </summary>
public static class WavReader
{
    private const ushort FormatPcm = 0x0001;
    private const ushort FormatFloat = 0x0003;
    private const ushort FormatExtensible = 0xFFFE;
    private const uint UnknownSize = 0xFFFF_FFFF;
    private const int SmplHeaderBytes = 36;
    private const int SmplLoopBytes = 24;
    private const int MostSmplLoops = 32;

    /// <summary>
    /// The 14 bytes every <c>KSDATAFORMAT_SUBTYPE</c> GUID ends with after its first two, which hold the format code
    /// ({0000XXXX-0000-0010-8000-00AA00389B71}, as stored little-endian).
    /// </summary>
    private static readonly byte[] s_subFormatTail =
    [
        0x00,
        0x00,
        0x00,
        0x00,
        0x10,
        0x00,
        0x80,
        0x00,
        0x00,
        0xAA,
        0x00,
        0x38,
        0x9B,
        0x71,
    ];

    /// <summary>
    /// Reads the header of the WAV file in <paramref name="stream"/> (seekable, at the file's start) and leaves the
    /// stream somewhere past it. Throws <see cref="SoundFileException"/> saying why when it cannot be read.
    /// </summary>
    public static WavFormat ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new SoundFileException("the stream cannot seek");
        }

        var length = stream.Length;
        Span<byte> head = stackalloc byte[12];
        if (!TryRead(stream, head))
        {
            throw new SoundFileException("too short for a WAV file");
        }

        var rf64 = head[..4].SequenceEqual("RF64"u8);
        if ((!rf64 && !head[..4].SequenceEqual("RIFF"u8)) || !head[8..12].SequenceEqual("WAVE"u8))
        {
            throw new SoundFileException("not a WAV file (no RIFF and WAVE header)");
        }

        var ds64DataBytes = -1L;
        Fmt? fmt = null;
        var dataOffset = -1L;
        var dataBytes = 0L;
        var loop = LoopPoints.None;
        var warnings = new List<string>();
        Span<byte> chunkHead = stackalloc byte[8];
        var position = 12L;
        while (position + 8 <= length)
        {
            stream.Position = position;
            if (!TryRead(stream, chunkHead))
            {
                break;
            }

            var id = chunkHead[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHead[4..]);
            var body = position + 8;
            var available = length - body;
            if (id.SequenceEqual("ds64"u8) && rf64)
            {
                ds64DataBytes = ReadDs64(stream, size);
            }
            else if (id.SequenceEqual("fmt "u8))
            {
                fmt = ReadFmt(stream, size);
            }
            else if (id.SequenceEqual("data"u8))
            {
                var declared =
                    rf64 && size == UnknownSize && ds64DataBytes >= 0 ? ds64DataBytes
                    : size is UnknownSize or 0 ? available
                    : size;
                if (declared > available)
                {
                    warnings.Add(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"the data chunk says {declared} bytes but only {available} are there: those are read"
                        )
                    );
                }

                dataOffset = body;
                dataBytes = Math.Min(declared, available);
                position = body + declared + (declared & 1);
                continue;
            }
            else if (id.SequenceEqual("smpl"u8))
            {
                loop = ReadSmpl(stream, size);
            }

            if (size == UnknownSize)
            {
                break;
            }

            position = body + size + (size & 1);
        }

        if (fmt is not { } format)
        {
            throw new SoundFileException("no fmt chunk");
        }

        if (dataOffset < 0)
        {
            throw new SoundFileException("no data chunk");
        }

        var blockAlign = format.Channels * format.Bits / 8;
        var frames = dataBytes / blockAlign;
        loop = loop.Within(frames, out var problem);
        if (problem is not null)
        {
            warnings.Add(problem);
        }

        return new WavFormat(
            format.Encoding,
            format.Bits,
            format.Channels,
            format.SampleRate,
            blockAlign,
            dataOffset,
            frames * blockAlign,
            loop,
            warnings.Count == 0 ? null : string.Join("; ", warnings)
        );
    }

    private readonly record struct Fmt(WavEncoding Encoding, int Bits, int Channels, int SampleRate);

    private static Fmt ReadFmt(Stream stream, uint size)
    {
        if (size < 16)
        {
            throw new SoundFileException($"the fmt chunk is {size} bytes, too short");
        }

        Span<byte> data = stackalloc byte[40];
        var read = (int)Math.Min(size, 40);
        if (!TryRead(stream, data[..read]))
        {
            throw new SoundFileException("the fmt chunk is cut short");
        }

        var tag = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        if (tag == FormatExtensible)
        {
            if (read < 40)
            {
                throw new SoundFileException("an extensible fmt chunk shorter than 40 bytes");
            }

            var subFormat = data[24..40];
            tag = BinaryPrimitives.ReadUInt16LittleEndian(subFormat);
            if (!subFormat[2..].SequenceEqual(s_subFormatTail) || tag is not (FormatPcm or FormatFloat))
            {
                throw new SoundFileException("an extensible format whose subformat is not PCM or IEEE float");
            }
        }

        var encoding = tag switch
        {
            FormatPcm => WavEncoding.Pcm,
            FormatFloat => WavEncoding.IeeeFloat,
            _ => throw new SoundFileException(
                string.Create(CultureInfo.InvariantCulture, $"format 0x{tag:X4} is not PCM or IEEE float")
            ),
        };
        if (channels is not (1 or 2))
        {
            throw new SoundFileException($"{channels} channels: only mono and stereo are read");
        }

        var bitsOk = encoding == WavEncoding.Pcm ? bits is 8 or 16 or 24 or 32 : bits is 32 or 64;
        if (!bitsOk)
        {
            throw new SoundFileException(
                $"{bits}-bit {(encoding == WavEncoding.Pcm ? "PCM" : "float")} samples are not read"
            );
        }

        if (blockAlign != channels * bits / 8)
        {
            throw new SoundFileException(
                $"a frame of {blockAlign} bytes, where {channels} channels of {bits} bits take {channels * bits / 8}"
            );
        }

        if (rate is < 1 or > 768_000)
        {
            throw new SoundFileException($"a rate of {rate} frames a second");
        }

        return new Fmt(encoding, bits, channels, rate);
    }

    /// <summary>The data size an RF64 file's ds64 chunk gives (its 64-bit sizes come first: the RIFF's, then the data's).</summary>
    private static long ReadDs64(Stream stream, uint size)
    {
        Span<byte> data = stackalloc byte[16];
        if (size < 16 || !TryRead(stream, data))
        {
            throw new SoundFileException("an RF64 file whose ds64 chunk is cut short");
        }

        return BinaryPrimitives.ReadInt64LittleEndian(data[8..]);
    }

    /// <summary>The first forward loop of a smpl chunk (its end inclusive in the file, so one is added), or none.</summary>
    private static LoopPoints ReadSmpl(Stream stream, uint size)
    {
        if (size < SmplHeaderBytes)
        {
            return LoopPoints.None;
        }

        Span<byte> header = stackalloc byte[SmplHeaderBytes];
        if (!TryRead(stream, header))
        {
            return LoopPoints.None;
        }

        var loops = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(header[28..]), MostSmplLoops);
        loops = (int)Math.Min(loops, (size - SmplHeaderBytes) / SmplLoopBytes);
        Span<byte> record = stackalloc byte[SmplLoopBytes];
        for (var i = 0; i < loops; i++)
        {
            if (!TryRead(stream, record))
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            if (type != 0)
            {
                continue;
            }

            var start = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            var end = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            return new LoopPoints(start, (long)end + 1);
        }

        return LoopPoints.None;
    }

    private static bool TryRead(Stream stream, Span<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = stream.Read(buffer[done..]);
            if (read == 0)
            {
                return false;
            }

            done += read;
        }

        return true;
    }
}
