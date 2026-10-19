using System.Globalization;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Itera.Engine.UI.Toolkit;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.UI.Menu;

/// <summary>What the menu shows.</summary>
public enum MenuView
{
    /// <summary>Nothing: a match is played.</summary>
    None,

    /// <summary>The main menu: play offline, host, join, a game's pages, settings, quit.</summary>
    Main,

    /// <summary>Joining a game: the friends who host one (through Steam), and the address box.</summary>
    Join,

    /// <summary>The settings.</summary>
    Settings,

    /// <summary>A client joins its host: how far, and cancel.</summary>
    Joining,

    /// <summary>A match's world starts.</summary>
    Starting,

    /// <summary>The menu over a match (Escape): resume, a game's pages, settings, leave the match, quit.</summary>
    InGame,

    /// <summary>A friend's "Join game" from Steam during a match: leave this match and join, or stay.</summary>
    Confirm,

    /// <summary>A game's page (<see cref="MenuPages"/>).</summary>
    Page,
}

/// <summary>
/// The engine's menu (<see cref="IMenu"/>; a game may swap it for its own, or add to it with <see cref="MenuPages"/> and
/// <see cref="SettingsRows"/>), a toolkit layer (<see cref="UiLayer"/>): over the loading screen's ring when no match is
/// played (the main menu, the join and settings pages, a join's progress, a match's start), and over the shaded match
/// when Escape opens it or a friend's "Join game" asks first (<see cref="JoinRequests"/>). Its buttons ask
/// <see cref="MatchControl"/> for matches (<see cref="MatchCommands"/>' starts) and the window to close; the join page
/// lists the friends who host (<see cref="SteamFriendList"/>); the Settings page is <see cref="SettingsPage"/>. While it
/// is open it takes the pointer and the keyboard's navigation keys, and the game gets no input
/// (<see cref="IMenuInput"/>); the band at the bottom says what the element under the pointer (or with the focus)
/// does, or why something cannot be done, and the main page why the last match ended. Its look is the game's
/// <see cref="MenuStyle"/>, its body text 32.4 units (3 % of the reference screen's height), its title 2.5 times that;
/// it clicks as the toolkit's layers do. The music track <c>Engine:Audio:MenuMusic</c> names
/// (<see cref="MusicPlayer.MenuTrack"/>) plays while there is no match or one is being joined, fading in over 2 s, and fades
/// out as a match starts. Main thread.
/// </summary>
public sealed class MenuLayer : UiLayer, IMenu
{
    /// <summary>The longest address that can be typed.</summary>
    public const int MaxAddressLength = 120;

    /// <summary>Friends' rows the join page shows at once; the wheel scrolls through more.</summary>
    public const int VisibleRows = 5;

    /// <summary>The menu's body text, in units: 3 % of the reference screen's height.</summary>
    public const float BodyUnits = 32.4f;

    private const float ButtonWidth = 367.2f;
    private const float ButtonHeight = 64.8f;
    private const float Gap = 16.2f;
    private const float BandHeight = 75.6f;

    // How far the game's corners keep from the screen's left edge and the band.
    private const float CornerMargin = 21.6f;
    private const float FriendRowHeight = 81;
    private const float FriendsWidth = 691.2f;

    private static readonly int s_shade = Ui.Id("menu.shade");
    private static readonly int s_band = Ui.Id("menu.band");
    private static readonly int s_play = Ui.Id("menu.play");
    private static readonly int s_host = Ui.Id("menu.host");
    private static readonly int s_join = Ui.Id("menu.join");
    private static readonly int s_settings = Ui.Id("menu.settings");
    private static readonly int s_quit = Ui.Id("menu.quit");
    private static readonly int s_back = Ui.Id("menu.back");
    private static readonly int s_cancel = Ui.Id("menu.cancel");
    private static readonly int s_resume = Ui.Id("menu.resume");
    private static readonly int s_leave = Ui.Id("menu.leave");
    private static readonly int s_confirmJoin = Ui.Id("menu.confirm.join");
    private static readonly int s_stay = Ui.Id("menu.confirm.stay");
    private static readonly int s_friends = Ui.Id("menu.friends");
    private static readonly int s_friendJoin = Ui.Id("menu.friend.join");
    private static readonly int s_avatar = Ui.Id("menu.friend.avatar");
    private static readonly int s_address = Ui.Id("menu.address");
    private static readonly int s_joinAddress = Ui.Id("menu.address.join");
    private static readonly int s_gamePage = Ui.Id("menu.page");

    private readonly MatchControl _matches;
    private readonly MatchCommands _starts;
    private readonly WindowService _window;
    private readonly SteamService _steam;
    private readonly SteamFriendList _friends;
    private readonly JoinRequests _joins;
    private readonly PlayerSettings _settings;
    private readonly MusicPlayer? _music;
    private readonly SettingsPage _settingsPage;
    private readonly MenuPages _pages;
    private readonly MenuStyle _colours;
    private readonly string _title;
    private readonly TextEntry _address;
    private readonly Dictionary<int, string> _hints = [];
    private readonly Dictionary<int, ulong> _avatarFriends = [];
    private readonly Dictionary<ulong, (FriendAvatar Source, SKImage Image)> _avatars = [];
    private readonly Dictionary<ulong, (FriendHost Host, string Players, string Hint)> _hostTexts = [];
    private MenuView _page = MenuView.Main;
    private bool _inGame;
    private bool _inGameSettings;
    private MenuPage? _gamePage;
    private string? _notice;
    private string? _lastEnd;
    private int _seenStarted;
    private bool _seenMatch;
    private bool _menuMusic;
    private (JoinStage Stage, long Tenths) _joinShown = (JoinStage.Failed, -1);
    private string _status = "";
    private (JoinRequest? Request, string Text) _confirmText;
    private bool _disposed;

    public MenuLayer(
        UiToolkit toolkit,
        MenuStyle colours,
        MatchControl matches,
        MatchCommands starts,
        IFrameInput input,
        WindowService window,
        SteamService steam,
        SteamFriendList friends,
        JoinRequests joins,
        PlayerSettings settings,
        IOptions<EngineOptions> options,
        MenuPages? pages = null,
        SettingsRows? rows = null,
        MusicPlayer? music = null
    )
        : base(toolkit, input)
    {
        ArgumentNullException.ThrowIfNull(options);
        _colours = colours;
        _matches = matches;
        _starts = starts;
        _window = window;
        _steam = steam;
        _friends = friends;
        _joins = joins;
        _settings = settings;
        _music = music;
        _pages = pages ?? new MenuPages();
        _settingsPage = new SettingsPage(settings, rows);
        _title = options.Value.Application.Name;
        _address = new TextEntry(MaxAddressLength, PressJoinAddress);
        Ui.Style = new MenuLook(colours);
    }

    /// <summary>What the menu shows now: its page with no match, a match's join or start, or the menu over a match.</summary>
    public MenuView View =>
        _matches.Phase == MatchPhase.Menu ? _page
        : _matches.Phase == MatchPhase.Joining ? MenuView.Joining
        : _matches.Phase == MatchPhase.Starting ? MenuView.Starting
        : _joins.Pending is not null ? MenuView.Confirm
        : _inGame
            ? (
                _inGameSettings ? MenuView.Settings
                : _gamePage is not null ? MenuView.Page
                : MenuView.InGame
            )
        : MenuView.None;

    public bool IsOpen => View != MenuView.None;

    public override bool Visible => IsOpen;

    /// <summary>Over the HUD; the console is drawn over it (registered after it).</summary>
    public override bool AboveMatch => true;

    /// <summary>The address box while it has the typing on the join page.</summary>
    public ITextInput? TextFocus => View == MenuView.Join ? Ui.Typing : null;

    /// <summary>True while the Settings page waits for a key or mouse button to bind.</summary>
    public bool CapturingBinding => View == MenuView.Settings && _settingsPage.CapturingBinding;

    /// <summary>The Settings page's tab shown.</summary>
    public string SettingsTab => _settingsPage.Tab;

    /// <summary>The address box's text, for tests.</summary>
    public TextLine Address => _address.Line;

    /// <summary>The labels of the buttons shown, in order, for tests.</summary>
    public IEnumerable<string> Buttons
    {
        get
        {
            for (var i = 0; i < Ui.Count; i++)
            {
                var element = Ui.ElementAt(i);
                if (element.Kind == UiKind.Button && element.Text is { } label)
                {
                    yield return label;
                }
            }
        }
    }

    /// <summary>The address box as last laid out; empty when the page has none (tests).</summary>
    internal SKRect AddressBox => Ui.Rect(s_address);

    public void BindingPressed(InputBinding binding) => _settingsPage.BindingPressed(binding);

    public void Escape()
    {
        if (CapturingBinding)
        {
            // Waiting for a key to bind: Escape keeps the binding it had.
            _settingsPage.Escape();
            return;
        }

        switch (View)
        {
            case MenuView.None:
                _inGame = true;
                break;
            case MenuView.InGame:
                _inGame = false;
                break;
            case MenuView.Confirm:
                _joins.Decline();
                break;
            case MenuView.Settings:
                LeaveSettings();
                break;
            case MenuView.Join:
                _page = MenuView.Main;
                break;
            case MenuView.Page when _gamePage is { } page:
                if (!page.Page.Escape())
                {
                    LeavePage();
                }

                break;
            case MenuView.Joining:
                _matches.RequestLeave("The join was cancelled.");
                return;
            default:
                return;
        }

        _notice = null;
    }

    protected override void BeforeUpdate(in FrameTime time)
    {
        FollowMatches();
        FollowMusic();
    }

    /// <summary>The address box's typing goes through the menu's input (<see cref="TextFocus"/>), not the game's.</summary>
    protected override void GiveTyping(TextEntry? entry, TextEntry? before) { }

    protected override bool StillTyping(TextEntry entry) => true;

    protected override void Build(Ui ui)
    {
        var view = View;
        // The loop takes Steam's reports; the list is kept fresh while the join page shows.
        _friends.Watching = view == MenuView.Join;
        var overMatch = _matches.Phase != MatchPhase.Menu;
        if (overMatch && view is MenuView.InGame or MenuView.Settings or MenuView.Confirm or MenuView.Page)
        {
            // Over the match: the picture shaded, so the menu reads. In the flow, declared first, so it draws under the
            // page (anchored elements draw over the flow).
            ui.Area(s_shade, UiBox.Fill, UiMarks.Passive);
        }

        switch (view)
        {
            case MenuView.Main:
                Main(ui);
                break;
            case MenuView.InGame:
                InGame(ui);
                break;
            case MenuView.Join:
                Join(ui);
                break;
            case MenuView.Settings:
                Settings(ui);
                break;
            case MenuView.Page:
                GamePage(ui);
                break;
            case MenuView.Joining:
                Joining(ui);
                break;
            case MenuView.Starting:
                Message(ui, "Starting…");
                break;
            case MenuView.Confirm:
                Confirm(ui);
                break;
        }

        Band(ui, view);
    }

    protected override void DrawArea(UiPainter painter, in UiElement element)
    {
        ArgumentNullException.ThrowIfNull(painter);
        if (element.Id == s_shade || element.Id == s_band)
        {
            painter.Fill(element.Rect, _colours.Shade);
        }
        else if (_avatarFriends.TryGetValue(element.Id, out var friend))
        {
            if (AvatarImage(friend) is { } image)
            {
                painter.Canvas.DrawImage(image, element.Rect, new SKSamplingOptions(SKFilterMode.Linear));
            }
            else
            {
                painter.Fill(element.Rect, _colours.Field);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            foreach (var (_, image) in _avatars.Values)
            {
                image.Dispose();
            }

            _avatars.Clear();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private void Main(Ui ui)
    {
        using (Page(ui))
        {
            PageTitle(ui, _title);
            var steam = _steam.IsAvailable;
            if (Button(ui, s_play, "Play Offline", "Plays alone."))
            {
                _matches.RequestStart(_starts.OfflineStart());
            }

            if (
                Button(
                    ui,
                    s_host,
                    "Host Game",
                    steam
                        ? "Hosts through Steam: friends join from their Join Game."
                        : "Steam is not running: hosts on UDP; others join by address."
                )
            )
            {
                _matches.RequestStart(_starts.HostStart());
            }

            if (Button(ui, s_join, "Join Game", "Joins a friend's game."))
            {
                _page = MenuView.Join;
                // Typing goes to the address box at once; a click elsewhere takes it away.
                Ui.StartTyping(s_address);
                Ui.ScrollTo(s_friends, 0);
                _friends.Watching = true;
                _friends.MarkStale();
            }

            GamePageButtons(ui, MenuPlace.Main);
            SettingsButton(ui);
            QuitButton(ui);
        }

        Corners(ui);
    }

    /// <summary>The game's corners (<see cref="MenuPages.Corners"/>): a column at the bottom left, over the band.</summary>
    private void Corners(Ui ui)
    {
        var corners = _pages.Corners;
        if (corners.Count == 0)
        {
            return;
        }

        using (
            ui.Column(
                0,
                new UiBox
                {
                    Gap = Gap / 2,
                    Anchor = UiAnchor.Screen(UiPoint.BottomLeft, CornerMargin, -BandHeight - CornerMargin),
                }
            )
        )
        {
            for (var i = 0; i < corners.Count; i++)
            {
                corners[i].Build(ui);
            }
        }
    }

    private void InGame(Ui ui)
    {
        using (Page(ui))
        {
            PageTitle(ui, "Menu");
            if (Button(ui, s_resume, "Resume", "Back to the game (Escape)."))
            {
                _inGame = false;
            }

            GamePageButtons(ui, MenuPlace.InGame);
            SettingsButton(ui);
            if (Button(ui, s_leave, "Leave Match", "Ends this match and goes back to the main menu."))
            {
                _matches.RequestLeave("You left the match.");
                _inGame = false;
            }

            QuitButton(ui);
        }
    }

    private void GamePageButtons(Ui ui, MenuPlace place)
    {
        var pages = _pages.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            if (page.Place != place && page.Place != MenuPlace.Both)
            {
                continue;
            }

            if (Button(ui, Ui.Id(s_gamePage, i), page.Label, page.Hint))
            {
                _gamePage = page;
                if (_matches.Phase == MatchPhase.Menu)
                {
                    _page = MenuView.Page;
                }
            }
        }
    }

    private void SettingsButton(Ui ui)
    {
        if (!Button(ui, s_settings, "Settings", "Sound, the mouse, the screen, the picture, and the keys."))
        {
            return;
        }

        _settingsPage.Opened();
        if (_matches.Phase == MatchPhase.Menu)
        {
            _page = MenuView.Settings;
        }
        else
        {
            _inGameSettings = true;
        }
    }

    private void QuitButton(Ui ui)
    {
        if (Button(ui, s_quit, "Quit", "Closes the game."))
        {
            _window.RequestExit("Quit in the menu");
        }
    }

    private void Settings(Ui ui)
    {
        using (Panel(ui))
        {
            Title(ui, "Settings");
            _settingsPage.Build(ui);
            if (_settingsPage.Footer(ui))
            {
                LeaveSettings();
            }
        }
    }

    private void LeaveSettings()
    {
        _settings.Save();
        if (_matches.Phase == MatchPhase.Menu)
        {
            _page = MenuView.Main;
        }
        else
        {
            _inGameSettings = false;
        }
    }

    private void GamePage(Ui ui)
    {
        if (_gamePage is not { } page)
        {
            return;
        }

        using (Panel(ui))
        {
            Title(ui, page.Page.Title);
            page.Page.Build(ui);
            if (Button(ui, s_back, "Back", ""))
            {
                LeavePage();
            }
        }
    }

    private void LeavePage()
    {
        _gamePage = null;
        if (_matches.Phase == MatchPhase.Menu)
        {
            _page = MenuView.Main;
        }
    }

    /// <summary>
    /// The join page: a row for each friend hosting (the wheel scrolls past <see cref="VisibleRows"/>), each with its
    /// picture, name, players, and Join; then the address box with Join and Back.
    /// </summary>
    private void Join(Ui ui)
    {
        using (Panel(ui))
        {
            Title(ui, "Join Game");
            var hosts = _friends.Hosts;
            PruneAvatars(hosts);
            if (!_friends.Available || hosts.Count == 0)
            {
                var empty = !_friends.Available
                    ? "Steam is not running: no friends to list."
                    : "No friends are hosting a game now.";
                ui.Text(empty, UiText.Body with { Tone = UiTone.Dim });
            }

            using (
                ui.Scroll(
                    s_friends,
                    new UiBox
                    {
                        Width = UiSize.Fixed(FriendsWidth),
                        // Room for the rows shown at once, also while fewer or none are: the address box stays put.
                        Height = UiSize.Fixed(VisibleRows * FriendRowHeight),
                    }
                )
            )
            {
                for (var i = 0; i < hosts.Count; i++)
                {
                    FriendRow(ui, i, hosts[i]);
                }
            }

            ui.Text("Or type a host's address:");
            _hints[s_address] = "host:port over UDP, or lobby:ID or steam:ID through Steam.";
            ui.TextBox(
                s_address,
                _address,
                "127.0.0.1:47600",
                UiBox.Sized(UiSize.Fixed(475.2f), UiSize.Fixed(ButtonHeight))
            );
            using (ui.Row(0, new UiBox { Gap = 2 * Gap }))
            {
                if (Button(ui, s_joinAddress, "Join", "Joins the address typed.", 226.8f))
                {
                    JoinAddress();
                }

                if (Button(ui, s_back, "Back", "", 226.8f))
                {
                    _page = MenuView.Main;
                }
            }
        }
    }

    private void FriendRow(Ui ui, int index, FriendHost host)
    {
        var texts = HostTexts(host);
        using (
            ui.Row(
                0,
                new UiBox
                {
                    Width = UiSize.Grow(),
                    Height = UiSize.Fixed(FriendRowHeight),
                    Gap = Gap,
                    AlignY = UiAlign.Centre,
                }
            )
        )
        {
            var avatar = Ui.Id(s_avatar, index);
            _avatarFriends[avatar] = host.SteamId;
            var version = host.Avatar is null
                ? 0
                : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(host.Avatar);
            ui.Area(avatar, UiBox.Fixed(62.6f, 62.6f), UiMarks.Passive, version);
            ui.Text(host.Name, box: new UiBox { Width = UiSize.Grow() });
            if (host.MaxPlayers > 0)
            {
                ui.Text(texts.Players, UiText.Body with { Tone = UiTone.Dim });
            }

            var join = Ui.Id(s_friendJoin, index);
            _hints[join] = texts.Hint;
            var marks = host.OtherVersion ? UiMarks.Disabled : UiMarks.None;
            var label = host.OtherVersion ? "Other version" : "Join";
            if (ui.Button(join, label, box: UiBox.Fixed(162, 57), marks: marks).Clicked)
            {
                var lobby = host.Lobby.ToString(CultureInfo.InvariantCulture);
                _matches.RequestStart(_starts.JoinStart(SteamAddress.LobbyPrefix + lobby));
                _page = MenuView.Main;
            }
        }
    }

    /// <summary>A friend's players and hint, made again only when the friend's entry changes.</summary>
    private (string Players, string Hint) HostTexts(FriendHost host)
    {
        if (_hostTexts.TryGetValue(host.SteamId, out var known) && ReferenceEquals(known.Host, host))
        {
            return (known.Players, known.Hint);
        }

        var players = string.Create(CultureInfo.InvariantCulture, $"{host.Players}/{host.MaxPlayers}");
        var hint = host.OtherVersion
            ? $"{host.Name} plays another version of the game."
            : $"Joins {host.Name}'s game through Steam.";
        _hostTexts[host.SteamId] = (host, players, hint);
        return (players, hint);
    }

    private void Joining(Ui ui)
    {
        ShowJoin();
        using (LinePage(ui, _status))
        {
            if (Button(ui, s_cancel, "Cancel", "Stops joining."))
            {
                _matches.RequestLeave("The join was cancelled.");
            }
        }
    }

    private void Confirm(Ui ui)
    {
        if (_joins.Pending is not { } request)
        {
            return;
        }

        if (!ReferenceEquals(_confirmText.Request, request))
        {
            _confirmText = (request, $"Leave this match and join {request.Name}?");
        }

        using (LinePage(ui, _confirmText.Text))
        {
            if (Button(ui, s_confirmJoin, "Join", "Ends this match and joins."))
            {
                // The match now ends first (its save written), then the join starts.
                _joins.Accept();
            }

            if (Button(ui, s_stay, "Stay", "Stays in this match (Escape)."))
            {
                _joins.Decline();
            }
        }
    }

    private static void Message(Ui ui, string text)
    {
        using (LinePage(ui, text)) { }
    }

    /// <summary>A page of one line, 46 % down, with its buttons (declared in the scope) from 52 % down.</summary>
    private static UiScope LinePage(Ui ui, string line)
    {
        var page = Page(ui);
        ui.Space(UiBox.Sized(UiSize.Fit, UiSize.Fixed(206.5f)));
        ui.Text(line);
        ui.Space(UiBox.Sized(UiSize.Fit, UiSize.Fixed(22.8f)));
        return page;
    }

    /// <summary>
    /// The band at the bottom: a notice (a bad address), the key a binding waits for, else what the element under the
    /// pointer (or with the focus) does; over it on the main menu, why the last match ended.
    /// </summary>
    private void Band(Ui ui, MenuView view)
    {
        var (text, tone) =
            _notice is not null ? (_notice, UiTone.Warning)
            : view == MenuView.Settings && _settingsPage.Notice is { } waiting ? (waiting, UiTone.Normal)
            : HintOf(Ui.Hovered) is { Length: > 0 } hovered ? (hovered, UiTone.Dim)
            : Ui.FocusShown && HintOf(Ui.Focused) is { Length: > 0 } focused ? (focused, UiTone.Dim)
            : (null, UiTone.Normal);
        if (view == MenuView.Main && _lastEnd is not null)
        {
            ui.Text(
                _lastEnd,
                UiText.Body with
                {
                    Tone = UiTone.Warning,
                    Align = UiAlign.Centre,
                },
                new UiBox { Anchor = UiAnchor.Screen(UiPoint.Bottom, 0, -BandHeight - 4) }
            );
        }

        if (text is null)
        {
            return;
        }

        using (
            ui.Stack(
                0,
                new UiBox
                {
                    Width = UiSize.Grow(),
                    Height = UiSize.Fixed(BandHeight),
                    AlignX = UiAlign.Centre,
                    AlignY = UiAlign.Centre,
                    Anchor = UiAnchor.Screen(UiPoint.Bottom),
                }
            )
        )
        {
            ui.Area(s_band, UiBox.Fill, UiMarks.Passive);
            ui.Text(text, UiText.Body with { Tone = tone, Align = UiAlign.Centre });
        }
    }

    private string? HintOf(int id) =>
        id == 0 ? null
        : _hints.TryGetValue(id, out var hint) ? hint
        : _settingsPage.HintOf(id);

    /// <summary>A page over the ring or the match: a column across the screen, its children centred, from 22 % down.</summary>
    private static UiScope Page(Ui ui) =>
        ui.Column(0, UiBox.Fill with { AlignX = UiAlign.Centre, Padding = new UiEdges(0, 241.4f, 0, 0), Gap = Gap });

    /// <summary>A page's title, with the room under it the menu had before the toolkit (its buttons from 36 % down).</summary>
    private static void PageTitle(Ui ui, string title)
    {
        Title(ui, title);
        ui.Space(UiBox.Fixed(0, 9.3f));
    }

    /// <summary>A page on a panel: centred across the screen, from 9 % down.</summary>
    private static UiScope Panel(Ui ui) =>
        ui.Column(
            0,
            new UiBox
            {
                Panel = true,
                Padding = new UiEdges(43.2f, 25.9f, 43.2f, 43.2f),
                Gap = Gap,
                AlignX = UiAlign.Centre,
                Anchor = UiAnchor.Screen(UiPoint.Top, 0, 97.2f),
            }
        );

    private static void Title(Ui ui, string title) => ui.Text(title, UiText.Title);

    /// <summary>A button of the menu's size, its hint kept for the band; true when clicked.</summary>
    private bool Button(Ui ui, int id, string label, string hint, float width = ButtonWidth)
    {
        _hints[id] = hint;
        return ui.Button(id, label, box: UiBox.Fixed(width, ButtonHeight)).Clicked;
    }

    /// <summary>Enter in the address box: it clicks as the Join button does, and joins.</summary>
    private void PressJoinAddress()
    {
        Toolkit.PlayPress();
        JoinAddress();
    }

    /// <summary>Joins the address typed, or says why it cannot.</summary>
    private void JoinAddress()
    {
        var address = _address.Line.Text.Trim();
        if (!SessionAddress.TryParse(address, out _, out _) && !SteamAddress.TryParse(address, out _, out _))
        {
            _notice =
                address.Length == 0
                    ? "Type the host's address: host:port (or lobby:ID, steam:ID)."
                    : $"'{address}' is not host:port, lobby:ID, or steam:ID.";
            return;
        }

        _notice = null;
        _matches.RequestStart(_starts.JoinStart(address));
        _page = MenuView.Main;
    }

    /// <summary>The join's line, composed again only when its stage or its tenth of a megabyte changes.</summary>
    private void ShowJoin()
    {
        var progress = _matches.JoinProgress;
        var tenths = progress.DownloadedBytes * 10 / (1024 * 1024);
        if (_joinShown == (progress.Stage, tenths))
        {
            return;
        }

        _joinShown = (progress.Stage, tenths);
        var address = _matches.JoinAddress ?? "";
        _status = progress.Stage switch
        {
            JoinStage.Connecting => $"Joining {address}…",
            JoinStage.Welcoming => $"Joining {address}: waiting for the host",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"Downloading the host's world: {tenths / 10.0:F1} of {progress.TotalBytes / (1024.0 * 1024.0):F1} MB"
            ),
        };
    }

    /// <summary>
    /// A match that started (also one that failed before a frame saw it) or ended changes the page: the menu over a match
    /// closes, and an end shows the main menu (a friend's join asked about in it goes with it).
    /// </summary>
    private void FollowMatches()
    {
        var hasMatch = _matches.Phase != MatchPhase.Menu;
        if (hasMatch == _seenMatch && _matches.Started == _seenStarted && _matches.LastEnd == _lastEnd)
        {
            return;
        }

        if ((!hasMatch && _seenMatch) || _matches.Started != _seenStarted)
        {
            _page = MenuView.Main;
            _gamePage = null;
        }

        _seenMatch = hasMatch;
        _seenStarted = _matches.Started;
        _lastEnd = _matches.LastEnd;
        _inGame = false;
        _inGameSettings = false;
    }

    /// <summary>
    /// The menu's track plays while there is no match or one is being joined, and fades out as a match starts (unless the game
    /// has played another meanwhile).
    /// </summary>
    private void FollowMusic()
    {
        var wanted = _matches.Phase is MatchPhase.Menu or MatchPhase.Joining;
        if (_music is null || wanted == _menuMusic)
        {
            return;
        }

        _menuMusic = wanted;
        var track = _music.MenuTrack;
        if (track < 0)
        {
            return;
        }

        if (wanted)
        {
            _music.Play(track);
        }
        else if (_music.Current == track)
        {
            _music.Stop();
        }
    }

    /// <summary>Frees the pictures of friends no longer listed.</summary>
    private void PruneAvatars(IReadOnlyList<FriendHost> hosts)
    {
        if (_avatars.Count <= hosts.Count)
        {
            return;
        }

        foreach (var friend in _avatars.Keys.ToList())
        {
            if (!hosts.Any(host => host.SteamId == friend))
            {
                _avatars[friend].Image.Dispose();
                _avatars.Remove(friend);
            }
        }
    }

    /// <summary>A friend's picture as an image, made once from its pixels.</summary>
    private SKImage? AvatarImage(ulong friend)
    {
        FriendAvatar? avatar = null;
        foreach (var host in _friends.Hosts)
        {
            if (host.SteamId == friend)
            {
                avatar = host.Avatar;
                break;
            }
        }

        if (avatar is null)
        {
            return null;
        }

        if (_avatars.TryGetValue(friend, out var known) && ReferenceEquals(known.Source, avatar))
        {
            return known.Image;
        }

        known.Image?.Dispose();
        var info = new SKImageInfo(avatar.Width, avatar.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var image = SKImage.FromPixelCopy(info, avatar.Rgba);
        if (image is null)
        {
            _avatars.Remove(friend);
            return null;
        }

        _avatars[friend] = (avatar, image);
        return image;
    }

    /// <summary>
    /// The engine's plain look at the menu's text size, with the menu's outlines (3.24 units), corners (12.96 units),
    /// and narrow label margins in its fixed-size buttons, as the menu drew them before the toolkit.
    /// </summary>
    private sealed class MenuLook(MenuStyle colours) : UiStyle(MenuLayer.BodyUnits, colours)
    {
        public override float OutlineUnits => 3.24f;

        public override float CornerUnits => 12.96f;

        public override UiBox DefaultBox(UiKind kind) =>
            kind == UiKind.Button
                ? base.DefaultBox(kind) with
                {
                    Padding = new UiEdges(BodyUnits / 4, 0),
                }
                : base.DefaultBox(kind);
    }
}
