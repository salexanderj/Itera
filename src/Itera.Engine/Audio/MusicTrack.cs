using Itera.Engine.Audio.Files;

namespace Itera.Engine.Audio;

/// <summary>
/// A piece of music a game declared by name (<see cref="MusicPlayer.Declare"/>), and what its file's header said when the
/// audio thread's step read it (<see cref="Error"/> when it could not be read: the track plays nothing).
/// </summary>
public sealed class MusicTrack
{
    internal MusicTrack(string name, string path, bool loop)
    {
        Name = name;
        Path = path;
        Loop = loop;
    }

    /// <summary>The name it is played by.</summary>
    public string Name { get; }

    /// <summary>Its file, a full path.</summary>
    public string Path { get; }

    /// <summary>
    /// True when it repeats until another track plays or it is stopped: round the loop points its file names (the part
    /// before them heard once), or whole. False plays it once.
    /// </summary>
    public bool Loop { get; }

    /// <summary>1 or 2; 0 before its header is read, or when it could not be.</summary>
    public int Channels { get; internal set; }

    /// <summary>Frames a second.</summary>
    public int SampleRate { get; internal set; }

    /// <summary>Its length in frames; -1 when not known.</summary>
    public long Frames { get; internal set; } = -1;

    /// <summary>The loop its file names, or none (a looping track then repeats whole).</summary>
    public LoopPoints LoopPoints { get; internal set; } = LoopPoints.None;

    /// <summary>What was wrong in the file but did not stop it being read; null for nothing.</summary>
    public string? Warning { get; internal set; }

    /// <summary>Why the file could not be read; null when it was (or is not read yet).</summary>
    public string? Error { get; internal set; }

    /// <summary>Its length in seconds; 0 when not known.</summary>
    public double Seconds => SampleRate > 0 && Frames > 0 ? Frames / (double)SampleRate : 0;
}
