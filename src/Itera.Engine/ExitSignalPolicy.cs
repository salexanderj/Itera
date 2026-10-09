using System.Diagnostics;

namespace Itera.Engine;

/// <summary>What the game loop does with a SIGINT (Ctrl+C) or SIGTERM.</summary>
internal enum ExitSignalAction
{
    /// <summary>The first signal: stop the loop and shut down cleanly.</summary>
    RequestExit,

    /// <summary>A copy of the first signal: already handled.</summary>
    Ignore,

    /// <summary>A later signal: let the runtime end the process at once.</summary>
    Terminate,
}

/// <summary>
/// Decides what each exit signal does. The first one requests a clean shutdown. A signal within
/// <see cref="DuplicateWindow"/> of the first is a copy of it: coreutils <c>timeout</c> and other tools signal both the
/// process and its process group, so one request can arrive twice within microseconds. A signal after that window ends
/// the process at once, so a person can still stop a loop that hangs (for example in a GPU wait). Thread-safe:
/// handlers for two signals can run at the same time.
/// </summary>
internal sealed class ExitSignalPolicy
{
    /// <summary>Signals this soon after the first count as the same request. Pressing Ctrl+C twice takes longer.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(500);

    /// <summary><see cref="Stopwatch"/> timestamp of the first signal; 0 until one arrives.</summary>
    private long _firstTimestamp;

    /// <param name="timestamp">When the signal arrived, from <see cref="Stopwatch.GetTimestamp"/>.</param>
    public ExitSignalAction OnSignal(long timestamp)
    {
        var first = Interlocked.CompareExchange(ref _firstTimestamp, timestamp, 0);
        if (first == 0)
        {
            return ExitSignalAction.RequestExit;
        }

        return Stopwatch.GetElapsedTime(first, timestamp) < DuplicateWindow
            ? ExitSignalAction.Ignore
            : ExitSignalAction.Terminate;
    }
}
