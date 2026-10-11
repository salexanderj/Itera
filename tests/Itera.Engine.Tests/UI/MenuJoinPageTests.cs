using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Itera.Engine.Tests.Networking;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// The menu with Steam (<see cref="FakeSteamNetwork"/>): the join page lists the friends who host with a Join button
/// each (another version's disabled), a row's Join joins the friend's lobby through Steam, Host Game hosts through
/// Steam, and a friend's "Join game" joins at once from the menu but asks first during a match (Join or Stay). Another
/// version's button makes no click, under the pointer or pressed. The wheel scrolls the friends' rows, a part of a turn
/// as far as that part of a step.
/// </summary>
public sealed class MenuJoinPageTests : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;
    private const ulong Me = 76_561_198_000_000_020;
    private const ulong Alice = 76_561_198_000_000_021;
    private const ulong Bob = 76_561_198_000_000_022;
    private const string MatchKey = "22;test;1;60";

    private readonly FakeSteamNetwork _network = new();
    private readonly FakeSteamPlatform _me;
    private readonly SteamService _steam;
    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly MatchControl _matches = new();
    private readonly AudioService _audio = MenuLayerTests.QueuingAudio();
    private readonly PointerInput _pointer = new();
    private readonly UiToolkit _toolkit;
    private readonly MenuLayer _menu;
    private readonly SteamFriendList _friends;
    private readonly JoinRequests _joins;
    private readonly UISurface _surface;
    private ulong _frame;

    public MenuJoinPageTests()
    {
        _me = _network.AddPlayer(Me);
        _steam = new SteamService(
            new SteamSettings { AppId = 480 },
            NullLogger<SteamService>.Instance,
            () => (_me, "")
        );
        _steam.Initialize();
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        var options = Options.Create(new EngineOptions());
        var starts = new MatchCommands(commands, _matches, _steam, options);
        _friends = new SteamFriendList(_steam, MatchKey, NullLogger<SteamFriendList>.Instance);
        _joins = new JoinRequests(_friends, _matches, starts);
        _toolkit = new UiToolkit(_fonts, MenuStyle.Plain, options, NullLogger<UiToolkit>.Instance, _audio);
        _menu = new MenuLayer(
            _toolkit,
            MenuStyle.Plain,
            _matches,
            starts,
            _pointer,
            _window,
            _steam,
            _friends,
            _joins,
            new PlayerSettings(new EngineOptions(), CommandLineKeys.None, null, NullLogger<PlayerSettings>.Instance),
            options
        );
        _surface = new UISurface([_menu], NullLogger<UISurface>.Instance);
        _surface.Initialize(Width, Height);
        Frame();
    }

    public void Dispose()
    {
        _surface.Dispose();
        _menu.Dispose();
        _joins.Dispose();
        _toolkit.Dispose();
        _fonts.Dispose();
        _window.Dispose();
        _audio.Dispose();
        _steam.Dispose();
    }

    [Fact]
    public void The_join_page_lists_friends_hosting_and_a_rows_join_joins_their_lobby_through_steam()
    {
        var alice = Host(Alice, "Alice", MatchKey);
        Host(Bob, "Bob", "21;test;1;60");

        Click("Join Game");
        Frame();

        Assert.Equal(MenuView.Join, _menu.View);
        Assert.True(_friends.Watching);
        Assert.Equal(["Join", "Other version", "Join", "Back"], _menu.Buttons);
        Click(RowJoin(0));
        var join = _matches.TakeStart()!;
        Assert.Equal(SessionMode.Join, join.Mode);
        Assert.Equal(SessionTransportKind.Steam, join.Transport);
        Assert.Equal($"lobby:{alice}", join.Address);

        // Another version's button does nothing, and makes no click.
        Frame();
        Click("Join Game");
        Frame();
        _audio.ClearQueued();
        Click(RowJoin(1));
        Assert.Null(_matches.TakeStart());
        Assert.Equal(0, _audio.QueuedEvents.Length);
    }

    [Fact]
    public void Host_game_with_steam_hosts_through_steam()
    {
        Click("Host Game");

        Assert.Equal(SessionTransportKind.Steam, _matches.TakeStart()!.Transport);
    }

    [Fact]
    public void A_friends_join_game_joins_at_once_from_the_menu()
    {
        _me.RequestJoin(Alice, "lobby:109775240000000005");
        _steam.RunCallbacks();
        Frame();

        Assert.Equal("lobby:109775240000000005", _matches.TakeStart()!.Address);
    }

    [Fact]
    public void During_a_match_a_friends_join_game_asks_first_and_join_or_stay_answers()
    {
        _network.AddPlayer(Alice).PersonaName = "Alice";
        _matches.Phase = MatchPhase.Playing;
        Frame();
        _me.RequestJoin(Alice, "lobby:109775240000000005");
        _steam.RunCallbacks();
        Frame();

        Assert.Equal(MenuView.Confirm, _menu.View);
        Assert.Null(_matches.TakeStart());
        Assert.Equal(["Join", "Stay"], _menu.Buttons);

        // Stay (Escape too) leaves the match as it is.
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.None, _menu.View);

        _me.RequestJoin(Alice, "lobby:109775240000000005");
        _steam.RunCallbacks();
        Frame();
        Click("Join");
        Assert.Equal("lobby:109775240000000005", _matches.TakeStart()!.Address);
        Assert.Equal(MenuView.None, _menu.View);
    }

    /// <summary>A friend hosting: befriended, with a lobby and its data.</summary>
    private ulong Host(ulong friend, string name, string matchKey)
    {
        var player = _network.AddPlayer(friend);
        player.PersonaName = name;
        _network.Befriend(Me, friend);
        player.CreateLobby(4);
        var lobby = player.OwnedLobby;
        player.SetLobbyData(lobby, SessionProtocol.LobbyPlayersKey, "1");
        player.SetLobbyData(lobby, SessionProtocol.LobbyMaxKey, "4");
        player.SetLobbyData(lobby, SessionProtocol.LobbyMatchKey, matchKey);
        return lobby;
    }

    /// <summary>The wheel over the friends' rows scrolls them; a quarter turn a quarter as far as a whole one.</summary>
    [Fact]
    public void The_wheel_scrolls_the_friends_rows_by_parts_of_a_step()
    {
        for (var i = 0; i < 8; i++)
        {
            Host(Alice + 10 + (ulong)i, "Friend " + i, MatchKey);
        }

        Click("Join Game");
        Frame();
        Frame();
        Assert.Equal(MenuView.Join, _menu.View);
        var first = RowJoinRect(0).Top;
        var over = new Vector2(RowJoinRect(0).MidX, RowJoinRect(1).MidY);

        _pointer.Pointer = new UiPointer(over, false, false, false, -0.25f);
        Frame();
        _pointer.Pointer = new UiPointer(over, false, false, false, 0);
        Frame();
        var quarter = first - RowJoinRect(0).Top;
        Assert.True(quarter > 0, $"A quarter turn moved the rows {quarter} pixels.");

        _pointer.Pointer = new UiPointer(over, false, false, false, -0.75f);
        Frame();
        _pointer.Pointer = new UiPointer(over, false, false, false, 0);
        Frame();
        Assert.Equal(4 * quarter, first - RowJoinRect(0).Top, 2);
    }

    /// <summary>Row <paramref name="row"/>'s Join button, as last laid out.</summary>
    private SKRect RowJoinRect(int row) => _menu.Ui.Rect(Ui.Id(Ui.Id("menu.friend.join"), row));

    /// <summary>The middle of row <paramref name="row"/>'s Join button.</summary>
    private Vector2 RowJoin(int row)
    {
        var rect = RowJoinRect(row);
        Assert.False(rect.IsEmpty, $"No row {row}:\n{_menu.Ui.Dump()}");
        return new Vector2(rect.MidX, rect.MidY);
    }

    /// <summary>A click on the first element reading <paramref name="label"/>.</summary>
    private void Click(string label)
    {
        Assert.True(_menu.Ui.TryFind(label, out var element), $"Nothing reads '{label}':\n{_menu.Ui.Dump()}");
        Click(new Vector2(element.Rect.MidX, element.Rect.MidY));
    }

    private void Click(Vector2 at)
    {
        _pointer.Pointer = new UiPointer(at, false, false, false, 0);
        Frame();
        _pointer.Pointer = new UiPointer(at, true, true, false, 0);
        Frame();
        _pointer.Pointer = new UiPointer(at, false, false, true, 0);
        Frame();
        _pointer.Pointer = new UiPointer(at, false, false, false, 0);
    }

    /// <summary>One iteration and frame: the loop updates the friend list, then the menu draws.</summary>
    private UiDraw Frame()
    {
        _friends.Update(_frame / 60.0);
        return _surface.Draw(new FrameTime(1 / 60f, _frame / 60.0, _frame++, 0), Width, Height);
    }

    private sealed class PointerInput : IFrameInput
    {
        private readonly InputSnapshot _frame;

        public UiPointer Pointer { get; set; }

        public ref readonly InputSnapshot Frame => ref _frame;

        public Vector2 MouseSinceTick => Vector2.Zero;

        public UiPointer MenuPointer => Pointer;

        public UiKeys MenuKeys => UiKeys.None;
    }
}
