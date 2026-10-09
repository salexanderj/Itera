using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Settings;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// The menu drawn on a 1280 x 720 surface with a pointer the test moves: the main menu's buttons and the matches they
/// ask for, the join page's address box, the settings page's sliders and switches, the menu over a match (Escape,
/// resume, leave), a join's cancel, Escape on each page, the clicks (soft as the pointer comes onto a button, a switch, or a slider; fuller as one is pressed; none for a
/// page or the menu opening under a still pointer, the address box, or a slider's drag), and that it draws again only
/// when what it shows changes and allocates nothing while idle or clicking.
/// </summary>
public sealed class MenuLayerTests : IDisposable
{
    private const int Width = 1280;
    private const int Height = 720;
    private const float Unit = Height / 100f;

    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly MatchControl _matches = new();
    private readonly AudioService _audio = QueuingAudio();

    /// <summary>The player's settings, with no file.</summary>
    private PlayerSettings Settings { get; } =
        new(new EngineOptions(), CommandLineKeys.None, null, NullLogger<PlayerSettings>.Instance);
    private readonly PointerInput _pointer = new();
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
        var starts = new MatchCommands(commands, _matches, steam, Options.Create(options));
        _menu = new MenuLayer(
            _fonts,
            MenuStyle.Plain,
            _matches,
            starts,
            _pointer,
            _window,
            steam,
            new SteamFriendList(steam, "test", NullLogger<SteamFriendList>.Instance),
            Settings,
            _audio,
            Options.Create(options)
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

        Click(MainButton(0));

        Assert.Equal(
            new MatchStart(SessionMode.Offline, SessionTransportKind.Direct, null, 4, null, false),
            _matches.TakeStart()
        );
    }

    [Fact]
    public void Host_game_without_steam_hosts_over_udp_and_quit_closes_the_window()
    {
        Click(MainButton(1));
        var host = _matches.TakeStart()!;
        Assert.Equal(SessionMode.Host, host.Mode);
        Assert.Equal(SessionTransportKind.Direct, host.Transport);

        Click(MainButton(4));
        Assert.True(_window.ExitRequested);
    }

    [Fact]
    public void The_join_page_joins_the_address_typed_and_says_why_a_bad_one_cannot()
    {
        Click(MainButton(2));
        Assert.Equal(MenuView.Join, _menu.View);
        var box = Assert.IsAssignableFrom<IKeyboardFocus>(_menu.TextFocus);

        foreach (var character in "nowhere")
        {
            box.Character(character);
        }

        box.KeyDown(Key.Enter, false);
        Assert.Null(_matches.TakeStart());
        Assert.Equal(MenuView.Join, _menu.View);

        box.KeyDown(Key.U, true);
        foreach (var character in "127.0.0.1:47600")
        {
            box.Character(character);
        }

        box.KeyDown(Key.Enter, false);
        var join = _matches.TakeStart()!;
        Assert.Equal(SessionMode.Join, join.Mode);
        Assert.Equal("127.0.0.1:47600", join.Address);
        Assert.Equal(MenuView.Main, _menu.View);
    }

    /// <summary>
    /// The longest address stays within its box, moved left to keep the cursor shown (about 23 of 120 characters
    /// fitted, the rest drawn over the page).
    /// </summary>
    [Fact]
    public void A_long_address_stays_within_its_box()
    {
        Click(MainButton(2));
        Frame();
        var box = _menu.AddressBox;
        Assert.False(box.IsEmpty);
        var before = Column((int)box.Right + 3, box);
        var beforeLeft = Column((int)box.Left - 3, box);

        var focus = Assert.IsAssignableFrom<IKeyboardFocus>(_menu.TextFocus);
        for (var i = 0; i < MenuLayer.MaxAddressLength; i++)
        {
            focus.Character('W');
        }

        Frame();

        Assert.Equal(MenuLayer.MaxAddressLength, _menu.Address.Text.Length);
        // Moved left so the cursor, at the end, shows: the text is much wider than the box.
        Assert.True(_menu.AddressScroll > box.Width, $"{_menu.AddressScroll}");
        Assert.Equal(before, Column((int)box.Right + 3, box));
        Assert.Equal(beforeLeft, Column((int)box.Left - 3, box));
    }

    [Theory]
    [InlineData(0, 50, 80, 100, 0)]
    [InlineData(0, 150, 300, 100, 50)]
    [InlineData(50, 120, 300, 100, 50)]
    [InlineData(50, 20, 300, 100, 20)]
    [InlineData(250, 300, 300, 100, 200)]
    [InlineData(200, 90, 90, 100, 0)]
    public void The_address_moves_only_as_far_as_its_cursor_needs(
        float scroll,
        float cursor,
        float width,
        float room,
        float expected
    )
    {
        Assert.Equal(expected, MenuLayer.ScrollToCursor(scroll, cursor, width, room));
    }

    [Fact]
    public void A_long_name_is_cut_with_an_ellipsis_to_fit()
    {
        using var font = new SkiaSharp.SKFont(_fonts.Mono, 20);
        var name = new string('M', 60);

        Assert.Equal("Alice", MenuLayer.Fit("Alice", 1000, font));
        var fitted = MenuLayer.Fit(name, 200, font);
        Assert.EndsWith("…", fitted, StringComparison.Ordinal);
        Assert.True(font.MeasureText(fitted) <= 200);
        Assert.True(font.MeasureText(name[..(fitted.Length)] + "…") > 200);
        Assert.Equal("", MenuLayer.Fit(name, 1, font));
    }

    [Fact]
    public void Escape_goes_back_from_the_join_page_and_does_nothing_on_the_main_menu()
    {
        Click(MainButton(2));
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

        Click(MainButton(0));
        Assert.Equal(MenuView.None, _menu.View);

        _menu.Escape();
        Frame();
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.None, _menu.View);

        _menu.Escape();
        Frame();
        Click(MainButton(2));
        Assert.Equal("You left the match.", _matches.TakeLeave());

        // The match ended: the main menu, with the reason.
        _matches.Phase = MatchPhase.Menu;
        _matches.LastEnd = "You left the match.";
        Frame();
        Assert.Equal(MenuView.Main, _menu.View);
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
    public void The_settings_page_moves_a_setting_with_its_slider_and_flips_its_switches_on_each_tab()
    {
        Click(MainButton(3));
        Assert.Equal(MenuView.Settings, _menu.View);
        Assert.Equal(SettingsTab.Game, _menu.SettingsTab);
        // Game: volume, sensitivity, field of view, VSync, full screen.
        Assert.Equal(["Game", "Graphics", "Keybindings", "", "", "", "On", "Off", "Back"], _menu.Buttons);

        // The volume's bar: down at its middle, dragged past its right end, let go.
        var bar = new Vector2(SettingControlLeft, SettingMiddle(0));
        _pointer.Pointer = new UiPointer(bar + new Vector2(13 * Unit, 0), true, true, false, 0);
        Frame();
        Assert.Equal(0.5f, Settings.Volume, 2);
        _pointer.Pointer = new UiPointer(bar + new Vector2(40 * Unit, 0), true, false, false, 0);
        Frame();
        Assert.Equal(1f, Settings.Volume, 2);
        _pointer.Pointer = new UiPointer(bar + new Vector2(40 * Unit, 0), false, false, true, 0);
        Frame();

        Click(SettingSwitch(3));
        Assert.False(Settings.VSync);
        Assert.Equal(["Game", "Graphics", "Keybindings", "", "", "", "Off", "Off", "Back"], _menu.Buttons);

        // Graphics: dynamic render scaling and its threshold, shadows, anti-aliasing. The threshold's slider steps
        // through the offered rates: near its left end the first, 30, past its right end the last, 360, and its middle
        // the sixth of eleven, 120.
        Click(SettingsTabButton(1));
        Assert.Equal(SettingsTab.Graphics, _menu.SettingsTab);
        Assert.Equal(["Game", "Graphics", "Keybindings", "On", "", "On", "", "Back"], _menu.Buttons);
        Assert.Equal(120, Settings.ScalingThreshold);
        var threshold = new Vector2(SettingControlLeft, SettingMiddle(1));
        _pointer.Pointer = new UiPointer(threshold + new Vector2(0.5f * Unit, 0), true, true, false, 0);
        Frame();
        Assert.Equal(30, Settings.ScalingThreshold);
        _pointer.Pointer = new UiPointer(threshold + new Vector2(40 * Unit, 0), true, false, false, 0);
        Frame();
        Assert.Equal(360, Settings.ScalingThreshold);
        _pointer.Pointer = new UiPointer(threshold + new Vector2(13 * Unit, 0), true, false, false, 0);
        Frame();
        Assert.Equal(120, Settings.ScalingThreshold);
        _pointer.Pointer = new UiPointer(threshold + new Vector2(13 * Unit, 0), false, false, true, 0);
        Frame();

        // Anti-aliasing's slider steps through Off, SMAA, MSAA 2x, 4x (the default), and 8x.
        Assert.Equal(AntiAliasMode.Msaa4, Settings.AntiAliasMode);
        var antiAliasing = new Vector2(SettingControlLeft, SettingMiddle(3));
        _pointer.Pointer = new UiPointer(antiAliasing + new Vector2(0.5f * Unit, 0), true, true, false, 0);
        Frame();
        Assert.Equal(AntiAliasMode.Off, Settings.AntiAliasMode);
        Assert.Equal(1, Settings.MsaaSamples);
        Assert.False(Settings.AntiAliasing);
        _pointer.Pointer = new UiPointer(antiAliasing + new Vector2(7 * Unit, 0), true, false, false, 0);
        Frame();
        Assert.Equal(AntiAliasMode.Smaa, Settings.AntiAliasMode);
        Assert.True(Settings.AntiAliasing);
        _pointer.Pointer = new UiPointer(antiAliasing + new Vector2(40 * Unit, 0), true, false, false, 0);
        Frame();
        Assert.Equal(AntiAliasMode.Msaa8, Settings.AntiAliasMode);
        Assert.Equal(8, Settings.MsaaSamples);
        _pointer.Pointer = new UiPointer(antiAliasing + new Vector2(40 * Unit, 0), false, false, true, 0);
        Frame();

        // Each switch flips its setting and shows it.
        Click(SettingSwitch(0));
        Click(SettingSwitch(2));
        Assert.False(Settings.DynamicScaling);
        Assert.False(Settings.Shadows);
        Assert.Equal(["Game", "Graphics", "Keybindings", "Off", "", "Off", "", "Back"], _menu.Buttons);

        Click(SettingsTabButton(2));
        Assert.Equal(SettingsTab.Keybindings, _menu.SettingsTab);

        // Escape leaves the page; it opens on Game again.
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.Main, _menu.View);
        Click(MainButton(3));
        Assert.Equal(SettingsTab.Game, _menu.SettingsTab);
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

        Click(MainButton(3));
        Click(SettingsTabButton(2));
        // Menu, Console, and Capture the pointer are fixed: no button. Then Stats line, Jump, Crouch, Extra 0 and 1.
        Assert.Equal(
            ["Game", "Graphics", "Keybindings", "F1", "Space", "C", "F2", "F3", "Reset keys", "Back"],
            _menu.Buttons
        );
        Click(KeyButton(0));
        Assert.False(_menu.CapturingBinding);

        // Jump's button waits for a key; C takes it from Crouch, whose row shows None.
        Click(KeyButton(4));
        Assert.True(_menu.CapturingBinding);
        Assert.Contains("Press a key…", _menu.Buttons);
        _audio.ClearQueued();
        _menu.BindingPressed(InputBinding.Of(Key.C));
        Frame();
        Assert.False(_menu.CapturingBinding);
        Assert.Equal(InputBinding.Of(Key.C), keys[jump]);
        Assert.Equal(InputBinding.None, keys[crouch]);
        Assert.Equal(
            ["Game", "Graphics", "Keybindings", "F1", "C", "None", "F2", "F3", "Reset keys", "Back"],
            _menu.Buttons
        );
        // Setting a binding makes no click.
        Assert.Equal(0, Clicks(EngineSounds.MenuPress));

        // Escape and ` stay the menu's and the console's; Escape stops the wait and keeps the binding, on the page.
        Click(KeyButton(4));
        _menu.BindingPressed(InputBinding.Of(Key.GraveAccent));
        Assert.True(_menu.CapturingBinding);
        _menu.Escape();
        Frame();
        Assert.False(_menu.CapturingBinding);
        Assert.Equal(MenuView.Settings, _menu.View);
        Assert.Equal(InputBinding.Of(Key.C), keys[jump]);

        // The wheel shows the last rows.
        _pointer.Pointer = new UiPointer(KeyButton(4), false, false, false, -10);
        Frame();
        Point(KeyButton(4));
        Frame();
        Assert.Equal(
            ["Game", "Graphics", "Keybindings", "C", "None", "F2", "F3", "F4", "F5", "F6", "F7", "Reset keys", "Back"],
            _menu.Buttons
        );

        // Reset keys puts them back; Back leaves the page.
        Click(new Vector2(Width / 2f - 11 * Unit, 85.75f * Unit));
        Assert.Equal(InputBinding.Of(Key.Space), keys[jump]);
        Assert.Equal(InputBinding.Of(Key.C), keys[crouch]);
        Click(new Vector2(Width / 2f + 11 * Unit, 85.75f * Unit));
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
        Click(new Vector2(Width / 2f, 55 * Unit));
        Assert.Equal("The join was cancelled.", _matches.TakeLeave());
    }

    [Fact]
    public void The_pointer_coming_onto_a_button_clicks_softly_and_a_press_clicks_fully()
    {
        Point(new Vector2(10, 10));
        Frame();
        Assert.Equal(0, Clicks(EngineSounds.MenuHover));

        Point(MainButton(0));
        Frame();
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
        Assert.True(_audio.QueuedEvents[0].AtListener);

        // Moving on the same button, or off it into nothing, clicks no more; onto the next one does.
        Point(MainButton(0) + new Vector2(5, 2));
        Frame();
        Point(new Vector2(10, 10));
        Frame();
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
        Point(MainButton(1));
        Frame();
        Assert.Equal(2, Clicks(EngineSounds.MenuHover));
        Assert.Equal(0, Clicks(EngineSounds.MenuPress));

        // Down and up on it: one fuller click, as the button acts.
        Click(MainButton(1));
        Assert.Equal(1, Clicks(EngineSounds.MenuPress));
        Assert.Equal(2, Clicks(EngineSounds.MenuHover));
        Assert.NotNull(_matches.TakeStart());
    }

    [Fact]
    public void A_page_or_the_menu_opening_under_a_still_pointer_makes_no_soft_click()
    {
        // Join Game opens its page under the pointer, which now lies over the friends' list or a button: no click.
        Click(MainButton(2));
        Frame();
        Assert.Equal(MenuView.Join, _menu.View);
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
        Assert.Equal(1, Clicks(EngineSounds.MenuPress));

        // Over a match, Escape opens the menu with the pointer resting on Resume: no click until it moves.
        _menu.Escape();
        _matches.Phase = MatchPhase.Playing;
        Frame();
        _audio.ClearQueued();
        Point(MainButton(0));
        Frame();
        _menu.Escape();
        Frame();
        Assert.Equal(MenuView.InGame, _menu.View);
        Assert.Equal(0, Clicks(EngineSounds.MenuHover));
        Point(MainButton(1));
        Frame();
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
    }

    [Fact]
    public void The_address_box_clicks_only_as_enter_joins_and_a_sliders_drag_presses_nothing()
    {
        Click(MainButton(2));
        _audio.ClearQueued();
        // The address box, under the friends' list: neither under the pointer nor clicked.
        Click(new Vector2(Width / 2f, 72 * Unit));
        Assert.Equal(0, _audio.QueuedEvents.Length);
        var box = Assert.IsAssignableFrom<IKeyboardFocus>(_menu.TextFocus);
        foreach (var character in "127.0.0.1:47600")
        {
            box.Character(character);
        }

        box.KeyDown(Key.Enter, false);
        Assert.Equal(1, Clicks(EngineSounds.MenuPress));
        Assert.NotNull(_matches.TakeStart());

        Click(MainButton(3));
        _audio.ClearQueued();
        // The volume's bar: onto it clicks softly; held, dragged, and let go, it presses nothing.
        var bar = new Vector2(SettingControlLeft, SettingMiddle(0));
        Point(bar);
        Frame();
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
        _pointer.Pointer = new UiPointer(bar, true, true, false, 0);
        Frame();
        _pointer.Pointer = new UiPointer(bar + new Vector2(10 * Unit, 0), true, false, false, 0);
        Frame();
        _pointer.Pointer = new UiPointer(bar + new Vector2(10 * Unit, 0), false, false, true, 0);
        Frame();
        Assert.Equal(0, Clicks(EngineSounds.MenuPress));
    }

    [Fact]
    public void It_draws_again_only_when_what_it_shows_changes_and_allocates_nothing_while_idle()
    {
        Point(new Vector2(10, 10));
        Assert.Equal(0, Frame().Rows);

        // The pointer onto a button: drawn again (the button under it), then not until it moves off.
        Point(MainButton(1));
        Assert.True(Frame().Rows > 0);
        Assert.Equal(0, Frame().Rows);

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            _menu.Update(new FrameTime(1 / 60f, i / 60.0, (ulong)i, 0));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);

        // The pointer going between two buttons, a click each time: nothing allocated either.
        _audio.ClearQueued();
        start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 10; i++)
        {
            Point(MainButton(i % 2));
            _menu.Update(new FrameTime(1 / 60f, i / 60.0, (ulong)i, 0));
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

    /// <summary>The middle of main-menu button <paramref name="index"/> (Play Offline 0 to Quit 4).</summary>
    private static Vector2 MainButton(int index) => new(Width / 2f, (36 + 3 + index * 7.5f) * Unit);

    /// <summary>The middle of the settings tab's row <paramref name="row"/>, top to bottom, in pixels.</summary>
    private static float SettingMiddle(int row) => (31 + row * 7 + 3.4f) * Unit;

    /// <summary>The left end of a Game or Graphics row's slider or switch, 26 units wide at the row's right end.</summary>
    private static float SettingControlLeft => Width / 2f + (MenuLayer.SettingRowUnits / 2 - 26) * Unit;

    /// <summary>The middle of the switch on the settings tab's row <paramref name="row"/>.</summary>
    private static Vector2 SettingSwitch(int row) => new(SettingControlLeft + 13 * Unit, SettingMiddle(row));

    /// <summary>The middle of the button on the Keybindings tab's row <paramref name="row"/> shown.</summary>
    private static Vector2 KeyButton(int row) => new(Width / 2f + 19 * Unit, (31 + row * 6 + 3) * Unit);

    /// <summary>The middle of the settings page's tab <paramref name="tab"/>: Game, Graphics, Keybindings.</summary>
    private static Vector2 SettingsTabButton(int tab) => new(Width / 2f + (tab - 1) * 23.5f * Unit, 25.75f * Unit);

    private void Point(Vector2 at) => _pointer.Pointer = new UiPointer(at, false, false, false, 0);

    /// <summary>A left click at <paramref name="at"/>: down in one frame, up in the next.</summary>
    private void Click(Vector2 at)
    {
        _pointer.Pointer = new UiPointer(at, true, true, false, 0);
        Frame();
        _pointer.Pointer = new UiPointer(at, false, false, true, 0);
        Frame();
        Point(at);
    }

    /// <summary>The surface's pixels down column <paramref name="x"/> over <paramref name="box"/>'s rows.</summary>
    private uint[] Column(int x, SkiaSharp.SKRect box)
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

    /// <summary>The frame input the menu reads: only the pointer.</summary>
    private sealed class PointerInput : IFrameInput
    {
        private readonly InputSnapshot _frame;

        public UiPointer Pointer { get; set; }

        public ref readonly InputSnapshot Frame => ref _frame;

        public Vector2 MouseSinceTick => Vector2.Zero;

        public UiPointer MenuPointer => Pointer;
    }
}
