using System.Globalization;
using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
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

    /// <summary>The main menu: play offline, host, join, settings, quit.</summary>
    Main,

    /// <summary>Joining a game: the friends who host one (through Steam), and the address box.</summary>
    Join,

    /// <summary>The settings.</summary>
    Settings,

    /// <summary>A client joins its host: how far, and cancel.</summary>
    Joining,

    /// <summary>A match's world starts.</summary>
    Starting,

    /// <summary>The menu over a match (Escape): resume, settings, leave the match, quit.</summary>
    InGame,

    /// <summary>A friend's "Join game" from Steam during a match: leave this match and join, or stay.</summary>
    Confirm,
}

/// <summary>
/// The menu: over the loading screen's ring when no match is played (the main menu, the
/// join and settings pages, a join's progress, a match's start), and over the match when Escape opens it or a friend's
/// "Join game" asks first. Its buttons ask <see cref="MatchControl"/> for matches (<see cref="MatchCommands"/>' starts)
/// and the window to close; the join page lists the friends who host (<see cref="SteamFriendList"/>). While it is open
/// it takes the pointer and the game gets no input (<see cref="IMenuInput"/>); it draws again only when what it shows
/// changes (a page, the button under the pointer, the text typed, a join's progress, the friends), and its update
/// allocates nothing otherwise. It clicks softly as the pointer comes onto a button, a switch, or a slider, and more
/// fully as a button or switch is pressed, or Enter joins from the address box (<see cref="EngineSounds.MenuHover"/> and <see cref="EngineSounds.MenuPress"/>, played at the listener). Its look is
/// the game's <see cref="MenuStyle"/>. Main thread.
/// </summary>
public sealed partial class MenuLayer : IUiLayer, IMenuInput, IDisposable
{
    /// <summary>The longest address that can be typed.</summary>
    public const int MaxAddressLength = 120;

    /// <summary>Friends' rows the join page shows at once; the wheel scrolls through more.</summary>
    public const int VisibleRows = 5;

    private readonly UiFonts _fonts;
    private readonly MenuStyle _style;
    private readonly MatchControl _matches;
    private readonly MatchCommands _starts;
    private readonly IFrameInput _input;
    private readonly WindowService _window;
    private readonly SteamService _steam;
    private readonly SteamFriendList _friends;
    private readonly PlayerSettings _settings;
    private readonly AudioService _audio;
    private readonly string _title;
    private readonly List<Element> _elements = [];
    private readonly Dictionary<ulong, (FriendAvatar Source, SKImage Image)> _avatars = [];
    private readonly TextEntry _addressEntry;
    private MenuView _page = MenuView.Main;
    private bool _inGame;
    private bool _inGameSettings;
    private bool _addressFocused;
    private (string Name, string Address)? _confirm;
    private IReadOnlyList<FriendHost> _rows = [];
    private int _rowScroll;
    private WheelSteps _rowWheel;

    /// <summary>How far the address's text is moved left in its box, in pixels, so its cursor shows.</summary>
    private float _addressScroll;
    private int _friendsVersion = -1;
    private int _settingsVersion = -1;
    private int _dragging = -1;
    private MenuView _laidOutView = MenuView.None;
    private int _laidOutWidth;
    private int _laidOutHeight;
    private int _width;
    private int _height;
    private int _hover = -1;
    private int _pressed = -1;
    private Vector2 _lastPointer;
    private bool _pointerSeen;
    private int _hoverSound = -1;
    private int _pressSound = -1;
    private int _seenStarted;
    private bool _seenMatch;
    private int _addressVersion = -1;
    private (JoinStage Stage, long Tenths) _joinShown = (JoinStage.Failed, -1);
    private string _status = "";
    private string? _notice;
    private string? _lastEnd;
    private bool _dirty = true;
    private SKFont? _font;
    private SKFont? _titleFont;
    private float _fontPixels;
    private SKPaint? _fill;
    private SKPaint? _stroke;
    private SKPaint? _text;
    private bool _disposed;

    public MenuLayer(
        UiFonts fonts,
        MenuStyle style,
        MatchControl matches,
        MatchCommands starts,
        IFrameInput input,
        WindowService window,
        SteamService steam,
        SteamFriendList friends,
        PlayerSettings settings,
        AudioService audio,
        IOptions<EngineOptions> options
    )
    {
        _settings = settings;
        _audio = audio;
        _fonts = fonts;
        _style = style;
        _matches = matches;
        _starts = starts;
        _input = input;
        _window = window;
        _steam = steam;
        _friends = friends;
        _title = options.Value.Application.Name;
        _addressEntry = new TextEntry(MaxAddressLength, PressJoinAddress);
        friends.JoinRequested += OnJoinRequested;
    }

    /// <summary>What the menu shows now: its page with no match, a match's join or start, or the menu over a match.</summary>
    public MenuView View =>
        _matches.Phase == MatchPhase.Menu ? _page
        : _matches.Phase == MatchPhase.Joining ? MenuView.Joining
        : _matches.Phase == MatchPhase.Starting ? MenuView.Starting
        : _confirm is not null ? MenuView.Confirm
        : _inGame ? (_inGameSettings ? MenuView.Settings : MenuView.InGame)
        : MenuView.None;

    public bool IsOpen => View != MenuView.None;

    public ITextInput? TextFocus => View == MenuView.Join && _addressFocused ? _addressEntry : null;

    /// <summary>Over the HUD; the console is drawn over it (registered after it).</summary>
    public bool AboveMatch => true;

    public bool Visible => IsOpen;

    public bool Dirty => _dirty;

    /// <summary>The address box's text, for tests.</summary>
    public TextLine Address => _addressEntry.Line;

    /// <summary>The labels of the buttons shown, in order, for tests.</summary>
    public IEnumerable<string> Buttons =>
        _elements.Where(e => e.Action is not MenuAction.AddressBox).Select(e => e.Label);

    public void Escape()
    {
        if (CapturingBinding)
        {
            // Waiting for a key to bind: Escape keeps the binding it had.
            StopBinding();
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
                _confirm = null;
                break;
            case MenuView.Settings when _matches.Phase != MatchPhase.Menu:
                _inGameSettings = false;
                _settings.Save();
                break;
            case MenuView.Settings:
                _page = MenuView.Main;
                _settings.Save();
                break;
            case MenuView.Join:
                _page = MenuView.Main;
                break;
            case MenuView.Joining:
                Activate(MenuAction.Cancel, -1);
                return;
            default:
                return;
        }

        _notice = null;
        _dirty = true;
    }

    public void Update(in FrameTime time)
    {
        FollowMatches();
        var view = View;
        // Steam's reports are taken in a match too, where a friend's "Join game" asks first.
        _friends.Update(time.TotalSeconds, view == MenuView.Join);
        if (view == MenuView.None)
        {
            // Opened again, the menu clicks for the pointer only once it moves.
            _pointerSeen = false;
            return;
        }

        if (view == MenuView.Join && _friends.Version != _friendsVersion)
        {
            _friendsVersion = _friends.Version;
            _laidOutView = MenuView.None;
        }

        if (view != _laidOutView || _width != _laidOutWidth || _height != _laidOutHeight)
        {
            Layout(view, _width, _height);
        }

        if (view == MenuView.Joining)
        {
            ShowJoin();
        }

        if (_addressEntry.Line.Version != _addressVersion)
        {
            _addressVersion = _addressEntry.Line.Version;
            _dirty = true;
        }

        FollowSettings();

        var pointer = _input.MenuPointer;
        if (view == MenuView.Join && pointer.Scroll != 0)
        {
            Scroll(pointer.Scroll);
        }
        else if (view == MenuView.Settings && _settingsTab == SettingsTab.Keybindings && pointer.Scroll != 0)
        {
            ScrollKeys(pointer.Scroll);
        }

        Point(in pointer);
    }

    public SKRectI Bounds(int width, int height) => new(0, 0, width, height);

    public void Draw(SKCanvas canvas, int width, int height)
    {
        _width = width;
        _height = height;
        var view = View;
        // A switch flipped in this frame's update shows its new state now.
        FollowSettings();
        if (view != _laidOutView || width != _laidOutWidth || height != _laidOutHeight)
        {
            Layout(view, width, height);
        }

        var unit = height / 100f;
        Paints(unit);
        if (_matches.Phase != MatchPhase.Menu && view is MenuView.InGame or MenuView.Settings or MenuView.Confirm)
        {
            // Over the match: the picture shaded, so the menu reads.
            _fill!.Color = _style.Shade;
            canvas.DrawRect(0, 0, width, height, _fill);
        }
        if (view is MenuView.Join or MenuView.Settings && _matches.Phase == MatchPhase.Menu)
        {
            // Over the ring: a panel under the list, so the rows read.
            _fill!.Color = _style.Shade;
            var keys = view == MenuView.Settings && _settingsTab == SettingsTab.Keybindings;
            var bottom = keys ? 90 : 86;
            // The Game and Graphics tabs' rows are wider than the rest (SettingRowUnits).
            var panelWidth = (view == MenuView.Settings && !keys ? SettingRowUnits + 8 : 72) * unit;
            var panel = new SKRect((width - panelWidth) / 2, 9 * unit, (width + panelWidth) / 2, bottom * unit);
            canvas.DrawRoundRect(panel, 1.5f * unit, 1.5f * unit, _fill);
        }

        DrawTitleAndLines(canvas, view, width, unit);
        for (var i = 0; i < _elements.Count; i++)
        {
            DrawElement(canvas, _elements[i], i == _hover, unit);
        }

        DrawHint(canvas, width, height, unit);
        // Last: a layout made here marks the menu changed, and it is drawn now.
        _dirty = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _friends.JoinRequested -= OnJoinRequested;
        foreach (var (_, image) in _avatars.Values)
        {
            image.Dispose();
        }

        _avatars.Clear();
        _font?.Dispose();
        _titleFont?.Dispose();
        _fill?.Dispose();
        _stroke?.Dispose();
        _text?.Dispose();
    }

    /// <summary>A setting or a binding changed: the page is laid out again (a switch's label) and drawn.</summary>
    private void FollowSettings()
    {
        if (_settings.Version != _settingsVersion)
        {
            _settingsVersion = _settings.Version;
            _laidOutView = MenuView.None;
            _dirty = true;
        }

        FollowKeys();
    }

    /// <summary>
    /// A match that started or ended changes the page: the menu over a match closes, an end shows the main menu, and a
    /// question about a friend's join is dropped.
    /// </summary>
    private void FollowMatches()
    {
        var hasMatch = _matches.Phase != MatchPhase.Menu;
        if (hasMatch == _seenMatch && _matches.Started == _seenStarted && _matches.LastEnd == _lastEnd)
        {
            return;
        }

        if (!hasMatch && _seenMatch)
        {
            _page = MenuView.Main;
        }

        _seenMatch = hasMatch;
        _seenStarted = _matches.Started;
        _lastEnd = _matches.LastEnd;
        _inGame = false;
        _inGameSettings = false;
        _confirm = null;
        _dirty = true;
    }

    /// <summary>
    /// A friend's "Join game" from Steam: with no match it joins at once; in a match it asks first.
    /// </summary>
    private void OnJoinRequested(string name, string address)
    {
        if (_matches.Phase == MatchPhase.Menu)
        {
            _matches.RequestStart(_starts.JoinStart(address));
            _page = MenuView.Main;
        }
        else
        {
            _confirm = (name.Length > 0 ? name : "a friend", address);
        }

        _dirty = true;
    }

    /// <summary>
    /// The pointer: the element under it, and a click (down and up on one element) activates that element. The pointer
    /// moving onto an element clicks softly; a page changing or the menu opening under a still pointer does not.
    /// </summary>
    private void Point(in UiPointer pointer)
    {
        var moved = _pointerSeen && pointer.Position != _lastPointer;
        _lastPointer = pointer.Position;
        _pointerSeen = true;
        if (_dragging >= 0)
        {
            // A slider follows the pointer while the button is held on it, also off its bar.
            Drag(_dragging, pointer.Position.X);
            if (!pointer.Down || pointer.Released)
            {
                _dragging = -1;
            }

            return;
        }

        var hover = -1;
        for (var i = 0; i < _elements.Count; i++)
        {
            if (_elements[i].Enabled && _elements[i].Rect.Contains(pointer.Position.X, pointer.Position.Y))
            {
                hover = i;
                break;
            }
        }

        if (hover != _hover)
        {
            if (hover >= 0 && moved && !IsAddressBox(hover))
            {
                Click(EngineSounds.MenuHover, ref _hoverSound);
            }

            _hover = hover;
            _dirty = true;
        }

        if (pointer.Pressed && hover >= 0 && _elements[hover].Action == MenuAction.Slider)
        {
            _dragging = _elements[hover].Row;
            Drag(_dragging, pointer.Position.X);
            return;
        }

        if (pointer.Pressed)
        {
            _pressed = hover;
            if (View == MenuView.Join && _addressFocused != (hover >= 0 && IsAddressBox(hover)))
            {
                // A click elsewhere takes the typing away from the box, one on it gives it back.
                _addressFocused = hover >= 0 && IsAddressBox(hover);
                _dirty = true;
            }
        }

        if (pointer.Released)
        {
            var pressed = _pressed;
            _pressed = -1;
            if (pressed >= 0 && pressed == hover)
            {
                if (!IsAddressBox(pressed))
                {
                    Click(EngineSounds.MenuPress, ref _pressSound);
                }

                Activate(_elements[pressed].Action, _elements[pressed].Row);
            }
        }
    }

    private bool IsAddressBox(int index) => _elements[index].Action == MenuAction.AddressBox;

    /// <summary>
    /// Plays the menu's click <paramref name="name"/> where the listener is. <paramref name="id"/> keeps its recipe's id
    /// once found; with no such recipe (the audio not started, as in tests) nothing plays.
    /// </summary>
    private void Click(string name, ref int id)
    {
        if (id < 0 && !_audio.Recipes.TryFind(name, out id))
        {
            id = -1;
            return;
        }

        _audio.Play(new SoundEvent { Recipe = id, AtListener = true });
    }

    /// <summary>Enter in the address box: it clicks as the Join button does, and joins.</summary>
    private void PressJoinAddress()
    {
        Click(EngineSounds.MenuPress, ref _pressSound);
        JoinAddress();
    }

    /// <summary>The first friend's row the join page shows (tests).</summary>
    internal int RowScroll => _rowScroll;

    /// <summary>How far the address's text is moved left in its box, in pixels, as last drawn (tests).</summary>
    internal float AddressScroll => _addressScroll;

    /// <summary>The address box as last laid out; empty when the page has none (tests).</summary>
    internal SKRect AddressBox
    {
        get
        {
            foreach (var element in _elements)
            {
                if (element.Action == MenuAction.AddressBox)
                {
                    return element.Rect;
                }
            }

            return SKRect.Empty;
        }
    }

    /// <summary>The wheel on the join page moves the friends' rows.</summary>
    private void Scroll(float steps)
    {
        var most = Math.Max(0, _rows.Count - VisibleRows);
        var scroll = Math.Clamp(_rowScroll - _rowWheel.Take(steps), 0, most);
        if (scroll != _rowScroll)
        {
            _rowScroll = scroll;
            _laidOutView = MenuView.None;
            _dirty = true;
        }
    }

    private void Activate(MenuAction action, int row)
    {
        _notice = null;
        _dirty = true;
        switch (action)
        {
            case MenuAction.PlayOffline:
                _matches.RequestStart(_starts.OfflineStart());
                break;
            case MenuAction.Host:
                _matches.RequestStart(_starts.HostStart());
                break;
            case MenuAction.OpenJoin:
                _page = MenuView.Join;
                // Typing goes to the address box at once; a click elsewhere takes it away.
                _addressFocused = true;
                _rowScroll = 0;
                _friends.MarkStale();
                break;
            case MenuAction.OpenSettings:
                _settingsTab = SettingsTab.Game;
                _bindAction = -1;
                _keyScroll = 0;
                if (_matches.Phase == MatchPhase.Menu)
                {
                    _page = MenuView.Settings;
                }
                else
                {
                    _inGameSettings = true;
                }

                break;
            case MenuAction.Back:
                _bindAction = -1;
                if (View == MenuView.Settings)
                {
                    _settings.Save();
                }

                if (_matches.Phase == MatchPhase.Menu)
                {
                    _page = MenuView.Main;
                }
                else
                {
                    _inGameSettings = false;
                }

                break;
            case MenuAction.Toggle:
                Toggle(row);
                break;
            case MenuAction.Tab:
                _settingsTab = (SettingsTab)row;
                _bindAction = -1;
                _laidOutView = MenuView.None;
                break;
            case MenuAction.Bind:
                StartBinding(row);
                break;
            case MenuAction.ResetKeys:
                _bindAction = -1;
                Keys.ResetAll();
                _laidOutView = MenuView.None;
                break;
            case MenuAction.JoinFriend:
                if (row >= 0 && row < _rows.Count)
                {
                    var lobby = _rows[row].Lobby.ToString(CultureInfo.InvariantCulture);
                    _matches.RequestStart(_starts.JoinStart(SteamAddress.LobbyPrefix + lobby));
                    _page = MenuView.Main;
                }

                break;
            case MenuAction.JoinAddress:
                JoinAddress();
                break;
            case MenuAction.AddressBox:
                _addressFocused = true;
                break;
            case MenuAction.Cancel:
                _matches.RequestLeave("The join was cancelled.");
                break;
            case MenuAction.Resume:
                _inGame = false;
                break;
            case MenuAction.Leave:
                _matches.RequestLeave("You left the match.");
                _inGame = false;
                break;
            case MenuAction.ConfirmJoin:
                if (_confirm is { } confirm)
                {
                    // The match now ends first (its save written), then the join starts.
                    _matches.RequestStart(_starts.JoinStart(confirm.Address));
                    _confirm = null;
                }

                break;
            case MenuAction.Stay:
                _confirm = null;
                break;
            case MenuAction.Quit:
                _window.RequestExit("Quit in the menu");
                break;
        }
    }

    /// <summary>Joins the address typed, or says why it cannot.</summary>
    private void JoinAddress()
    {
        var address = _addressEntry.Line.Text.Trim();
        if (!SessionAddress.TryParse(address, out _, out _) && !SteamAddress.TryParse(address, out _, out _))
        {
            _notice =
                address.Length == 0
                    ? "Type the host's address: host:port (or lobby:ID, steam:ID)."
                    : $"'{address}' is not host:port, lobby:ID, or steam:ID.";
            _dirty = true;
            return;
        }

        _matches.RequestStart(_starts.JoinStart(address));
        _page = MenuView.Main;
        _dirty = true;
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
        _dirty = true;
    }

    /// <summary>The elements of <paramref name="view"/> on a <paramref name="width"/> x <paramref name="height"/> surface.</summary>
    private void Layout(MenuView view, int width, int height)
    {
        _laidOutView = view;
        _laidOutWidth = width;
        _laidOutHeight = height;
        _elements.Clear();
        _hover = -1;
        _pressed = -1;
        _dirty = true;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var unit = height / 100f;
        var y = 36 * unit;
        switch (view)
        {
            case MenuView.Main:
                var steam = _steam.IsAvailable;
                Button(ref y, width, unit, "Play Offline", MenuAction.PlayOffline, true, "Plays alone.");
                Button(
                    ref y,
                    width,
                    unit,
                    "Host Game",
                    MenuAction.Host,
                    true,
                    steam
                        ? "Hosts through Steam: friends join from their Join Game."
                        : "Steam is not running: hosts on UDP; others join by address."
                );
                Button(ref y, width, unit, "Join Game", MenuAction.OpenJoin, true, "Joins a friend's game.");
                Button(
                    ref y,
                    width,
                    unit,
                    "Settings",
                    MenuAction.OpenSettings,
                    true,
                    "Sound, the mouse, the screen, the picture, and the keys."
                );
                Button(ref y, width, unit, "Quit", MenuAction.Quit, true, "Closes the game.");
                break;
            case MenuView.Join:
                LayoutJoin(width, unit);
                break;
            case MenuView.Settings:
                LayoutSettings(width, unit);
                break;
            case MenuView.Joining:
                y = 52 * unit;
                Button(ref y, width, unit, "Cancel", MenuAction.Cancel, true, "Stops joining.");
                break;
            case MenuView.InGame:
                Button(ref y, width, unit, "Resume", MenuAction.Resume, true, "Back to the game (Escape).");
                Button(
                    ref y,
                    width,
                    unit,
                    "Settings",
                    MenuAction.OpenSettings,
                    true,
                    "Sound, the mouse, the screen, the picture, and the keys."
                );
                Button(
                    ref y,
                    width,
                    unit,
                    "Leave Match",
                    MenuAction.Leave,
                    true,
                    "Ends this match and goes back to the main menu."
                );
                Button(ref y, width, unit, "Quit", MenuAction.Quit, true, "Closes the game.");
                break;
            case MenuView.Confirm:
                y = 52 * unit;
                Button(ref y, width, unit, "Join", MenuAction.ConfirmJoin, true, "Ends this match and joins.");
                Button(ref y, width, unit, "Stay", MenuAction.Stay, true, "Stays in this match (Escape).");
                break;
        }
    }

    /// <summary>
    /// The join page: a row for each friend hosting (up to <see cref="VisibleRows"/>, the wheel for more), each with its
    /// Join button, then the address box with Join and Back.
    /// </summary>
    private void LayoutJoin(int width, float unit)
    {
        _rows = _friends.Hosts;
        _rowScroll = Math.Clamp(_rowScroll, 0, Math.Max(0, _rows.Count - VisibleRows));
        PruneAvatars();
        var rowWidth = 64 * unit;
        var left = (width - rowWidth) / 2;
        var shown = Math.Min(VisibleRows, _rows.Count - _rowScroll);
        for (var i = 0; i < shown; i++)
        {
            var row = _rowScroll + i;
            var host = _rows[row];
            var top = (27 + i * 7.5f) * unit;
            var button = new SKRect(left + rowWidth - 15 * unit, top + 0.75f * unit, left + rowWidth, top + 6 * unit);
            _elements.Add(
                new Element(
                    button,
                    host.OtherVersion ? "Other version" : "Join",
                    MenuAction.JoinFriend,
                    !host.OtherVersion,
                    host.OtherVersion
                        ? $"{host.Name} plays another version of the game."
                        : $"Joins {host.Name}'s game through Steam.",
                    row
                )
            );
        }

        var y = 69 * unit;
        Field(ref y, width, unit);
        var w = 21 * unit;
        var h = 6 * unit;
        var gap = 2 * unit;
        _elements.Add(
            new Element(
                new SKRect(width / 2f - gap / 2 - w, y, width / 2f - gap / 2, y + h),
                "Join",
                MenuAction.JoinAddress,
                true,
                "Joins the address typed."
            )
        );
        _elements.Add(
            new Element(
                new SKRect(width / 2f + gap / 2, y, width / 2f + gap / 2 + w, y + h),
                "Back",
                MenuAction.Back,
                true,
                ""
            )
        );
    }

    /// <summary>Frees the pictures of friends no longer listed.</summary>
    private void PruneAvatars()
    {
        foreach (var friend in _avatars.Keys.ToList())
        {
            if (!_rows.Any(row => row.SteamId == friend))
            {
                _avatars[friend].Image.Dispose();
                _avatars.Remove(friend);
            }
        }
    }

    private void Button(ref float y, int width, float unit, string label, MenuAction action, bool enabled, string hint)
    {
        var w = 34 * unit;
        var h = 6 * unit;
        var left = (width - w) / 2;
        _elements.Add(new Element(new SKRect(left, y, left + w, y + h), label, action, enabled, hint));
        y += h + 1.5f * unit;
    }

    private void Field(ref float y, int width, float unit)
    {
        var w = 44 * unit;
        var h = 6 * unit;
        var left = (width - w) / 2;
        _elements.Add(
            new Element(
                new SKRect(left, y, left + w, y + h),
                "",
                MenuAction.AddressBox,
                true,
                "host:port over UDP, or lobby:ID or steam:ID through Steam."
            )
        );
        y += h + 2 * unit;
    }

    private void Paints(float unit)
    {
        var pixels = Math.Max(12, 3 * unit);
        if (_font is null || Math.Abs(pixels - _fontPixels) > 0.01f)
        {
            _fontPixels = pixels;
            _font?.Dispose();
            _titleFont?.Dispose();
            _font = new SKFont(_fonts.Mono, pixels);
            _titleFont = new SKFont(_fonts.Mono, Math.Max(20, 7.5f * unit)) { Embolden = true };
        }

        _fill ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        _stroke ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        _text ??= new SKPaint { IsAntialias = true };
        _stroke.StrokeWidth = Math.Max(1.5f, 0.3f * unit);
    }

    private void DrawTitleAndLines(SKCanvas canvas, MenuView view, int width, float unit)
    {
        var title = view switch
        {
            MenuView.Main => _title,
            MenuView.Join => "Join Game",
            MenuView.Settings => "Settings",
            MenuView.InGame => "Menu",
            _ => null,
        };
        if (title is not null)
        {
            _text!.Color = _style.Text;
            var top = view is MenuView.Join or MenuView.Settings ? 19 * unit : 30 * unit;
            canvas.DrawText(title, width / 2f, top, SKTextAlign.Center, _titleFont!, _text);
        }

        if (view == MenuView.Join)
        {
            DrawJoinPage(canvas, width, unit);
            return;
        }

        if (view == MenuView.Settings)
        {
            DrawSettingNames(canvas, width, unit);
            return;
        }

        string? line = view switch
        {
            MenuView.Joining => _status,
            MenuView.Starting => "Starting…",
            MenuView.Confirm when _confirm is { } confirm => $"Leave this match and join {confirm.Name}?",
            _ => null,
        };
        if (line is not null)
        {
            DrawLine(canvas, line, _style.Text, width, 46 * unit);
        }
    }

    /// <summary>The join page's lines and rows: why no friend is listed, else each friend's picture, name, and players.</summary>
    private void DrawJoinPage(SKCanvas canvas, int width, float unit)
    {
        var rowWidth = 64 * unit;
        var left = (width - rowWidth) / 2;
        var font = _font!;
        string? empty =
            !_friends.Available ? "Steam is not running: no friends to list."
            : _rows.Count == 0 ? "No friends are hosting a game now."
            : null;
        if (empty is not null)
        {
            DrawLine(canvas, empty, _style.TextDim, width, 36 * unit);
        }

        var shown = Math.Min(VisibleRows, _rows.Count - _rowScroll);
        for (var i = 0; i < shown; i++)
        {
            var host = _rows[_rowScroll + i];
            var top = (27 + i * 7.5f) * unit;
            var middle = top + 3.4f * unit;
            var baseline = middle - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
            var picture = new SKRect(left, top + 0.5f * unit, left + 5.8f * unit, top + 6.3f * unit);
            if (AvatarImage(host) is { } image)
            {
                canvas.DrawImage(image, picture, new SKSamplingOptions(SKFilterMode.Linear));
            }
            else
            {
                _fill!.Color = _style.Field;
                canvas.DrawRect(picture, _fill);
            }

            // The name up to the players' count, cut with an ellipsis where it is longer.
            var nameLeft = picture.Right + 1.5f * unit;
            var nameRight = left + rowWidth - 16.5f * unit;
            if (host.MaxPlayers > 0)
            {
                _text!.Color = _style.TextDim;
                var players = string.Create(CultureInfo.InvariantCulture, $"{host.Players}/{host.MaxPlayers}");
                canvas.DrawText(players, nameRight, baseline, SKTextAlign.Right, font, _text);
                nameRight -= font.MeasureText(players) + 1.5f * unit;
            }

            _text!.Color = _style.Text;
            var name = Fit(host.Name, nameRight - nameLeft, font);
            canvas.DrawText(name, nameLeft, baseline, SKTextAlign.Left, font, _text);
        }

        if (_rows.Count > VisibleRows)
        {
            var more = string.Create(
                CultureInfo.InvariantCulture,
                $"{_rowScroll + 1}-{_rowScroll + shown} of {_rows.Count} (wheel)"
            );
            DrawLine(canvas, more, _style.TextDim, width, 65 * unit);
        }

        _text!.Color = _style.Text;
        canvas.DrawText("Or type a host's address:", width / 2f, 67.5f * unit, SKTextAlign.Center, font, _text);
    }

    /// <summary>A friend's picture as an image, made once from its pixels.</summary>
    private SKImage? AvatarImage(FriendHost host)
    {
        if (host.Avatar is not { } avatar)
        {
            return null;
        }

        if (_avatars.TryGetValue(host.SteamId, out var known) && ReferenceEquals(known.Source, avatar))
        {
            return known.Image;
        }

        known.Image?.Dispose();
        var info = new SKImageInfo(avatar.Width, avatar.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var image = SKImage.FromPixelCopy(info, avatar.Rgba);
        if (image is null)
        {
            _avatars.Remove(host.SteamId);
            return null;
        }

        _avatars[host.SteamId] = (avatar, image);
        return image;
    }

    private void DrawElement(SKCanvas canvas, Element element, bool hover, float unit)
    {
        var corner = 1.2f * unit;
        if (element.Action == MenuAction.AddressBox)
        {
            _fill!.Color = _style.Field;
            canvas.DrawRoundRect(element.Rect, corner, corner, _fill);
            _stroke!.Color = _addressFocused ? _style.ButtonHover : _style.Ink;
            canvas.DrawRoundRect(element.Rect, corner, corner, _stroke);
            DrawAddress(canvas, element.Rect, unit);
            return;
        }

        if (element.Action == MenuAction.Slider)
        {
            // The bar, filled to the setting's share of its range.
            _fill!.Color = _style.Field;
            canvas.DrawRoundRect(element.Rect, corner, corner, _fill);
            var (least, most) = SliderRange(element.Row);
            var share = Math.Clamp((SliderValue(element.Row) - least) / (most - least), 0, 1);
            var filled = new SKRect(
                element.Rect.Left,
                element.Rect.Top,
                element.Rect.Left + share * element.Rect.Width,
                element.Rect.Bottom
            );
            _fill.Color = hover || _dragging == element.Row ? _style.ButtonHover : _style.Button;
            canvas.DrawRoundRect(filled, corner, corner, _fill);
            _stroke!.Color = _style.Ink;
            canvas.DrawRoundRect(element.Rect, corner, corner, _stroke);
            return;
        }

        // The tab shown is drawn sunk, as a text box is.
        _fill!.Color =
            !element.Enabled ? _style.ButtonDisabled
            : hover ? _style.ButtonHover
            : element.Selected ? _style.Field
            : _style.Button;
        canvas.DrawRoundRect(element.Rect, corner, corner, _fill);
        _stroke!.Color = _style.Ink;
        canvas.DrawRoundRect(element.Rect, corner, corner, _stroke);
        _text!.Color =
            !element.Enabled ? _style.TextDim
            : hover ? _style.ButtonTextHover
            : element.Selected ? _style.Text
            : _style.ButtonText;
        var baseline = element.Rect.MidY - (_font!.Metrics.Ascent + _font.Metrics.Descent) / 2;
        canvas.DrawText(element.Label, element.Rect.MidX, baseline, SKTextAlign.Center, _font, _text);
    }

    /// <summary>
    /// The address within its box: clipped to it, and moved left as the cursor goes past the box's right side, so the
    /// cursor always shows (a long address would otherwise be drawn over the page).
    /// </summary>
    private void DrawAddress(SKCanvas canvas, SKRect box, float unit)
    {
        var font = _font!;
        var left = box.Left + 1.5f * unit;
        var room = box.Width - 3 * unit;
        var baseline = box.MidY - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
        var text = _addressEntry.Line.Text;
        if (text.Length == 0 && !_addressFocused)
        {
            _addressScroll = 0;
            _text!.Color = _style.TextDim;
            canvas.DrawText("127.0.0.1:47600", left, baseline, SKTextAlign.Left, font, _text);
            return;
        }

        var cursor = font.MeasureText(text.AsSpan(0, _addressEntry.Line.Cursor));
        _addressScroll = ScrollToCursor(_addressScroll, cursor, font.MeasureText(text), room);
        canvas.Save();
        canvas.ClipRect(new SKRect(box.Left + 0.5f * unit, box.Top, box.Right - 0.5f * unit, box.Bottom));
        _text!.Color = _style.Text;
        canvas.DrawText(text, left - _addressScroll, baseline, SKTextAlign.Left, font, _text);
        if (_addressFocused)
        {
            var x = left + cursor - _addressScroll;
            _stroke!.Color = _style.Text;
            canvas.DrawLine(x, box.Top + 1.2f * unit, x, box.Bottom - 1.2f * unit, _stroke);
        }

        canvas.Restore();
    }

    /// <summary>
    /// How far a line of text of <paramref name="width"/> is moved left in a box with <paramref name="room"/> for it, from
    /// <paramref name="scroll"/>, so the cursor at <paramref name="cursor"/> (both from the text's start) shows: only as
    /// far as it must, and never so far that room is left empty after the text's end.
    /// </summary>
    internal static float ScrollToCursor(float scroll, float cursor, float width, float room)
    {
        if (cursor - scroll > room)
        {
            scroll = cursor - room;
        }

        if (cursor < scroll)
        {
            scroll = cursor;
        }

        return Math.Clamp(scroll, 0, Math.Max(0, width - room));
    }

    /// <summary>
    /// <paramref name="text"/> when it fits <paramref name="room"/> in <paramref name="font"/>, else its longest start that
    /// fits with an ellipsis after it (a friend's long name).
    /// </summary>
    internal static string Fit(string text, float room, SKFont font)
    {
        if (font.MeasureText(text) <= room)
        {
            return text;
        }

        const string ellipsis = "…";
        var fits = 0;
        var most = text.Length;
        while (fits < most)
        {
            var middle = (fits + most + 1) / 2;
            if (font.MeasureText(string.Concat(text.AsSpan(0, middle), ellipsis)) <= room)
            {
                fits = middle;
            }
            else
            {
                most = middle - 1;
            }
        }

        return fits > 0 || font.MeasureText(ellipsis) <= room ? string.Concat(text.AsSpan(0, fits), ellipsis) : "";
    }

    /// <summary>
    /// The band at the bottom: a notice, else what the button under the pointer does; over it on the main menu, why the
    /// last match ended.
    /// </summary>
    private void DrawHint(SKCanvas canvas, int width, int height, float unit)
    {
        var top = height - 7 * unit;
        if (View == MenuView.Main && _lastEnd is not null)
        {
            DrawLine(canvas, _lastEnd, _style.Warning, width, top - 2.5f * unit);
        }

        var (text, colour) =
            _notice is not null ? (_notice, _style.Warning)
            : CapturingBinding ? (_bindHint, _style.Text)
            : _hover >= 0 && _elements[_hover].Hint.Length > 0 ? (_elements[_hover].Hint, _style.TextDim)
            : (null, default);
        if (text is null)
        {
            return;
        }

        _fill!.Color = _style.Shade;
        canvas.DrawRect(0, top, width, 7 * unit, _fill);
        DrawLine(canvas, text, colour, width, top + 4.4f * unit);
    }

    /// <summary>A line of text centred on <paramref name="baseline"/>; one wider than the window is drawn smaller, to fit.</summary>
    private void DrawLine(SKCanvas canvas, string text, SKColor colour, int width, float baseline)
    {
        _text!.Color = colour;
        var fit = Math.Min(1, 0.96f * width / Math.Max(1, _font!.MeasureText(text)));
        canvas.Save();
        canvas.Scale(fit, fit, width / 2f, baseline);
        canvas.DrawText(text, width / 2f, baseline, SKTextAlign.Center, _font, _text);
        canvas.Restore();
    }

    /// <summary>One thing the menu shows that the pointer can click; <paramref name="Row"/> names a friend's row.</summary>
    private sealed record Element(
        SKRect Rect,
        string Label,
        MenuAction Action,
        bool Enabled,
        string Hint,
        int Row = -1,
        bool Selected = false
    );

    /// <summary>What an element does.</summary>
    private enum MenuAction
    {
        PlayOffline,
        Host,
        OpenJoin,
        OpenSettings,
        Quit,
        Back,
        JoinFriend,
        JoinAddress,
        AddressBox,
        Cancel,
        Resume,
        Leave,
        ConfirmJoin,
        Stay,
        Slider,
        Toggle,
        Tab,
        Bind,
        ResetKeys,
    }
}
