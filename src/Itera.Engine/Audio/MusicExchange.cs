namespace Itera.Engine.Audio;

/// <summary>What a music command asks.</summary>
internal enum MusicCommandKind : byte
{
    /// <summary>Play a track, cross-fading from what plays.</summary>
    Play,

    /// <summary>Fade out what plays.</summary>
    Stop,
}

/// <summary>A music command from the main thread to the audio thread.</summary>
/// <param name="Kind">Play or stop.</param>
/// <param name="Track">The track to play; -1 for a stop.</param>
/// <param name="FadeSeconds">How long the fade takes, seconds (0: at once).</param>
/// <param name="Restart">Play the track from its start even when it plays already.</param>
/// <param name="Play">The play's number, counted by the main thread, which the audio thread reports when the track ends.</param>
internal readonly record struct MusicCommand(
    MusicCommandKind Kind,
    int Track,
    float FadeSeconds,
    bool Restart,
    int Play
);

/// <summary>
/// How the main thread hands music commands to the audio thread (<see cref="Post"/>, <see cref="Take"/>; one lock around
/// a fixed array, as <see cref="AudioExchange"/>), and how the audio thread tells it a track ended by itself (its file's
/// end, or a file that failed), <see cref="Ended"/>. Past <see cref="MaxCommands"/> waiting, the oldest is dropped: the
/// last command decides what plays. Allocates nothing.
/// </summary>
internal sealed class MusicExchange
{
    /// <summary>The most commands that wait for the audio thread.</summary>
    public const int MaxCommands = 8;

    private readonly Lock _gate = new();
    private readonly MusicCommand[] _commands = new MusicCommand[MaxCommands];
    private int _count;
    private int _ended;

    /// <summary>The newest play (its number) whose track ended by itself; 0 for none. Any thread.</summary>
    public int Ended => Volatile.Read(ref _ended);

    /// <summary>Adds <paramref name="commands"/> to those waiting. Main thread.</summary>
    public void Post(ReadOnlySpan<MusicCommand> commands)
    {
        if (commands.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            foreach (ref readonly var command in commands)
            {
                Append(_commands, ref _count, in command);
            }
        }
    }

    /// <summary>Moves the waiting commands into <paramref name="into"/> (at least <see cref="MaxCommands"/> long), oldest first, and returns how many. Audio thread.</summary>
    public int Take(Span<MusicCommand> into)
    {
        lock (_gate)
        {
            var count = _count;
            _commands.AsSpan(0, count).CopyTo(into);
            _count = 0;
            return count;
        }
    }

    /// <summary>Play <paramref name="play"/>'s track ended by itself. Audio thread.</summary>
    public void ReportEnded(int play)
    {
        if (play > _ended)
        {
            Volatile.Write(ref _ended, play);
        }
    }

    /// <summary>Adds <paramref name="command"/> after the <paramref name="count"/> in <paramref name="commands"/>; when they are full, the oldest goes.</summary>
    internal static void Append(MusicCommand[] commands, ref int count, in MusicCommand command)
    {
        if (count == commands.Length)
        {
            commands.AsSpan(1).CopyTo(commands);
            count--;
        }

        commands[count++] = command;
    }
}
