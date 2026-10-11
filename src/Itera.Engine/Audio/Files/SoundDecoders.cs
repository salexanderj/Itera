namespace Itera.Engine.Audio.Files;

/// <summary>Opens a sound file by what its first bytes say it is, not by its name: WAV (<c>RIFF</c> or <c>RF64</c>) or Ogg Vorbis (<c>OggS</c>).</summary>
public static class SoundDecoders
{
    /// <summary>
    /// Opens the file at <paramref name="path"/>. Throws <see cref="SoundFileException"/> saying why when it is missing or
    /// cannot be read.
    /// </summary>
    public static ISoundDecoder Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new SoundFileException(
                exception is FileNotFoundException or DirectoryNotFoundException
                    ? "no such file"
                    : $"cannot be opened ({exception.Message})",
                exception
            );
        }

        return Open(stream, closeStream: true);
    }

    /// <summary>
    /// Opens the sound in <paramref name="stream"/> (seekable, at its start). Throws <see cref="SoundFileException"/>
    /// saying why when it cannot be read; the stream is closed then too when <paramref name="closeStream"/>.
    /// </summary>
    public static ISoundDecoder Open(Stream stream, bool closeStream = true)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            Span<byte> magic = stackalloc byte[4];
            var read = stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false);
            stream.Position = 0;
            if (read == 4 && (magic.SequenceEqual("RIFF"u8) || magic.SequenceEqual("RF64"u8)))
            {
                return new WavDecoder(stream, closeStream);
            }

            if (read == 4 && magic.SequenceEqual("OggS"u8))
            {
                return new VorbisDecoder(stream, closeStream);
            }

            throw new SoundFileException("not a WAV or Ogg Vorbis file");
        }
        catch
        {
            if (closeStream)
            {
                stream.Dispose();
            }

            throw;
        }
    }
}
