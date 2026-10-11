namespace Itera.Engine.Audio.Files;

/// <summary>
/// An open sound file read from start to end (and moved about with <see cref="Seek"/>): its channels (1 or 2), rate, length,
/// and loop points, and its samples as floats within ±1, the channels interleaved (left, right, left, ...). Opened by
/// <see cref="SoundDecoders.Open(string)"/>; one thread at a time. <see cref="Read"/> allocates nothing in the WAV
/// decoder; the Ogg Vorbis decoder (NVorbis) allocates as it decodes, so it runs off the audio thread.
/// </summary>
public interface ISoundDecoder : IDisposable
{
    /// <summary>1 (mono) or 2 (stereo).</summary>
    int Channels { get; }

    /// <summary>Frames a second.</summary>
    int SampleRate { get; }

    /// <summary>The sound's length in frames; -1 when the file does not say.</summary>
    long Frames { get; }

    /// <summary>The loop the file names (a WAV's <c>smpl</c> chunk, an Ogg file's LOOPSTART tags), or none.</summary>
    LoopPoints LoopPoints { get; }

    /// <summary>What in the file was wrong but did not stop it being read (data cut short, loop points out of range); null for nothing.</summary>
    string? Warning { get; }

    /// <summary>
    /// Reads the next frames into <paramref name="output"/> (as many as fit whole: its length over <see cref="Channels"/>)
    /// and returns how many; fewer only at the end, 0 past it.
    /// </summary>
    int Read(Span<float> output);

    /// <summary>Moves to <paramref name="frame"/> (0 to <see cref="Frames"/>), so the next <see cref="Read"/> starts there.</summary>
    void Seek(long frame);
}
