using System.Buffers.Binary;

namespace Itera.Engine.Audio;

/// <summary>Writes mono 16-bit PCM WAV files, for listening to recipes outside the game (<c>sound wav</c>).</summary>
public static class WavWriter
{
    /// <summary>Writes <paramref name="samples"/> at <paramref name="sampleRate"/> to <paramref name="path"/>, replacing it.</summary>
    public static void Write(string path, ReadOnlySpan<short> samples, int sampleRate)
    {
        var dataBytes = samples.Length * sizeof(short);
        var header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 36 + dataBytes);
        "WAVE"u8.CopyTo(header.AsSpan(8));
        "fmt "u8.CopyTo(header.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), sampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), dataBytes);

        var data = new byte[dataBytes];
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        }

        using var file = File.Create(path);
        file.Write(header);
        file.Write(data);
    }
}
