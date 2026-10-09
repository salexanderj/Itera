using Itera.Engine.Input;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Input;

public sealed class KeyboardFocusTests : IDisposable
{
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly RecordingFocus _focus = new();
    private readonly InputService _input;

    public KeyboardFocusTests()
    {
        _input = new InputService(_window, NullLogger<InputService>.Instance);
        _input.SetKeyboardFocus(_focus);
    }

    public void Dispose()
    {
        _input.Dispose();
        _window.Dispose();
    }

    [Fact]
    public void While_the_game_s_input_is_blocked_its_snapshots_are_empty_but_the_console_opens_and_Escape_quits()
    {
        _input.SetGameInputBlocked(true);
        _input.HandleKeyDown(Key.W, null);
        _input.HandleMouseDown(MouseButton.Left);
        _input.HandleScroll(2);
        _input.BeginFrame();
        _input.BeginTick();

        Assert.False(_input.Tick.IsDown(Key.W));
        Assert.False(_input.Frame.WasPressed(Key.W));
        Assert.False(_input.Tick.WasPressed(MouseButton.Left));
        Assert.Equal(0, _input.Frame.Scroll);

        Open();
        Assert.True(_focus.IsOpen);
        _input.HandleKeyDown(Key.Escape, null);
        Assert.False(_focus.IsOpen);
        Assert.False(_window.ExitRequested);

        // The pointer is free while loading, so Escape asks to quit.
        _input.HandleKeyDown(Key.Escape, null);
        Assert.True(_window.ExitRequested);

        // Let through again, keys pressed from now on reach the game.
        _input.SetGameInputBlocked(false);
        _input.HandleKeyDown(Key.D, null);
        _input.BeginTick();
        Assert.True(_input.Tick.IsDown(Key.D));
        Assert.False(_input.Tick.IsDown(Key.W));
    }

    [Fact]
    public void The_opening_key_opens_releases_held_keys_and_its_character_is_dropped()
    {
        _input.HandleKeyDown(Key.W, null);
        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.HandleCharacter('`');
        _input.HandleCharacter('a');
        _input.BeginTick();

        Assert.True(_focus.IsOpen);
        Assert.Equal("a", _focus.Typed);
        Assert.False(_input.Tick.IsDown(Key.W));
        Assert.False(_input.Tick.WasPressed(Key.GraveAccent));
    }

    /// <summary>The opening key held types none of its repeats into the console.</summary>
    [Fact]
    public void Holding_the_opening_key_types_none_of_its_repeats()
    {
        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.HandleCharacter('`');
        _input.HandleCharacter('`');
        _input.HandleCharacter('`');
        Assert.True(_focus.IsOpen);
        Assert.Equal("", _focus.Typed);

        _input.HandleKeyUp(Key.GraveAccent);
        // A backtick from another key, once ` is let go, is typed.
        _input.HandleCharacter('`');

        Assert.Equal("`", _focus.Typed);
    }

    /// <summary>Ctrl holds the console's shortcuts everywhere, and Command too on macOS.</summary>
    [Theory]
    [InlineData(Key.ControlLeft, false, true)]
    [InlineData(Key.ControlRight, true, true)]
    [InlineData(Key.SuperLeft, true, true)]
    [InlineData(Key.SuperRight, true, true)]
    [InlineData(Key.SuperLeft, false, false)]
    [InlineData(Key.AltLeft, true, false)]
    public void The_shortcut_key_is_ctrl_and_command_on_macos(Key key, bool macOS, bool shortcut)
    {
        Assert.Equal(shortcut, InputService.IsShortcutKey(key, macOS));
    }

    [Fact]
    public void While_open_keys_reach_the_console_and_not_the_game_even_after_it_closes()
    {
        Open();
        _input.HandleKeyDown(Key.F2, null);
        _input.HandleKeyDown(Key.W, null);
        _input.HandleKeyUp(Key.W);
        _input.HandleKeyDown(Key.Escape, null);
        _input.HandleKeyUp(Key.Escape);
        _input.HandleKeyDown(Key.D, null);
        _input.BeginFrame();
        _input.BeginTick();

        Assert.False(_focus.IsOpen);
        Assert.Equal([Key.F2, Key.W], _focus.Keys);
        Assert.False(_window.ExitRequested);
        Assert.False(_input.Frame.WasPressed(Key.F2));
        Assert.False(_input.Tick.WasPressed(Key.W));
        Assert.False(_input.Tick.WasPressed(Key.Escape));
        // Once closed, keys reach the game again.
        Assert.True(_input.Tick.WasPressed(Key.D));
    }

    [Fact]
    public void The_opening_key_closes_it_and_its_character_is_dropped()
    {
        Open();
        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.HandleCharacter('`');
        Open();
        _input.HandleCharacter('b');

        Assert.Equal("b", _focus.Typed);
    }

    [Fact]
    public void Holding_the_opening_key_does_not_close_it_again()
    {
        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.HandleCharacter('`');
        // A platform that repeats held keys sends more key-downs and characters before the key-up.
        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.HandleCharacter('`');
        _input.HandleKeyUp(Key.GraveAccent);
        _input.HandleCharacter('c');

        Assert.True(_focus.IsOpen);
        Assert.Equal("c", _focus.Typed);
    }

    [Fact]
    public void While_open_clicks_do_nothing_and_the_wheel_scrolls_the_console()
    {
        Open();
        _input.HandleMouseDown(MouseButton.Right);
        _input.HandleScroll(2);
        _input.BeginTick();

        Assert.False(_input.Tick.WasPressed(MouseButton.Right));
        Assert.Equal(0, _input.Tick.Scroll);
        Assert.Equal(2, _focus.Scrolled);
    }

    [Fact]
    public void Ctrl_is_passed_with_the_key()
    {
        Open();
        _input.HandleKeyDown(Key.ControlLeft, null);
        _input.HandleKeyDown(Key.Left, null);

        Assert.True(_focus.LastControl);
    }

    [Fact]
    public void A_disabled_console_leaves_the_key_to_the_game()
    {
        _focus.Enabled = false;

        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.BeginTick();

        Assert.False(_focus.IsOpen);
        Assert.True(_input.Tick.WasPressed(Key.GraveAccent));
    }

    private void Open()
    {
        _input.HandleKeyDown(Key.GraveAccent, null);
        _input.HandleCharacter('`');
        Assert.True(_focus.IsOpen);
    }

    private sealed class RecordingFocus : IKeyboardFocus
    {
        public bool Enabled { get; set; } = true;

        public bool IsOpen { get; private set; }

        public string Typed { get; private set; } = "";

        public List<Key> Keys { get; } = [];

        public float Scrolled { get; private set; }

        public bool LastControl { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void KeyDown(Key key, bool control)
        {
            Keys.Add(key);
            LastControl = control;
        }

        public void KeyUp(Key key) { }

        public void Character(char character) => Typed += character;

        public void Paste(string text) => Typed += text;

        public void Scroll(float steps) => Scrolled += steps;

        public void ReleaseKeys() { }
    }
}
