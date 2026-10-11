using System.Numerics;
using System.Text;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// The menu drawn on a 1280 x 720 surface with a pointer the test moves, its elements found by what they read: the main
/// menu's buttons and the matches they ask for, the join page's address box, the settings page's sliders and switches
/// (the UI scale's too), the menu over a match (Escape, resume, leave), a join's cancel, Escape on each page, a game's
/// own page and Settings rows, the keyboard's focus, the clicks (soft as the pointer comes onto a button, a switch, or a
/// slider; fuller as one is pressed; none for a page or the menu opening under a still pointer, the address box, or a
/// slider's drag), and that it draws again only when what it shows changes and allocates nothing while idle or clicking.
/// </summary>
public sealed class MenuLayerTests : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;

    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly MatchControl _matches = new();
    private readonly AudioService _audio = QueuingAudio();
    private readonly MenuPages _pages = new();
    private readonly SettingsRows _rows = new();

    /// <summary>The player's settings, with no file.</summary>
    private PlayerSettings Settings { get; } =
        new(new EngineOptions(), CommandLineKeys.None, null, NullLogger<PlayerSettings>.Instance);
    private readonly PointerInput _pointer = new();
    private readonly UiToolkit _toolkit;
    private readonly MenuLayer _menu;
    private readonly UISurface _surface;
    private ulong _frame;

    public MenuLayerTests()
    {
        var commands = new ConsoleCommands(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
        var steam = new SteamService(
            new SteamSettings { Enabled = false },
            NullLogger<SteamService>.Instance,
            () => (null, "off")
        );
        steam.Initialize();
        var options = new EngineOptions();
        options.Application.Name = "Sample Game";
        options.Voxels.Seed = 4;
        _toolkit = new UiToolkit(
            _fonts,
            MenuStyle.Plain,
            Options.Create(options),
            NullLogger<UiToolkit>.Instance,
            _audio
        );
        var starts = new MatchCommands(commands, _matches, steam, Options.Create(options));
        var friends = new SteamFriendList(steam, "test", NullLogger<SteamFriendList>.Instance);
        _menu = new MenuLayer(
            _toolkit,
            MenuStyle.Plain,
            _matches,
            starts,
            _pointer,
            _window,
            steam,
            friends,
            new JoinRequests(friends, _matches, starts),
            Settings,
            Options.Create(options),
            _pages,
            _rows
        );
        _surface = new UISurface([_menu], NullLogger<UISurface>.Instance);
        _surface.Initialize(Width, Height);
        Frame();
    }

    public void Dispose()
    {
        _surface.Dispose();
        _menu.Dispose();
        _toolkit.Dispose();
        _fonts.Dispose();
        _window.Dispose();
        _audio.Dispose();
    }

    /// <summary>An audio service with the engine's recipes that keeps what is played, with no device or thread.</summary>
    internal static AudioService QueuingAudio()
    {
        var options = new EngineOptions();
        options.Audio.Cache = false;
        var audio = new AudioService(Options.Create(options), NullLogger<AudioService>.Instance)
        {
            QueueWithoutThread = true,
        };
        audio.Initialize(new NullAudioDevice());
        return audio;
    }

    [Fact]
    public void With_no_match_the_main_menu_shows_and_play_offline_starts_an_offline_match()
    {
        Assert.Equal(MenuView.Main, _menu.View);
        Assert.True(_menu.IsOpen);
        Assert.Equal(["Play Offline", "Host Game", "Join Game", "Settings", "Quit"], _menu.Buttons);

        Click("Play Offline");

        Assert.Equal(
            new MatchStart(SessionMode.Offline, SessionTransportKind.Direct, null, 4, null, false),
            _matches.TakeStart()
        );
    }

    [Fact]
    public void Host_game_without_steam_hosts_over_udp_and_quit_closes_the_window()
    {
        Click("Host Game");
        var host = _matches.TakeStart()!;
        Assert.Equal(SessionMode.Host, host.Mode);
        Assert.Equal(SessionTransportKind.Direct, host.Transport);

        Click("Quit");
        Assert.True(_window.ExitRequested);
    }

    [Fact]
    public void The_join_page_joins_the_address_typed_and_says_why_a_bad_one_cannot()
    {
        Click("Join Game");
        Assert.Equal(MenuView.Join, _menu.View);
        var box = Assert.IsAssignableFrom<ITextInput>(_menu.TextFocus);

        Type(box, "nowhere");
        box.KeyDown(Key.Enter, false);
        Frame();
        Assert.Null(_matches.TakeStart());
        Assert.Equal(MenuView.Join, _menu.View);
        Assert.True(_menu.Ui.TryFind("'nowhere' is not host:port, lobby:ID, or steam:ID.", out _));

        box.KeyDown(Key.U, true);
        Type(box, "127.0.0.1:47600");
        box.KeyDown(Key.Enter, false);
        var join = _matches.TakeStart()!;
        Assert.Equal(SessionMode.Join, join.Mode);
        Assert.Equal("127.0.0.1:47600", join.Address);
        Frame();
        Assert.Equal(MenuView.Main, _menu.View);
    }

    /// <summary>The longest address stays within its box: nothing drawn past its sides.</summary>
    [Fact]
    public void A_long_address_stays_within_its_box()
    {
        Click("Join Game");
        Frame();
        var box = _menu.AddressBox;
        Assert.False(box.IsEmpty);
        var before = Column((int)box.Right + 3, box);
        var beforeLeft = Column((int)box.Left - 3, box);

        var focus = Assert.IsAssignableFrom<ITextInput>(_menu.TextFocus);
        for (var i = 0; i < MenuLayer.MaxAddressLength; i++)
        {
            focus.Character(new Rune('W'));
        }

        Frame();

        Assert.Equal(MenuLayer.MaxAddressLength, _menu.Address.Text.Length);
        Assert.Equal(before, Column((int)box.Right + 3, box));
        Assert.Equal(beforeLeft, Column((int)box.Left - 3, box));
    }

    [Fact]
    public void Escape_goes_back_from_the_join_page_and_does_nothing_on_the_main_menu()
    {
        Click("Join Game");
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.Main, _menu.View);
        Assert.Null(_menu.TextFocus);

        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.Main, _menu.View);
    }

    [Fact]
    public void In_a_match_escape_opens_the_menu_over_it_resume_closes_it_and_leave_ends_the_match()
    {
        _matches.Phase = MatchPhase.Playing;
        Frame();
        Assert.Equal(MenuView.None, _menu.View);
        Assert.False(_menu.IsOpen);

        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.InGame, _menu.View);
        Assert.Equal(["Resume", "Settings", "Leave Match", "Quit"], _menu.Buttons);
        // The picture shaded under the page: the shade is drawn first.
        Assert.Equal(Ui.Id("menu.shade"), _menu.Ui.Drawn(0).Id);

        Click("Resume");
        Assert.Equal(MenuView.None, _menu.View);

        _menu.Escape();
        Frame();
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.None, _menu.View);

        _menu.Escape();
        Frame();
        Click("Leave Match");
        Assert.Equal("You left the match.", _matches.TakeLeave());

        // The match ended: the main menu, with the reason.
        _matches.Phase = MatchPhase.Menu;
        _matches.LastEnd = "You left the match.";
        Frame();
        Assert.Equal(MenuView.Main, _menu.View);
        Assert.True(_menu.Ui.TryFind("You left the match.", out _));
    }

    [Fact]
    public void Escape_during_a_matchs_loading_screen_opens_the_menu_over_it()
    {
        _matches.Phase = MatchPhase.Loading;
        Frame();
        Assert.Equal(MenuView.None, _menu.View);

        _menu.Escape();
        Frame();

        Assert.Equal(MenuView.InGame, _menu.View);
        Assert.Contains("Leave Match", _menu.Buttons);
    }

    [Fact]
    public void A_match_that_started_and_failed_before_a_frame_saw_it_leaves_the_main_page_showing()
    {
        Click("Settings");
        Assert.Equal(MenuView.Settings, _menu.View);

        // A friend's join started and failed within one iteration: the match counted, and back at the menu.
        _matches.Started++;
        _matches.LastEnd = "The host refused the join.";
        Frame();

        Assert.Equal(MenuView.Main, _menu.View);
    }

    [Fact]
    public void The_settings_page_moves_a_setting_with_its_slider_and_flips_its_switches_on_each_tab()
    {
        Click("Settings");
        Assert.Equal(MenuView.Settings, _menu.View);
        Assert.Equal(SettingsRows.GameTab, _menu.SettingsTab);
        Assert.Equal(["Game", "Graphics", "Keybindings", "Back"], _menu.Buttons);
        // Game: volume, sensitivity, field of view, UI scale; VSync and full screen.
        Assert.Equal(4, Count(UiKind.Slider));
        Assert.Equal([true, false], Switches());

        // The volume's bar: down at its middle, dragged past its right end, let go.
        var volume = Element(UiKind.Slider, 0).Rect;
        Hold(new Vector2(volume.MidX, volume.MidY), pressed: true);
        Assert.Equal(0.5f, Settings.Volume, 2);
        Hold(new Vector2(volume.Right + 200, volume.MidY), pressed: false);
        Assert.Equal(1f, Settings.Volume, 2);
        Release(new Vector2(volume.Right + 200, volume.MidY));

        // The UI scale: its bar's right end is 150 %, and the toolkit takes it at once.
        var scale = Element(UiKind.Slider, 3).Rect;
        Hold(new Vector2(scale.Right - 1, scale.MidY), pressed: true);
        Release(new Vector2(scale.Right - 1, scale.MidY));
        Assert.Equal(PlayerSettings.MostUiScale, Settings.UiScale);

        ClickAt(Centre(Element(UiKind.Switch, 0).Rect));
        Assert.False(Settings.VSync);
        Assert.Equal([false, false], Switches());

        // Graphics: dynamic render scaling and its threshold, shadows, anti-aliasing. The threshold's slider steps
        // through the offered rates: at its left end the first, 30, past its right end the last, 360.
        Click("Graphics");
        Assert.Equal(SettingsRows.GraphicsTab, _menu.SettingsTab);
        Assert.Equal([true, true], Switches());
        Assert.Equal(120, Settings.ScalingThreshold);
        var threshold = Element(UiKind.Slider, 0).Rect;
        Hold(new Vector2(threshold.Left + 1, threshold.MidY), pressed: true);
        Assert.Equal(30, Settings.ScalingThreshold);
        Hold(new Vector2(threshold.Right + 200, threshold.MidY), pressed: false);
        Assert.Equal(360, Settings.ScalingThreshold);
        Release(new Vector2(threshold.Right + 200, threshold.MidY));

        // Anti-aliasing's slider steps through Off, SMAA, MSAA 2x, 4x (the default), and 8x.
        Assert.Equal(AntiAliasMode.Msaa4, Settings.AntiAliasMode);
        var antiAliasing = Element(UiKind.Slider, 1).Rect;
        Hold(new Vector2(antiAliasing.Left + 1, antiAliasing.MidY), pressed: true);
        Assert.Equal(AntiAliasMode.Off, Settings.AntiAliasMode);
        Assert.Equal(1, Settings.MsaaSamples);
        Hold(new Vector2(antiAliasing.Right + 200, antiAliasing.MidY), pressed: false);
        Assert.Equal(AntiAliasMode.Msaa8, Settings.AntiAliasMode);
        Assert.Equal(8, Settings.MsaaSamples);
        Release(new Vector2(antiAliasing.Right + 200, antiAliasing.MidY));

        // Each switch flips its setting and shows it.
        ClickAt(Centre(Element(UiKind.Switch, 0).Rect));
        ClickAt(Centre(Element(UiKind.Switch, 1).Rect));
        Assert.False(Settings.DynamicScaling);
        Assert.False(Settings.Shadows);
        Assert.Equal([false, false], Switches());

        Click("Keybindings");
        Assert.Equal(SettingsRows.KeysTab, _menu.SettingsTab);

        // Escape leaves the page; it opens on Game again.
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.Main, _menu.View);
        Click("Settings");
        Assert.Equal(SettingsRows.GameTab, _menu.SettingsTab);
    }

    [Fact]
    public void The_keybindings_tab_binds_through_clicks_unbinds_a_clash_scrolls_and_resets()
    {
        var keys = Settings.Keys;
        var jump = keys.Register("jump", "Jump", "Jumps.", InputBinding.Of(Key.Space));
        var crouch = keys.Register("crouch", "Crouch", "Crouches.", InputBinding.Of(Key.C));
        for (var i = 0; i < 6; i++)
        {
            keys.Register($"extra{i}", $"Extra {i}", "", InputBinding.Of(Key.F2 + i));
        }

        Click("Settings");
        Click("Keybindings");
        // Menu, Console, and Capture the pointer are fixed: no button. Then Stats line, Jump, Crouch, Extra 0 to 5.
        Assert.Equal(
            [
                "Game",
                "Graphics",
                "Keybindings",
                "F1",
                "Space",
                "C",
                "F2",
                "F3",
                "F4",
                "F5",
                "F6",
                "F7",
                "Reset keys",
                "Back",
            ],
            _menu.Buttons
        );

        // Jump's button waits for a key; C takes it from Crouch, whose row shows None.
        Click("Space");
        Assert.True(_menu.CapturingBinding);
        Assert.Contains("Press a key…", _menu.Buttons);
        _audio.ClearQueued();
        _menu.BindingPressed(InputBinding.Of(Key.C));
        Frame();
        Assert.False(_menu.CapturingBinding);
        Assert.Equal(InputBinding.Of(Key.C), keys[jump]);
        Assert.Equal(InputBinding.None, keys[crouch]);
        Assert.Contains("None", _menu.Buttons);
        // Setting a binding makes no click.
        Assert.Equal(0, Clicks(EngineSounds.MenuPress));

        // Escape and ` stay the menu's and the console's; Escape stops the wait and keeps the binding, on the page.
        Click("C");
        _menu.BindingPressed(InputBinding.Of(Key.GraveAccent));
        Assert.True(_menu.CapturingBinding);
        _menu.Escape();
        Frame();
        Assert.False(_menu.CapturingBinding);
        Assert.Equal(MenuView.Settings, _menu.View);
        Assert.Equal(InputBinding.Of(Key.C), keys[jump]);

        // The wheel over the list brings its last rows into view.
        var last = Find("F7").Rect;
        _pointer.Pointer = new UiPointer(Centre(Find("F1").Rect), false, false, false, -10);
        Frame();
        Frame();
        Assert.True(Find("F7").Rect.Top < last.Top, $"{Find("F7").Rect.Top} against {last.Top}");

        // Reset keys puts them back; Back leaves the page.
        Click("Reset keys");
        Assert.Equal(InputBinding.Of(Key.Space), keys[jump]);
        Assert.Equal(InputBinding.Of(Key.C), keys[crouch]);
        Click("Back");
        Assert.Equal(MenuView.Main, _menu.View);
    }

    [Fact]
    public void While_joining_it_shows_the_join_and_cancel_leaves()
    {
        _matches.Phase = MatchPhase.Joining;
        _matches.JoinAddress = "127.0.0.1:47600";
        _matches.JoinProgress = new JoinProgress(JoinStage.Downloading, 1 << 20, 3 << 20, null);
        Frame();

        Assert.Equal(MenuView.Joining, _menu.View);
        Assert.Equal(["Cancel"], _menu.Buttons);
        Assert.True(_menu.Ui.TryFind("Downloading the host's world: 1.0 of 3.0 MB", out _));
        Click("Cancel");
        Assert.Equal("The join was cancelled.", _matches.TakeLeave());
    }

    [Fact]
    public void A_games_page_opens_from_its_button_and_its_switch_rows_show_on_their_tabs()
    {
        var page = new CreditsPage();
        _pages.Add("Credits", "Who made the game.", page);
        var minimap = Settings.RegisterSwitch("minimap", true);
        var hud = Settings.RegisterSwitch("hud", false);
        _rows.AddSwitch(minimap, "Minimap", "The map in the corner.");
        _rows.AddSwitch(hud, "Big HUD", "Larger readouts.", "Display");
        Frame();

        Assert.Equal(["Play Offline", "Host Game", "Join Game", "Credits", "Settings", "Quit"], _menu.Buttons);
        Click("Credits");
        Assert.Equal(MenuView.Page, _menu.View);
        Assert.True(_menu.Ui.TryFind("Made by the test.", out _));
        // The page takes its first Escape; the next goes back.
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.Page, _menu.View);
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.Main, _menu.View);

        Click("Settings");
        Assert.True(_menu.Ui.TryFind("Minimap", out _));
        ClickAt(Centre(Element(UiKind.Switch, 2).Rect));
        Assert.False(Settings.IsOn(minimap));
        Assert.Equal(["Game", "Graphics", "Display", "Keybindings", "Back"], _menu.Buttons);
        Click("Display");
        ClickAt(Centre(Element(UiKind.Switch, 0).Rect));
        Assert.True(Settings.IsOn(hud));
    }

    [Fact]
    public void The_keyboard_moves_the_focus_through_the_buttons_and_presses_one()
    {
        _pointer.Keys = UiKeys.Next;
        Frame();
        _pointer.Keys = UiKeys.Next;
        Frame();
        _pointer.Keys = UiKeys.Press;
        Frame();
        _pointer.Keys = UiKeys.None;

        Assert.Equal(SessionMode.Host, _matches.TakeStart()!.Mode);
    }

    [Fact]
    public void The_pointer_coming_onto_a_button_clicks_softly_and_a_press_clicks_fully()
    {
        Point(new Vector2(10, 10));
        Frame();
        Assert.Equal(0, Clicks(EngineSounds.MenuHover));

        Point(Centre(Find("Play Offline").Rect));
        Frame();
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
        Assert.True(_audio.QueuedEvents[0].AtListener);

        // Moving on the same button, or off it into nothing, clicks no more; onto the next one does.
        Point(Centre(Find("Play Offline").Rect) + new Vector2(5, 2));
        Frame();
        Point(new Vector2(10, 10));
        Frame();
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
        Point(Centre(Find("Host Game").Rect));
        Frame();
        Assert.Equal(2, Clicks(EngineSounds.MenuHover));
        Assert.Equal(0, Clicks(EngineSounds.MenuPress));

        // Down and up on it: one fuller click, as the button acts.
        Click("Host Game");
        Assert.Equal(1, Clicks(EngineSounds.MenuPress));
        Assert.Equal(2, Clicks(EngineSounds.MenuHover));
        Assert.NotNull(_matches.TakeStart());
    }

    [Fact]
    public void A_page_or_the_menu_opening_under_a_still_pointer_makes_no_soft_click()
    {
        // Join Game opens its page under the pointer: no click for what now lies under it.
        Point(Centre(Find("Join Game").Rect));
        Frame();
        _audio.ClearQueued();
        Click("Join Game");
        Frame();
        Assert.Equal(MenuView.Join, _menu.View);
        Assert.Equal(0, Clicks(EngineSounds.MenuHover));
        Assert.Equal(1, Clicks(EngineSounds.MenuPress));

        // Over a match, Escape opens the menu with the pointer resting on a button: no click until it moves.
        _menu.Escape();
        _matches.Phase = MatchPhase.Playing;
        Frame();
        _menu.Escape();
        Frame();
        var resume = Centre(Find("Resume").Rect);
        _menu.Escape();
        Frame();
        _audio.ClearQueued();
        Point(resume);
        Frame();
        _menu.Escape();
        Frame();
        Frame();
        Assert.Equal(MenuView.InGame, _menu.View);
        Assert.Equal(0, Clicks(EngineSounds.MenuHover));
        Point(Centre(Find("Settings").Rect));
        Frame();
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
    }

    [Fact]
    public void The_address_box_clicks_only_as_enter_joins_and_a_sliders_drag_presses_nothing()
    {
        Click("Join Game");
        _audio.ClearQueued();
        // Onto the address box and pressing it: no click.
        ClickAt(Centre(_menu.AddressBox));
        Assert.Equal(0, _audio.QueuedEvents.Length);
        var box = Assert.IsAssignableFrom<ITextInput>(_menu.TextFocus);
        Type(box, "127.0.0.1:47600");
        box.KeyDown(Key.Enter, false);
        Assert.Equal(1, Clicks(EngineSounds.MenuPress));
        Assert.NotNull(_matches.TakeStart());
        Frame();

        Click("Settings");
        _audio.ClearQueued();
        // The volume's bar: onto it clicks softly; held, dragged, and let go, it presses nothing.
        var bar = Element(UiKind.Slider, 0).Rect;
        Point(new Vector2(bar.Left + 5, bar.MidY));
        Frame();
        Point(Centre(bar));
        Frame();
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
        Hold(Centre(bar), pressed: true);
        Hold(Centre(bar) + new Vector2(50, 0), pressed: false);
        Release(Centre(bar) + new Vector2(50, 0));
        Assert.Equal(0, Clicks(EngineSounds.MenuPress));
    }

    [Fact]
    public void It_draws_again_only_when_what_it_shows_changes_and_allocates_nothing_while_idle()
    {
        Point(new Vector2(10, 10));
        Frame();
        Assert.Equal(0, Frame().Rows);

        // The pointer onto a button: drawn again (the button under it), then not until it moves off.
        Point(Centre(Find("Host Game").Rect));
        Assert.True(Frame().Rows > 0);
        Assert.Equal(0, Frame().Rows);

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            _menu.Update(new FrameTime(1 / 60f, i / 60.0, (ulong)i, 0), Width, Height);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);

        // The pointer going between two buttons, a click each time: nothing allocated either.
        var play = Centre(Find("Play Offline").Rect);
        var host = Centre(Find("Host Game").Rect);
        _audio.ClearQueued();
        start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            Point(i % 2 == 0 ? play : host);
            _menu.Update(new FrameTime(1 / 60f, i / 60.0, (ulong)i, 0), Width, Height);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.Equal(10, Clicks(EngineSounds.MenuHover));
    }

    /// <summary>The clicks of recipe <paramref name="name"/> queued so far.</summary>
    private int Clicks(string name)
    {
        Assert.True(_audio.Recipes.TryFind(name, out var id));
        var count = 0;
        foreach (var sound in _audio.QueuedEvents)
        {
            count += sound.Recipe == id ? 1 : 0;
        }

        return count;
    }

    private UiElement Find(string label)
    {
        Assert.True(_menu.Ui.TryFind(label, out var element), $"Nothing reads '{label}':\n{_menu.Ui.Dump()}");
        return element;
    }

    /// <summary>The <paramref name="nth"/> element of <paramref name="kind"/> in the order declared.</summary>
    private UiElement Element(UiKind kind, int nth)
    {
        for (var i = 0; i < _menu.Ui.Count; i++)
        {
            var element = _menu.Ui.ElementAt(i);
            if (element.Kind == kind && nth-- == 0)
            {
                return element;
            }
        }

        throw new InvalidOperationException($"No {kind} {nth}:\n{_menu.Ui.Dump()}");
    }

    private int Count(UiKind kind)
    {
        var count = 0;
        for (var i = 0; i < _menu.Ui.Count; i++)
        {
            count += _menu.Ui.ElementAt(i).Kind == kind ? 1 : 0;
        }

        return count;
    }

    /// <summary>The switches shown, on or off, in order.</summary>
    private List<bool> Switches()
    {
        var switches = new List<bool>();
        for (var i = 0; i < _menu.Ui.Count; i++)
        {
            if (_menu.Ui.ElementAt(i) is { Kind: UiKind.Switch } element)
            {
                switches.Add(element.On);
            }
        }

        return switches;
    }

    private static Vector2 Centre(SKRect rect) => new(rect.MidX, rect.MidY);

    private static void Type(ITextInput box, string text)
    {
        foreach (var character in text)
        {
            box.Character(new Rune(character));
        }
    }

    private void Point(Vector2 at) => _pointer.Pointer = new UiPointer(at, false, false, false, 0);

    /// <summary>The button held at <paramref name="at"/>, gone down in this frame when <paramref name="pressed"/>.</summary>
    private void Hold(Vector2 at, bool pressed)
    {
        _pointer.Pointer = new UiPointer(at, true, pressed, false, 0);
        Frame();
    }

    private void Release(Vector2 at)
    {
        _pointer.Pointer = new UiPointer(at, false, false, true, 0);
        Frame();
        Point(at);
        Frame();
    }

    /// <summary>A left click on the element reading <paramref name="label"/>: down in one frame, up in the next.</summary>
    private void Click(string label) => ClickAt(Centre(Find(label).Rect));

    private void ClickAt(Vector2 at)
    {
        Point(at);
        Frame();
        _pointer.Pointer = new UiPointer(at, true, true, false, 0);
        Frame();
        _pointer.Pointer = new UiPointer(at, false, false, true, 0);
        Frame();
        Point(at);
        Frame();
    }

    /// <summary>The surface's pixels down column <paramref name="x"/> over <paramref name="box"/>'s rows.</summary>
    private uint[] Column(int x, SKRect box)
    {
        var pixels = _surface.Pixels;
        var column = new uint[(int)box.Height];
        for (var i = 0; i < column.Length; i++)
        {
            var offset = (((int)box.Top + i) * _surface.Width + x) * 4;
            column[i] = BitConverter.ToUInt32(pixels.Slice(offset, 4));
        }

        return column;
    }

    private UiDraw Frame() => _surface.Draw(new FrameTime(1 / 60f, _frame / 60.0, _frame++, 0), Width, Height);

    /// <summary>A game's page: a line of text; it takes the first Escape.</summary>
    private sealed class CreditsPage : IMenuPage
    {
        private bool _escaped;

        public string Title => "Credits";

        public void Build(Ui ui) => ui.Text("Made by the test.");

        public bool Escape()
        {
            if (_escaped)
            {
                return false;
            }

            _escaped = true;
            return true;
        }
    }

    /// <summary>The frame input the menu reads: the pointer and the menu's keys.</summary>
    private sealed class PointerInput : IFrameInput
    {
        private readonly InputSnapshot _frame;

        public UiPointer Pointer { get; set; }

        public UiKeys Keys { get; set; }

        public ref readonly InputSnapshot Frame => ref _frame;

        public Vector2 MouseSinceTick => Vector2.Zero;

        public UiPointer MenuPointer => Pointer;

        public UiKeys MenuKeys => Keys;
    }
}
