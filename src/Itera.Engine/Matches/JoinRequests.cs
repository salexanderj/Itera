using Itera.Engine.Commands;
using Itera.Engine.Networking;

namespace Itera.Engine.Matches;

/// <summary>A friend's "Join game" from Steam waiting for an answer: the friend's name and the address to join.</summary>
/// <param name="Name">The friend's Steam name ("a friend" when Steam gave none).</param>
/// <param name="Address">The address to join (<c>lobby:ID</c>).</param>
public sealed record JoinRequest(string Name, string Address);

/// <summary>
/// A friend's "Join game" from the Steam overlay (<see cref="SteamFriendList.JoinRequested"/>), whatever menu the game
/// shows: with no match it joins at once; during a match it waits as <see cref="Pending"/> for the menu to ask the player
/// (<see cref="Accept"/> ends the match and joins, <see cref="Decline"/> stays), and goes when that match ends. A menu
/// that never asks leaves it waiting until then. Run-wide; the loop makes it at the start, so no request is missed.
/// Main thread.
/// </summary>
public sealed class JoinRequests : IDisposable
{
    private readonly SteamFriendList _friends;
    private readonly MatchControl _matches;
    private readonly MatchCommands _starts;
    private JoinRequest? _pending;
    private int _pendingMatch;
    private bool _disposed;

    public JoinRequests(SteamFriendList friends, MatchControl matches, MatchCommands starts)
    {
        _friends = friends;
        _matches = matches;
        _starts = starts;
        friends.JoinRequested += OnJoinRequested;
    }

    /// <summary>The friend's join waiting for an answer in the match it came in, or null.</summary>
    public JoinRequest? Pending =>
        _pending is not null && _matches.Phase != MatchPhase.Menu && _matches.Started == _pendingMatch
            ? _pending
            : null;

    /// <summary>Rises whenever a request comes or is answered.</summary>
    public int Version { get; private set; }

    /// <summary>Joins the friend waiting (<see cref="Pending"/>): the match ends first (its save written), then the join starts.</summary>
    public void Accept()
    {
        if (Pending is not { } request)
        {
            return;
        }

        _pending = null;
        Version++;
        _matches.RequestStart(_starts.JoinStart(request.Address));
    }

    /// <summary>Stays in the match: the request waiting is dropped.</summary>
    public void Decline()
    {
        if (_pending is null)
        {
            return;
        }

        _pending = null;
        Version++;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _friends.JoinRequested -= OnJoinRequested;
    }

    private void OnJoinRequested(string name, string address)
    {
        if (_matches.Phase == MatchPhase.Menu)
        {
            _pending = null;
            _matches.RequestStart(_starts.JoinStart(address));
        }
        else
        {
            _pending = new JoinRequest(name.Length > 0 ? name : "a friend", address);
            _pendingMatch = _matches.Started;
        }

        Version++;
    }
}
