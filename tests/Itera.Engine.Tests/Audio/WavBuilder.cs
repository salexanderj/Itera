using System.Buffers.Binary;
using Itera.Engine.Audio.Files;

namespace Itera.Engine.Tests.Audio;

/// <summary>
/// WAV files written in memory for the readers' tests: any encoding and size the reader takes (or should refuse), as a
/// plain or extensible format, RIFF or RF64, with a <c>smpl</c> loop before or after the data, an odd-sized chunk before
/// it, a data size left unknown, or the file cut short.
/// </summary>
internal sealed record WavBuilder
{
    public WavEncoding Encoding { get; init; } = WavEncoding.Pcm;

    public int Bits { get; init; } = 16;

    public int Channels { get; init; } = 1;

    public int Rate { get; init; } = 22_050;

    /// <summary>The format tag written in place of the encoding's (to test one the reader refuses).</summary>
    public ushort? FormatTag { get; init; }

    /// <summary>Written as <c>WAVE_FORMAT_EXTENSIBLE</c>, its subformat the encoding's (or <see cref="SubFormat"/>).</summary>
    public bool Extensible { get; init; }

    /// <summary>The extensible subformat's code in place of the encoding's.</summary>
    public ushort? SubFormat { get; init; }

    public bool Rf64 { get; init; }

    /// <summary>The size written in the data chunk's header in place of the true one (0xFFFFFFFF or 0: unknown).</summary>
    public uint? DataSize { get; init; }

    /// <summary>Bytes cut off the file's end.</summary>
    public int CutBytes { get; init; }

    /// <summary>A smpl chunk's forward loop, its end inclusive as files store it.</summary>
    public (uint Start, uint End)? Loop { get; init; }

    /// <summary>The smpl chunk before the data rather than after it.</summary>
    public bool LoopBeforeData { get; init; }

    /// <summary>A LIST chunk of an odd size (with its pad byte) before the data.</summary>
    public bool OddChunkBeforeData { get; init; }

    /// <summary>The file holding <paramref name="samples"/> (interleaved, within ±1).</summary>
    public byte[] Build(ReadOnlySpan<float> samples)
    {
        var data = Encode(samples);
        using var body = new MemoryStream();
        if (Rf64)
        {
            var ds64 = new byte[28];
            BinaryPrimitives.WriteInt64LittleEndian(ds64.AsSpan(8), data.Length);
            BinaryPrimitives.WriteInt64LittleEndian(ds64.AsSpan(16), samples.Length / Channels);
            Chunk(body, "ds64", ds64);
        }

        Chunk(body, "fmt ", Fmt());
        if (OddChunkBeforeData)
        {
            Chunk(body, "LIST", Encoding8("INFOx"));
        }

        if (Loop is not null && LoopBeforeData)
        {
            Chunk(body, "smpl", Smpl());
        }

        Chunk(body, "data", data, Rf64 ? 0xFFFF_FFFFu : DataSize);
        if (Loop is not null && !LoopBeforeData)
        {
            Chunk(body, "smpl", Smpl());
        }

        using var file = new MemoryStream();
        file.Write(Encoding8(Rf64 ? "RF64" : "RIFF"));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, Rf64 ? 0xFFFF_FFFFu : (uint)(body.Length + 4));
        file.Write(size);
        file.Write(Encoding8("WAVE"));
        body.Position = 0;
        body.CopyTo(file);
        var bytes = file.ToArray();
        return CutBytes > 0 ? bytes[..^CutBytes] : bytes;
    }

    /// <summary>Frames of a stepped ramp from -1 to 1, each channel offset, for checking every sample lands where it should.</summary>
    public static float[] Ramp(int frames, int channels)
    {
        var samples = new float[frames * channels];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = -1f + 2f * ((i * 37) % 257) / 257f;
        }

        return samples;
    }

    /// <summary>The value <paramref name="sample"/> reads back as when stored at <paramref name="bits"/> bits of PCM.</summary>
    public static float Quantised(float sample, int bits) =>
        bits switch
        {
            8 => (Math.Clamp((int)MathF.Round(sample * 128) + 128, 0, 255) - 128) / 128f,
            16 => Math.Clamp((int)MathF.Round(sample * 32768), -32768, 32767) / 32768f,
            24 => Math.Clamp((int)MathF.Round(sample * 8_388_608), -8_388_608, 8_388_607) / 8_388_608f,
            _ => (float)(
                Math.Clamp(Math.Round(sample * 2_147_483_648.0), int.MinValue, int.MaxValue) / 2_147_483_648.0
            ),
        };

    private byte[] Fmt()
    {
        var code = FormatTag ?? (Encoding == WavEncoding.Pcm ? (ushort)1 : (ushort)3);
        var fmt = new byte[Extensible ? 40 : 16];
        BinaryPrimitives.WriteUInt16LittleEndian(fmt, Extensible ? (ushort)0xFFFE : code);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), (ushort)Channels);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(4), Rate);
        BinaryPrimitives.WriteInt32LittleEndian(fmt.AsSpan(8), Rate * Channels * Bits / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), (ushort)(Channels * Bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), (ushort)Bits);
        if (Extensible)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(16), 22);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(18), (ushort)Bits);
            BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(20), Channels == 2 ? 3u : 4u);
            BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(24), SubFormat ?? code);
            byte[] tail = [0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
            tail.CopyTo(fmt.AsSpan(26));
        }

        return fmt;
    }

    private byte[] Smpl()
    {
        var smpl = new byte[36 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(36 + 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(36 + 8), Loop!.Value.Start);
        BinaryPrimitives.WriteUInt32LittleEndian(smpl.AsSpan(36 + 12), Loop.Value.End);
        return smpl;
    }

    private byte[] Encode(ReadOnlySpan<float> samples)
    {
        var bytesPerSample = Bits / 8;
        var data = new byte[samples.Length * bytesPerSample];
        for (var i = 0; i < samples.Length; i++)
        {
            var at = data.AsSpan(i * bytesPerSample);
            var s = samples[i];
            switch (Encoding, Bits)
            {
                case (WavEncoding.IeeeFloat, 32):
                    BinaryPrimitives.WriteSingleLittleEndian(at, s);
                    break;
                case (WavEncoding.IeeeFloat, _):
                    BinaryPrimitives.WriteDoubleLittleEndian(at, s);
                    break;
                case (_, 8):
                    at[0] = (byte)Math.Clamp((int)MathF.Round(s * 128) + 128, 0, 255);
                    break;
                case (_, 16):
                    BinaryPrimitives.WriteInt16LittleEndian(
                        at,
                        (short)Math.Clamp((int)MathF.Round(s * 32768), -32768, 32767)
                    );
                    break;
                case (_, 24):
                    var v = Math.Clamp((int)MathF.Round(s * 8_388_608), -8_388_608, 8_388_607);
                    at[0] = (byte)v;
                    at[1] = (byte)(v >> 8);
                    at[2] = (byte)(v >> 16);
                    break;
                default:
                    BinaryPrimitives.WriteInt32LittleEndian(
                        at,
                        (int)Math.Clamp(Math.Round(s * 2_147_483_648.0), int.MinValue, int.MaxValue)
                    );
                    break;
            }
        }

        return data;
    }

    private static void Chunk(MemoryStream stream, string id, byte[] body, uint? size = null)
    {
        stream.Write(Encoding8(id));
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, size ?? (uint)body.Length);
        stream.Write(header);
        stream.Write(body);
        if ((body.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }

    private static byte[] Encoding8(string text) => System.Text.Encoding.ASCII.GetBytes(text);
}
