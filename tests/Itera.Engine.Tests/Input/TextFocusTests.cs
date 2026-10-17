using System.Numerics;
using System.Text;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Control;
using Itera.Engine.Input;
using Itera.Engine.Systems;
using Itera.Engine.UI;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Input;

/// <summary>
/// A game's text box (<see cref="IFrameInput.SetTextFocus"/>) and its screens that close on Escape
/// (<see cref="IEscapeHandler"/>): while the box has the typing, keys and characters go to it and none reaches the game,
/// the keys held as it opens are let go, ` still opens the console, and the menu and a blocked input come first; Escape
/// goes to the box, then the screens from the newest, then the panel, then the menu; a match's end drops them all.
/// </summary>
public sealed class TextFocusTests : IDisposable
{
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly Menu _menu = new();
    private readonly Console _console = new();
    private readonly InputService _input;

    public TextFocusTests()
    {
        _input = new InputService(_window, NullLogger<InputService>.Instance);
        _input.SetMenu(_menu);
        _input.SetKeyboardFocus(_console);
    }

    public void Dispose()
    {
        _input.Dispose();
        _window.Dispose();
    }

    [Fact]
    public void A_text_box_takes_the_keys_and_characters_and_the_game_gets_none()
    {
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        Type("walk");
        _input.HandleKeyDown(Key.Left, null);
        _input.HandleKeyDown(Key.Backspace, null);
        _input.BeginTick();

        Assert.Same(entry, _input.TextFocus);
        Assert.Equal("wak", entry.Line.Text);
        Assert.False(_input.Tick.WasPressed(Key.W));
        Assert.False(_input.Tick.IsDown(Key.Left));
    }

    [Fact]
    public void Keys_held_as_the_box_opens_are_let_go_and_mouse_buttons_stay_held()
    {
        _input.HandleKeyDown(Key.W, null);
        _input.HandleMouseDown(MouseButton.Right);
        _input.BeginTick();
        Assert.True(_input.Tick.IsDown(Key.W));

        _input.SetTextFocus(new TextEntry(32));
        _input.BeginTick();

        Assert.False(_input.Tick.IsDown(Key.W));
        Assert.True(_input.Tick.IsDown(MouseButton.Right));
    }

    [Fact]
    public void Enter_and_escape_go_to_the_box_and_the_menu_stays_shut()
    {
        var entered = 0;
        var escaped = 0;
        var entry = new TextEntry(32, () => entered++, () => escaped++);
        _input.SetTextFocus(entry);
        _input.HandleKeyDown(Key.Enter, null);
        _input.HandleKeyDown(Key.KeypadEnter, null);
        _input.HandleKeyDown(Key.Escape, null);

        Assert.Equal(2, entered);
        Assert.Equal(1, escaped);
        Assert.Equal(0, _menu.Escapes);
    }

    [Fact]
    public void A_player_walking_stops_when_a_box_takes_the_keys_and_typing_w_moves_nothing()
    {
        using var world = World.Create();
        using var system = new PlayerInputSystem(
            world,
            _input,
            Options.Create(new EngineOptions()),
            forceFlying: false,
            NullLogger<PlayerInputSystem>.Instance
        );
        system.Initialize();
        world.Create(new PlayerCommand(), new PlayerMode(), new LocalPlayer());

        _input.HandleKeyDown(Key.W, null);
        Step(system);
        Assert.Equal(new Vector2(0, 1), Move(world));

        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        Step(system);
        Assert.Equal(Vector2.Zero, Move(world));

        Type("w");
        Step(system);
        Assert.Equal(Vector2.Zero, Move(world));
        Assert.Equal("w", entry.Line.Text);
    }

    [Fact]
    public void A_held_editing_key_repeats_after_a_pause_until_it_comes_up()
    {
        var now = 10.0;
        _input.Clock = () => now;
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        entry.Line.Set("abcdefghij");

        _input.HandleKeyDown(Key.Backspace, null);
        now = 10.39;
        _input.RepeatText(now);
        Assert.Equal("abcdefghi", entry.Line.Text);

        // After the pause, then 30 a second.
        now = 10.41;
        _input.RepeatText(now);
        Assert.Equal("abcdefgh", entry.Line.Text);
        now = 10.45;
        _input.RepeatText(now);
        Assert.Equal("abcdefg", entry.Line.Text);

        _input.HandleKeyUp(Key.Backspace);
        now = 11;
        _input.RepeatText(now);
        Assert.Equal("abcdefg", entry.Line.Text);
    }

    [Fact]
    public void A_long_wait_repeats_a_few_times_not_every_repeat_it_missed()
    {
        var now = 0.0;
        _input.Clock = () => now;
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        entry.Line.Set("abcdefghij", cursor: 0);

        _input.HandleKeyDown(Key.Delete, null);
        now = 5;
        _input.RepeatText(now);

        Assert.Equal("fghij", entry.Line.Text);
    }

    [Fact]
    public void Only_a_key_the_box_says_repeats_is_repeated_and_only_while_the_box_has_the_typing()
    {
        var now = 0.0;
        _input.Clock = () => now;
        var still = new RecordingText(repeats: false);
        _input.SetTextFocus(still);
        _input.HandleKeyDown(Key.Home, null);
        now = 1;
        _input.RepeatText(now);
        Assert.Equal([Key.Home], still.Downs);

        var repeating = new RecordingText(repeats: true);
        _input.SetTextFocus(repeating);
        _input.HandleKeyDown(Key.Left, null);
        _input.ClearTextFocus(repeating);
        now = 2;
        _input.RepeatText(now);
        _input.SetTextFocus(repeating);
        now = 3;
        _input.RepeatText(now);
        Assert.Equal([Key.Left], repeating.Downs);

        // The console opening over the box stops it too.
        _input.HandleKeyDown(Key.Right, null);
        _input.HandleKeyDown(Key.GraveAccent, null);
        now = 4;
        _input.RepeatText(now);
        Assert.Equal([Key.Left, Key.Right], repeating.Downs);
    }

    [Fact]
    public void The_menus_text_box_repeats_held_keys_too()
    {
        var now = 0.0;
        _input.Clock = () => now;
        var box = new RecordingText(repeats: true);
        _menu.IsOpen = true;
        _menu.TextFocus = box;
        _input.HandleKeyDown(Key.Backspace, null);
        now = 0.41;
        _input.RepeatText(now);

        Assert.Equal([Key.Backspace, Key.Backspace], box.Downs);
    }

    [Fact]
    public void Ctrl_v_pastes_the_clipboard_into_the_box()
    {
        var box = new RecordingText();
        _input.SetTextFocus(box);
        var keyboard = new Keyboard("pasted text");
        _input.HandleKeyDown(Key.ControlLeft, keyboard);
        _input.HandleKeyDown(Key.V, keyboard);

        Assert.Equal("pasted text", box.Pasted);
        Assert.Equal([Key.ControlLeft], box.Downs);
    }

    [Fact]
    public void With_the_console_off_the_console_key_types_into_the_box()
    {
        _console.Enabled = false;
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        Type("a");
        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.HandleCharacter('`');
        _input.HandleKeyUp(Key.GraveAccent);

        Assert.False(_console.IsOpen);
        Assert.Equal("a`", entry.Line.Text);
    }

    [Theory]
    [InlineData(0x1F600u, "\U0001F600")]
    [InlineData(0x41u, "A")]
    [InlineData(0xD800u, "")]
    [InlineData(0x110000u, "")]
    [InlineData(uint.MaxValue, "")]
    public void Code_points_from_the_window_arrive_whole_and_ones_that_are_no_character_are_dropped(
        uint codePoint,
        string typed
    )
    {
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        _input.OnCodePoint(codePoint);

        Assert.Equal(typed, entry.Line.Text);
    }

    /// <summary>
    /// A game's text box that throws as a window callback hands it a character does not throw through the callback
    /// (through GLFW's native frames it ended the process unsaved): the exception comes out of the next update, once,
    /// where the loop ends the match with it.
    /// </summary>
    [Fact]
    public void A_handler_that_throws_in_a_window_callback_throws_at_the_next_update_once()
    {
        _input.SetTextFocus(new Throwing());

        _input.OnCodePoint('a');
        _input.OnCodePoint('b');

        var thrown = Assert.Throws<InvalidOperationException>(_input.Update);
        Assert.Equal("a", thrown.Message);
        _input.Update();
    }

    [Fact]
    public void The_control_ports_console_key_then_type_reaches_the_console_whole()
    {
        var script = InputScript.Parse(_input, ["tap GraveAccent", "type pos"], out var error);
        Assert.Null(error);
        Assert.True(script!.Step(0));

        Assert.True(_console.IsOpen);
        Assert.Equal("pos", _console.Typed);
    }

    [Fact]
    public void Key_releases_reach_the_box()
    {
        var box = new RecordingText();
        _input.SetTextFocus(box);
        _input.HandleKeyDown(Key.A, null);
        _input.HandleKeyUp(Key.A);

        Assert.Equal([Key.A], box.Released);
    }

    [Fact]
    public void The_console_key_opens_the_console_over_the_box_and_closing_it_gives_the_typing_back()
    {
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.HandleCharacter('`');
        _input.HandleKeyUp(Key.GraveAccent);
        Assert.True(_console.IsOpen);

        Type("pos");
        _input.HandleKeyDown(Key.Escape, null);
        Assert.False(_console.IsOpen);
        Type("hi");

        Assert.Equal("pos", _console.Typed);
        Assert.Equal("hi", entry.Line.Text);
        Assert.Equal(0, _menu.Escapes);
    }

    [Fact]
    public void While_the_menu_is_open_the_box_gets_nothing_and_escape_goes_to_the_menu()
    {
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        _menu.IsOpen = true;
        Type("menu");
        _input.HandleKeyDown(Key.Escape, null);

        Assert.Equal("", entry.Line.Text);
        Assert.Equal(1, _menu.Escapes);
    }

    [Fact]
    public void While_the_games_input_is_blocked_the_box_gets_nothing()
    {
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        _input.SetGameInputBlocked(true);
        Type("loading");
        _input.HandleKeyDown(Key.Escape, null);

        Assert.Equal("", entry.Line.Text);
        Assert.Equal(1, _menu.Escapes);
    }

    [Fact]
    public void Clearing_the_box_gives_the_keys_back_to_the_game_and_another_box_is_left_alone()
    {
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        _input.ClearTextFocus(new TextEntry(32));
        Assert.Same(entry, _input.TextFocus);

        _input.ClearTextFocus(entry);
        _input.HandleKeyDown(Key.W, null);
        _input.HandleCharacter('w');
        _input.BeginTick();

        Assert.Null(_input.TextFocus);
        Assert.True(_input.Tick.WasPressed(Key.W));
        Assert.Equal("", entry.Line.Text);
    }

    [Fact]
    public void A_character_past_the_basic_plane_reaches_the_box_whole()
    {
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        _input.HandleCharacter(new Rune(0x1F600));
        _input.HandleKeyDown(Key.Left, null);
        _input.HandleCharacter('a');

        Assert.Equal("a\U0001F600", entry.Line.Text);
    }

    [Fact]
    public void The_control_ports_type_reaches_the_box_whole()
    {
        var entry = new TextEntry(32);
        _input.SetTextFocus(entry);
        var script = InputScript.Parse(_input, ["type a \U0001F600 b"], out var error);
        Assert.Null(error);
        Assert.True(script!.Step(0));

        Assert.Equal("a \U0001F600 b", entry.Line.Text);
    }

    [Fact]
    public void Escape_goes_to_the_newest_screen_that_takes_it_then_the_panel_then_the_menu()
    {
        var calls = new List<string>();
        var older = new Screen("older", calls, takes: true);
        var newer = new Screen("newer", calls, takes: false);
        var panel = new Panel(calls);
        _input.AddEscapeHandler(older);
        _input.AddEscapeHandler(newer);
        _input.AddEscapeHandler(older);
        _input.SetPanel(panel);

        _input.HandleKeyDown(Key.Escape, null);
        Assert.Equal(["newer", "older"], calls);

        calls.Clear();
        _input.RemoveEscapeHandler(older);
        _input.HandleKeyDown(Key.Escape, null);
        Assert.Equal(["newer", "panel"], calls);

        calls.Clear();
        _input.ClearPanel(panel);
        _input.RemoveEscapeHandler(newer);
        _input.RemoveEscapeHandler(newer);
        _input.HandleKeyDown(Key.Escape, null);
        Assert.Empty(calls);
        Assert.Equal(1, _menu.Escapes);
    }

    [Fact]
    public void A_screen_that_removes_itself_as_it_closes_leaves_the_others_asked_in_turn()
    {
        var calls = new List<string>();
        var under = new Screen("under", calls, takes: true);
        var top = new Screen("top", calls, takes: true);
        top.OnEscape = () => _input.RemoveEscapeHandler(top);
        _input.AddEscapeHandler(under);
        _input.AddEscapeHandler(top);

        _input.HandleKeyDown(Key.Escape, null);
        _input.HandleKeyDown(Key.Escape, null);

        Assert.Equal(["top", "under"], calls);
        Assert.Equal(0, _menu.Escapes);
    }

    [Fact]
    public void A_screen_that_removes_another_as_it_closes_asks_each_at_most_once()
    {
        var calls = new List<string>();
        var bottom = new Screen("bottom", calls, takes: false);
        var middle = new Screen("middle", calls, takes: false);
        var top = new Screen("top", calls, takes: false);
        top.OnEscape = () => _input.RemoveEscapeHandler(bottom);
        _input.AddEscapeHandler(bottom);
        _input.AddEscapeHandler(middle);
        _input.AddEscapeHandler(top);

        _input.HandleKeyDown(Key.Escape, null);

        Assert.Equal(["top", "middle"], calls);
        Assert.Equal(1, _menu.Escapes);
    }

    [Fact]
    public void Screens_are_not_asked_while_the_menu_is_open_or_the_input_is_blocked()
    {
        var calls = new List<string>();
        _input.AddEscapeHandler(new Screen("screen", calls, takes: true));
        _menu.IsOpen = true;
        _input.HandleKeyDown(Key.Escape, null);
        _menu.IsOpen = false;
        _input.SetGameInputBlocked(true);
        _input.HandleKeyDown(Key.Escape, null);

        Assert.Empty(calls);
        Assert.Equal(2, _menu.Escapes);
    }

    [Fact]
    public void A_matchs_end_drops_its_text_box_screens_and_panel()
    {
        var calls = new List<string>();
        _input.SetTextFocus(new TextEntry(32));
        _input.AddEscapeHandler(new Screen("screen", calls, takes: true));
        _input.SetPanel(new Panel(calls));

        _input.EndMatch();
        _input.HandleKeyDown(Key.Escape, null);

        Assert.Null(_input.TextFocus);
        Assert.False(_input.PanelSet);
        Assert.Empty(calls);
        Assert.Equal(1, _menu.Escapes);
    }

    [Fact]
    public void A_screen_takes_escape_without_freeing_the_pointer()
    {
        _input.AddEscapeHandler(new Screen("screen", [], takes: true));
        _input.SetInjectedCapture(true);
        _input.Update();
        _input.HandleKeyDown(Key.Escape, null);
        _input.Update();

        Assert.True(_input.PointerCaptured);
    }

    private void Step(PlayerInputSystem system)
    {
        _input.BeginTick();
        system.Update(new FixedStepTime(1f / 60, 1));
    }

    private static Vector2 Move(World world)
    {
        var move = Vector2.Zero;
        world.Query(
            new QueryDescription().WithAll<PlayerCommand, LocalPlayer>(),
            (ref PlayerCommand command) => move = command.Move
        );
        return move;
    }

    private void Type(string text)
    {
        foreach (var character in text.EnumerateRunes())
        {
            var key = character.Value is >= 'a' and <= 'z' ? Key.A + (character.Value - 'a') : Key.Unknown;
            _input.HandleKeyDown(key, null);
            _input.HandleCharacter(character);
            _input.HandleKeyUp(key);
        }
    }

    private sealed class Menu : IMenuInput
    {
        public bool IsOpen { get; set; }

        public ITextInput? TextFocus { get; set; }

        public int Escapes { get; private set; }

        public bool CapturingBinding => false;

        public void Escape() => Escapes++;

        public void BindingPressed(InputBinding binding) { }
    }

    private sealed class Console : IKeyboardFocus
    {
        public bool Enabled { get; set; } = true;

        public bool IsOpen { get; private set; }

        public string Typed { get; private set; } = "";

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public bool KeyDown(Key key, bool control) => false;

        public void KeyUp(Key key) { }

        public void Character(Rune character) => Typed += character.ToString();

        public void Paste(string text) { }

        public void Scroll(float steps) { }

        public void ReleaseKeys() { }
    }

    private sealed class RecordingText(bool repeats = false) : ITextInput
    {
        public List<Key> Downs { get; } = [];

        public List<Key> Released { get; } = [];

        public string Pasted { get; private set; } = "";

        public bool KeyDown(Key key, bool control)
        {
            Downs.Add(key);
            return repeats;
        }

        public void KeyUp(Key key) => Released.Add(key);

        public void Character(Rune character) { }

        public void Paste(string text) => Pasted += text;
    }

    /// <summary>A keyboard whose clipboard holds <paramref name="clipboard"/>.</summary>
    private sealed class Keyboard(string clipboard) : IKeyboard
    {
        public event Action<IKeyboard, Key, int>? KeyDown
        {
            add { }
            remove { }
        }

        public event Action<IKeyboard, Key, int>? KeyUp
        {
            add { }
            remove { }
        }

        public event Action<IKeyboard, char>? KeyChar
        {
            add { }
            remove { }
        }

        public string Name => "test";

        public int Index => 0;

        public bool IsConnected => true;

        public IReadOnlyList<Key> SupportedKeys => [];

        public string ClipboardText
        {
            get => clipboard;
            set => clipboard = value;
        }

        public bool IsKeyPressed(Key key) => false;

        public bool IsScancodePressed(int scancode) => false;

        public void BeginInput() { }

        public void EndInput() { }
    }

    private sealed class Screen(string name, List<string> calls, bool takes) : IEscapeHandler
    {
        public Action? OnEscape { get; set; }

        public bool Escape()
        {
            calls.Add(name);
            OnEscape?.Invoke();
            return takes;
        }
    }

    private sealed class Panel(List<string> calls) : IPanelInput
    {
        public void Escape() => calls.Add("panel");
    }

    /// <summary>A text box that throws at every character, naming it.</summary>
    private sealed class Throwing : ITextInput
    {
        public bool KeyDown(Key key, bool control) => false;

        public void KeyUp(Key key) { }

        public void Character(Rune character) => throw new InvalidOperationException(character.ToString());

        public void Paste(string text) { }
    }
}
