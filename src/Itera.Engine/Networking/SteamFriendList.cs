using System.Globalization;
using Itera.Engine.Matches;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Networking;

/// <summary>A friend's Steam picture as RGBA rows (four bytes a pixel, the first row at the top).</summary>
public sealed record FriendAvatar(int Width, int Height, byte[] Rgba);

/// <summary>A friend hosting a game this player may join, as the menu's join page shows it.</summary>
/// <param name="SteamId">The friend's Steam id.</param>
/// <param name="Name">The friend's Steam name.</param>
/// <param name="Lobby">The lobby to join (<c>lobby:ID</c>).</param>
/// <param name="Players">Players in the game now, the host's included; 0 until the lobby's data has arrived.</param>
/// <param name="MaxPlayers">The most it takes; 0 until the lobby's data has arrived.</param>
/// <param name="OtherVersion">
/// True when the lobby's match key (protocol, generator, step rate) is not this build's: the host would refuse the join.
/// False until the lobby's data has arrived.
/// </param>
/// <param name="Avatar">The friend's picture, or null (none, or not fetched yet).</param>
public sealed record FriendHost(
    ulong SteamId,
    string Name,
    ulong Lobby,
    int Players,
    int MaxPlayers,
    bool OtherVersion,
    FriendAvatar? Avatar
);

/// <summary>
/// The friends who host a game of this app that this player may join a friend playing
/// this game in a lobby (Steam's game played, or the host's rich presence), with the lobby's players, limit, and match
/// key (written by the host's <see cref="SessionService"/>), and the friend's picture. Read again when asked
/// (<see cref="MarkStale"/>: the join page opens), every 5 s while a menu lists them (<see cref="Watching"/>), and when
/// Steam reports a change. A friend's "Join game" from the Steam overlay while the game runs becomes
/// <see cref="JoinRequested"/> (<see cref="Matches.JoinRequests"/> takes it). The loop updates it, whatever menu the
/// game shows. Without Steam the list is empty. Main thread.
/// </summary>
public sealed partial class SteamFriendList : ISteamFriendsListener
{
    /// <summary>How often the list is read again while the join page shows, in seconds.</summary>
    public const double RefreshSeconds = 5;

    private readonly SteamService _steam;
    private readonly Func<string> _matchKey;
    private readonly ILogger<SteamFriendList> _logger;
    private readonly Dictionary<ulong, FriendAvatar?> _avatars = [];
    private List<FriendHost> _hosts = [];
    private ISteamPlatform? _platform;
    private bool _stale = true;
    private double _refreshedAt = double.NegativeInfinity;

    public SteamFriendList(
        SteamService steam,
        IMatchIdentity identity,
        IOptions<EngineOptions> options,
        ILogger<SteamFriendList> logger
    )
        // The key is made at each read, not here: a voxel world's identity carries the run's object kinds, which the
        // run's start registers after this list is built.
        : this(steam, () => SessionProtocol.MatchKey(identity.Id, identity.Version, options.Value.FixedStepHz), logger)
    { }

    /// <param name="steam">Steam, when it runs.</param>
    /// <param name="matchKey">This build's match key (<see cref="SessionProtocol.MatchKey"/>).</param>
    /// <param name="logger">Logger.</param>
    internal SteamFriendList(SteamService steam, string matchKey, ILogger<SteamFriendList> logger)
        : this(steam, () => matchKey, logger) { }

    /// <param name="steam">Steam, when it runs.</param>
    /// <param name="matchKey">Makes this build's match key (<see cref="SessionProtocol.MatchKey"/>) when the list is read.</param>
    /// <param name="logger">Logger.</param>
    internal SteamFriendList(SteamService steam, Func<string> matchKey, ILogger<SteamFriendList> logger)
    {
        _steam = steam;
        _matchKey = matchKey;
        _logger = logger;
    }

    /// <summary>The friends hosting now, by name.</summary>
    public IReadOnlyList<FriendHost> Hosts => _hosts;

    /// <summary>Rises whenever <see cref="Hosts"/> changes.</summary>
    public int Version { get; private set; }

    /// <summary>True when Steam runs, so friends can be listed.</summary>
    public bool Available => _steam.IsAvailable;

    /// <summary>Why Steam does not run, when it does not.</summary>
    public string UnavailableReason => _steam.UnavailableReason;

    /// <summary>
    /// A friend picked "Join game" in Steam while this game runs: the friend's name and the address to join
    /// (<c>lobby:ID</c>).
    /// </summary>
    public event Action<string, string>? JoinRequested;

    /// <summary>Reads the list again at the next <see cref="Update(double)"/>.</summary>
    public void MarkStale() => _stale = true;

    /// <summary>True while a menu lists the friends hosting (the join page shows): the list is kept fresh.</summary>
    public bool Watching { get; set; }

    /// <summary>
    /// Takes Steam's reports, and keeps the list fresh while <see cref="Watching"/>. The loop calls it once an
    /// iteration.
    /// </summary>
    public void Update(double nowSeconds) => Update(nowSeconds, Watching);

    /// <summary>
    /// Takes Steam's reports (once Steam runs), and while <paramref name="watching"/> (the join page shows) reads the
    /// list again when it is stale or every <see cref="RefreshSeconds"/>. Main thread, once a frame.
    /// </summary>
    public void Update(double nowSeconds, bool watching)
    {
        if (_platform is null && _steam.Platform is { } platform)
        {
            _platform = platform;
            platform.FriendsListener = this;
        }

        if (!watching || _platform is null)
        {
            return;
        }

        var due = nowSeconds - _refreshedAt >= RefreshSeconds;
        if (!_stale && !due)
        {
            return;
        }

        _stale = false;
        if (due)
        {
            _refreshedAt = nowSeconds;
        }

        // Lobby data is asked for on the timed reads only: its arrival marks the list stale, and asking again then would
        // never stop.
        Refresh(_platform, requestData: due);
    }

    void ISteamFriendsListener.OnFriendChanged(ulong friend) => _stale = true;

    void ISteamFriendsListener.OnLobbyData(ulong lobby) => _stale = true;

    void ISteamFriendsListener.OnAvatarLoaded(ulong friend)
    {
        _avatars.Remove(friend);
        _stale = true;
    }

    void ISteamFriendsListener.OnJoinRequested(ulong friend, string address)
    {
        var name = _platform?.FriendName(friend) ?? "";
        LogJoinRequested(name, address);
        if (address.Length > 0)
        {
            JoinRequested?.Invoke(name, address);
        }
    }

    private void Refresh(ISteamPlatform platform, bool requestData)
    {
        var hosts = new List<FriendHost>();
        var matchKey = _matchKey();
        var count = platform.FriendCount;
        for (var i = 0; i < count; i++)
        {
            var friend = platform.FriendAt(i);
            var lobby = platform.FriendLobby(friend);
            if (lobby == 0)
            {
                continue;
            }

            if (requestData)
            {
                platform.RequestLobbyData(lobby);
            }

            var key = platform.LobbyData(lobby, SessionProtocol.LobbyMatchKey);
            hosts.Add(
                new FriendHost(
                    friend,
                    platform.FriendName(friend),
                    lobby,
                    Number(platform.LobbyData(lobby, SessionProtocol.LobbyPlayersKey)),
                    Number(platform.LobbyData(lobby, SessionProtocol.LobbyMaxKey)),
                    key.Length > 0 && !string.Equals(key, matchKey, StringComparison.Ordinal),
                    Avatar(platform, friend)
                )
            );
        }

        hosts.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        if (!hosts.SequenceEqual(_hosts))
        {
            _hosts = hosts;
            Version++;
        }
    }

    /// <summary>A friend's picture, read once; null while Steam fetches it (asked again when it arrives).</summary>
    private FriendAvatar? Avatar(ISteamPlatform platform, ulong friend)
    {
        if (_avatars.TryGetValue(friend, out var known))
        {
            return known;
        }

        if (!platform.FriendAvatar(friend, out var width, out var height, out var rgba))
        {
            return null;
        }

        var avatar = rgba is null ? null : new FriendAvatar(width, height, rgba);
        _avatars[friend] = avatar;
        return avatar;
    }

    private static int Number(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;

    [LoggerMessage(Level = LogLevel.Information, Message = "Steam: {Name} asked to join ({Address})")]
    private partial void LogJoinRequested(string name, string address);
}
