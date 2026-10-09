using Itera.Engine.Networking;
using Itera.Engine.Voxels;

namespace Itera.Engine.Matches;

/// <summary>Where the run is with its match.</summary>
public enum MatchPhase
{
    /// <summary>No match: the menu (or nothing yet).</summary>
    Menu,

    /// <summary>A client connects to its host and downloads the host's world; the world does not exist yet.</summary>
    Joining,

    /// <summary>The match is settled and its world starts, once a frame saying so is shown.</summary>
    Starting,

    /// <summary>The match's world and systems exist and the loading screen covers the picture.</summary>
    Loading,

    /// <summary>The match is played.</summary>
    Playing,
}

/// <summary>
/// The run's match, for run-wide services: the current one's services (null with none), its phase, the session role
/// (offline with none), why the last one ended, and the requests to start or leave one, which <c>GameLoop</c> takes at
/// the start of its next iteration. <c>GameLoop</c> alone starts and ends matches. Main thread.
/// </summary>
public sealed class MatchControl
{
    private MatchStart? _start;
    private string? _leave;

    /// <summary>The match now, or null.</summary>
    public MatchServices? Current { get; internal set; }

    /// <summary>Where the run is with its match.</summary>
    public MatchPhase Phase { get; internal set; }

    /// <summary>Matches started in this run; the first is 1.</summary>
    public int Started { get; internal set; }

    /// <summary>The session role of the match now; offline without one.</summary>
    public SessionRole Role => Current?.Session.Role ?? SessionRole.Offline;

    /// <summary>The match's world while it exists (loading or playing), else null.</summary>
    public VoxelWorld? World => Phase is MatchPhase.Loading or MatchPhase.Playing ? Current?.Voxels : null;

    /// <summary>How far a client's join is, while <see cref="Phase"/> is <see cref="MatchPhase.Joining"/>.</summary>
    public JoinProgress JoinProgress { get; internal set; }

    /// <summary>The address a client joins, as given; null unless joining.</summary>
    public string? JoinAddress { get; internal set; }

    /// <summary>
    /// Why the last match ended or could not start, for the player ("The host left: the host quit"); null before any
    /// ended, and cleared when the next starts.
    /// </summary>
    public string? LastEnd { get; internal set; }

    /// <summary>Asks for <paramref name="start"/> to start: the match now, if any, ends first (its save written).</summary>
    public void RequestStart(MatchStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _start = start;
    }

    /// <summary>Asks for the match now to end, for <paramref name="reason"/> (the player's words, the log's too).</summary>
    public void RequestLeave(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _leave = reason;
    }

    /// <summary>The leave asked for since the last call, or null.</summary>
    internal string? TakeLeave()
    {
        var leave = _leave;
        _leave = null;
        return leave;
    }

    /// <summary>The start asked for since the last call, or null.</summary>
    internal MatchStart? TakeStart()
    {
        var start = _start;
        _start = null;
        return start;
    }
}
