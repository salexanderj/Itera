namespace Itera.Engine.Audio.Files;

/// <summary>A sound file that cannot be read: its message says why, in words for the log.</summary>
public sealed class SoundFileException : Exception
{
    public SoundFileException() { }

    public SoundFileException(string message)
        : base(message) { }

    public SoundFileException(string message, Exception innerException)
        : base(message, innerException) { }
}
