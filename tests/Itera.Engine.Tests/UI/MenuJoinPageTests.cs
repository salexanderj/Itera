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
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// The menu with Steam (<see cref="FakeSteamNetwork"/>): the join page lists the friends who host with a Join button
/// each (another version's disabled), a row's Join joins the friend's lobby through Steam, Host Game hosts through
/// Steam, and a friend's "Join game" joins at once from the menu but asks first during a match (Join or Stay). Another
/// version's button makes no click, under the pointer or pressed.
/// </summary>
public sealed class MenuJoinPageTests : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;
    private const float Unit = Height / 100f;
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
    private readonly MenuLayer _menu;
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
        _menu = new MenuLayer(
            _fonts,
            MenuStyle.Plain,
            _matches,
            new MatchCommands(commands, _matches, _steam, options),
            _pointer,
            _window,
            _steam,
            new SteamFriendList(_steam, MatchKey, NullLogger<SteamFriendList>.Instance),
            new PlayerSettings(new EngineOptions(), CommandLineKeys.None, null, NullLogger<PlayerSettings>.Instance),
            _audio,
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

        Click(MainButton(2));
        Frame();

        Assert.Equal(MenuView.Join, _menu.View);
        Assert.Equal(["Join", "Other version", "Join", "Back"], _menu.Buttons);
        Click(RowJoin(0));
        var join = _matches.TakeStart()!;
        Assert.Equal(SessionMode.Join, join.Mode);
        Assert.Equal(SessionTransportKind.Steam, join.Transport);
        Assert.Equal($"lobby:{alice}", join.Address);

        // Another version's button does nothing, and makes no click.
        Click(MainButton(2));
        Frame();
        _audio.ClearQueued();
        Click(RowJoin(1));
        Assert.Null(_matches.TakeStart());
        Assert.Equal(0, _audio.QueuedEvents.Length);
    }

    [Fact]
    public void Host_game_with_steam_hosts_through_steam()
    {
        Click(MainButton(1));

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
        Click(new Vector2(Width / 2f, 55 * Unit));
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

    /// <summary>Small turns of the wheel move the friends' rows once they add up to a step.</summary>
    [Fact]
    public void Small_wheel_turns_on_the_join_page_add_up()
    {
        for (var i = 0; i < 8; i++)
        {
            Host(Alice + 10 + (ulong)i, "Friend " + i, MatchKey);
        }

        Click(MainButton(2));
        Frame();
        Assert.Equal(MenuView.Join, _menu.View);

        for (var i = 0; i < 4; i++)
        {
            _pointer.Pointer = new UiPointer(new Vector2(Width / 2f, Height / 2f), false, false, false, -0.25f);
            Frame();
            Assert.Equal(i < 3 ? 0 : 1, _menu.RowScroll);
        }
    }

    private static Vector2 MainButton(int index) => new(Width / 2f, (36 + 3 + index * 7.5f) * Unit);

    /// <summary>The middle of row <paramref name="row"/>'s Join button.</summary>
    private static Vector2 RowJoin(int row) => new(Width / 2f + 24.5f * Unit, (27 + row * 7.5f + 3.4f) * Unit);

    private void Click(Vector2 at)
    {
        _pointer.Pointer = new UiPointer(at, true, true, false, 0);
        Frame();
        _pointer.Pointer = new UiPointer(at, false, false, true, 0);
        Frame();
        _pointer.Pointer = new UiPointer(at, false, false, false, 0);
    }

    private UiDraw Frame() => _surface.Draw(new FrameTime(1 / 60f, _frame / 60.0, _frame++, 0), Width, Height);

    private sealed class PointerInput : IFrameInput
    {
        private readonly InputSnapshot _frame;

        public UiPointer Pointer { get; set; }

        public ref readonly InputSnapshot Frame => ref _frame;

        public Vector2 MouseSinceTick => Vector2.Zero;

        public UiPointer MenuPointer => Pointer;
    }
}
