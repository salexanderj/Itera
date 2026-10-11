using Itera.Engine.Networking;

namespace Itera.Engine.Matches;

/// <summary>
/// For a game's tests of its menu or HUD: puts a <see cref="MatchControl"/> the test made into the states the run's
/// loop gives it, as <see cref="MatchFlow"/> does, with no loop, window, or session: a client's join connecting and how
/// far it is, a match starting, loading, and playing, and a match's end with its reason. Never the run's own, which
/// the loop alone moves. A menu's requests are read with <see cref="MatchControl.PendingStart"/> and
/// <see cref="MatchControl.PendingLeave"/>; a real offline match without a window is <see cref="HeadlessRun.Play"/>.
/// </summary>
public static class MatchControlStates
{
    /// <summary>A client's join begins: a match counted, joining <paramref name="address"/>, no progress yet.</summary>
    public static void Joining(MatchControl matches, string address)
    {
        ArgumentNullException.ThrowIfNull(matches);
        ArgumentNullException.ThrowIfNull(address);
        Begin(matches);
        matches.Phase = MatchPhase.Joining;
        matches.JoinAddress = address;
        matches.JoinProgress = default;
    }

    /// <summary>How far the join is (<see cref="MatchControl.JoinProgress"/>).</summary>
    public static void JoinProgress(MatchControl matches, JoinProgress progress)
    {
        ArgumentNullException.ThrowIfNull(matches);
        matches.JoinProgress = progress;
    }

    /// <summary>
    /// A match's world is about to start: a host's or an offline match counted as it begins, or a join that finished
    /// (counted when it began).
    /// </summary>
    public static void Starting(MatchControl matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Phase != MatchPhase.Joining)
        {
            Begin(matches);
        }

        matches.Phase = MatchPhase.Starting;
    }

    /// <summary>The match's world exists and the loading screen covers it.</summary>
    public static void Loading(MatchControl matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        matches.Phase = MatchPhase.Loading;
    }

    /// <summary>The match is played: its world loaded, the loading screen gone.</summary>
    public static void Playing(MatchControl matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        matches.Phase = MatchPhase.Playing;
    }

    /// <summary>The match ended, or a join failed, for <paramref name="reason"/> (null for none): no match, at the menu.</summary>
    public static void Ended(MatchControl matches, string? reason)
    {
        ArgumentNullException.ThrowIfNull(matches);
        matches.Current = null;
        matches.Phase = MatchPhase.Menu;
        matches.LastEnd = reason;
        matches.JoinAddress = null;
        matches.JoinProgress = default;
    }

    private static void Begin(MatchControl matches)
    {
        matches.LastEnd = null;
        matches.Started++;
    }
}
