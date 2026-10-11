using System.Numerics;
using System.Text;
using Itera.Engine.Input;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Input;

/// <summary>
/// <see cref="InputService"/> with a menu (<see cref="IMenuInput"/>): while it is open, left clicks and the wheel go to
/// it (<see cref="InputService.MenuPointer"/>), none captures the pointer or reaches the game, and its text box takes
/// the keys and characters; Escape goes to it and never quits, also with the menu closed (in a match it opens it), and
/// with the pointer captured it frees the pointer and goes to the menu at once, never reaching the game; while the menu
/// waits for a key to bind, the next key and any mouse button go to it and click nothing, Escape goes to it as Escape,
/// and ` opens the console and stops the wait.
/// </summary>
public sealed class MenuInputTests : IDisposable
{
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly FakeMenu _menu = new();
    private readonly InputService _input;

    public MenuInputTests()
    {
        _input = new InputService(_window, NullLogger<InputService>.Instance);
        _input.SetMenu(_menu);
    }

    public void Dispose()
    {
        _input.Dispose();
        _window.Dispose();
    }

    [Fact]
    public void While_the_menu_is_open_a_click_goes_to_it_and_never_to_the_game_or_the_capture()
    {
        _menu.IsOpen = true;
        _input.InjectPointer(new Vector2(120, 80));
        _input.HandleMouseDown(MouseButton.Left);
        _input.HandleMouseDown(MouseButton.Right);
        _input.HandleScroll(2);
        _input.Update();
        _input.BeginFrame();
        _input.BeginTick();

        Assert.Equal(new UiPointer(new Vector2(120, 80), true, true, false, 2, RightPressed: true), _input.MenuPointer);
        Assert.False(_input.PointerCaptured);
        Assert.False(_input.Tick.WasPressed(MouseButton.Left));
        Assert.False(_input.Tick.WasPressed(MouseButton.Right));
        Assert.Equal(0, _input.Frame.Scroll);

        _input.HandleMouseUp(MouseButton.Left);
        _input.BeginFrame();
        Assert.Equal(new UiPointer(new Vector2(120, 80), false, false, true, 0), _input.MenuPointer);

        // A frame later the click's edges are gone.
        _input.BeginFrame();
        Assert.Equal(new UiPointer(new Vector2(120, 80), false, false, false, 0), _input.MenuPointer);
    }

    [Fact]
    public void With_the_menu_closed_clicks_reach_the_game_as_before()
    {
        _input.HandleMouseDown(MouseButton.Right);
        _input.BeginTick();
        _input.BeginFrame();

        Assert.True(_input.Tick.WasPressed(MouseButton.Right));
        Assert.False(_input.MenuPointer.Pressed);
    }

    [Fact]
    public void Escape_goes_to_the_menu_and_never_quits()
    {
        _input.HandleKeyDown(Key.Escape, null);
        _menu.IsOpen = true;
        _input.HandleKeyDown(Key.Escape, null);

        Assert.Equal(2, _menu.Escapes);
        Assert.False(_window.ExitRequested);
    }

    [Fact]
    public void Escape_with_the_pointer_captured_goes_to_the_menu_at_once_and_frees_the_pointer()
    {
        _input.SetInjectedCapture(true);
        _input.Update();
        Assert.True(_input.PointerCaptured);

        _input.HandleKeyDown(Key.Escape, null);
        _input.Update();
        _input.BeginTick();

        Assert.False(_input.PointerCaptured);
        Assert.Equal(1, _menu.Escapes);
        Assert.False(_input.Tick.WasPressed(Key.Escape));
        Assert.False(_window.ExitRequested);
    }

    [Fact]
    public void The_menus_text_box_takes_keys_and_characters_but_not_escape()
    {
        var box = new TypedFocus();
        _menu.IsOpen = true;
        _menu.TextFocus = box;
        _input.HandleKeyDown(Key.W, null);
        _input.HandleCharacter('w');
        _input.HandleKeyDown(Key.Backspace, null);
        _input.HandleKeyDown(Key.Escape, null);
        _input.SetGameInputBlocked(false);
        _input.BeginTick();

        Assert.Equal([Key.W, Key.Backspace], box.Keys);
        Assert.Equal("w", box.Typed);
        Assert.Equal(1, _menu.Escapes);
        Assert.False(_input.Tick.WasPressed(Key.W));
    }

    [Fact]
    public void While_the_menu_waits_for_a_key_the_next_key_and_any_button_go_to_it_and_click_nothing()
    {
        _menu.IsOpen = true;
        _menu.CapturingBinding = true;
        _input.HandleKeyDown(Key.J, null);
        _input.HandleMouseDown(MouseButton.Middle);
        _input.HandleMouseDown(MouseButton.Left);
        _input.HandleMouseUp(MouseButton.Left);
        _input.HandleKeyDown(Key.Escape, null);
        _input.SetGameInputBlocked(false);
        _input.BeginFrame();
        _input.BeginTick();

        Assert.Equal(
            [InputBinding.Of(Key.J), InputBinding.Of(MouseButton.Middle), InputBinding.Of(MouseButton.Left)],
            _menu.Bound
        );
        Assert.False(_input.MenuPointer.Pressed);
        Assert.False(_input.MenuPointer.Released);
        Assert.False(_input.Tick.WasPressed(Key.J));
        // Escape is the menu's own: it stops the wait there.
        Assert.Equal(1, _menu.Escapes);
    }

    [Fact]
    public void The_console_key_opens_the_console_and_stops_the_menus_wait()
    {
        var console = new ConsoleFocus();
        _input.SetKeyboardFocus(console);
        _menu.IsOpen = true;
        _menu.CapturingBinding = true;

        _input.HandleKeyDown(Key.GraveAccent, null);

        Assert.True(console.IsOpen);
        Assert.Equal([InputBinding.None], _menu.Bound);
    }

    [Fact]
    public void The_drawn_pointer_moves_from_its_middle_within_its_radius_and_the_view_does_not_turn()
    {
        Capture();
        _input.SetDrawnPointer(true, new Vector2(500, 300), 100);
        _input.BeginFrame();
        Assert.True(_input.DrawnPointerOn);
        Assert.Equal(new Vector2(500, 300), _input.MenuPointer.Position);

        _input.InjectLook(new Vector2(30, -40));
        _input.BeginFrame();
        _input.BeginTick();
        Assert.Equal(new Vector2(530, 260), _input.MenuPointer.Position);
        Assert.Equal(Vector2.Zero, _input.Frame.MouseDelta);
        Assert.Equal(Vector2.Zero, _input.Tick.MouseDelta);
        Assert.Equal(Vector2.Zero, _input.MouseSinceTick);

        // Far past its radius it stops on the circle; turned on again it stays where it is.
        _input.InjectLook(new Vector2(1000, 0));
        _input.BeginFrame();
        Assert.Equal(100, Vector2.Distance(new Vector2(500, 300), _input.MenuPointer.Position), 2);
        var at = _input.MenuPointer.Position;
        _input.SetDrawnPointer(true, Vector2.Zero, 5);
        _input.BeginFrame();
        Assert.Equal(at, _input.MenuPointer.Position);

        // Off: the mouse looks again, and none of the drawn pointer's movement turns the view.
        _input.SetDrawnPointer(false, default, 0);
        _input.InjectLook(new Vector2(7, 0));
        _input.BeginFrame();
        _input.BeginTick();
        Assert.False(_input.DrawnPointerOn);
        Assert.Equal(new Vector2(7, 0), _input.Frame.MouseDelta);
        Assert.Equal(new Vector2(7, 0), _input.Tick.MouseDelta);
    }

    /// <summary>
    /// A mouse button held across the drawn pointer's opening stays held: the
    /// radial menu's own key bound to a mouse button keeps it open, and Sprint on another stays held; only the left
    /// button, the drawn pointer's, is let go.
    /// </summary>
    [Fact]
    public void Mouse_buttons_held_as_the_drawn_pointer_turns_on_stay_held_but_the_left()
    {
        Capture();
        _input.HandleMouseDown(MouseButton.Button4);
        _input.HandleMouseDown(MouseButton.Button5);
        _input.HandleMouseDown(MouseButton.Left);
        _input.BeginTick();

        _input.SetDrawnPointer(true, new Vector2(500, 300), 100);
        for (var frame = 0; frame < 3; frame++)
        {
            _input.BeginFrame();
            _input.BeginTick();
            Assert.True(_input.Frame.IsDown(MouseButton.Button4));
            Assert.True(_input.Tick.IsDown(MouseButton.Button5));
            Assert.False(_input.Tick.IsDown(MouseButton.Left));
        }
    }

    /// <summary>The wheel goes to the drawn pointer, not the game, while it shows.</summary>
    [Fact]
    public void The_wheel_goes_to_the_drawn_pointer_and_not_the_game()
    {
        Capture();
        _input.SetDrawnPointer(true, new Vector2(500, 300), 100);
        _input.HandleScroll(2);
        _input.BeginFrame();
        Assert.Equal(0, _input.Frame.Scroll);
        Assert.Equal(2, _input.MenuPointer.Scroll);

        _input.SetDrawnPointer(false, default, 0);
        _input.HandleScroll(1);
        _input.BeginFrame();

        Assert.Equal(1, _input.Frame.Scroll);
    }

    [Fact]
    public void While_the_drawn_pointer_is_on_the_left_button_clicks_it_and_the_others_reach_the_game()
    {
        Capture();
        _input.HandleMouseDown(MouseButton.Left);
        _input.BeginTick();
        Assert.True(_input.Tick.IsDown(MouseButton.Left));

        // An attack held as it turns on is let go.
        _input.SetDrawnPointer(true, new Vector2(500, 300), 100);
        _input.BeginTick();
        Assert.False(_input.Tick.IsDown(MouseButton.Left));

        _input.HandleMouseUp(MouseButton.Left);
        _input.HandleMouseDown(MouseButton.Left);
        _input.HandleMouseDown(MouseButton.Right);
        _input.BeginFrame();
        _input.BeginTick();
        Assert.True(_input.MenuPointer.Pressed);
        Assert.True(_input.MenuPointer.Down);
        Assert.False(_input.Tick.WasPressed(MouseButton.Left));
        // Another button reaches the game, as a key would; the game takes no attack while the drawn pointer is on.
        Assert.True(_input.Tick.WasPressed(MouseButton.Right));
        Assert.True(_input.Tick.IsDown(MouseButton.Right));
        Assert.True(_input.PointerCaptured);

        _input.HandleMouseUp(MouseButton.Left);
        _input.BeginFrame();
        Assert.True(_input.MenuPointer.Released);
        Assert.False(_input.MenuPointer.Down);

        // Freeing the pointer turns it off.
        _input.SetInjectedCapture(false);
        _input.Update();
        Assert.False(_input.PointerCaptured);
        Assert.False(_input.DrawnPointerOn);
    }

    [Fact]
    public void While_the_menu_is_open_navigation_keys_reach_its_focus_and_its_text_box_keeps_all_but_tab()
    {
        _menu.IsOpen = true;
        _input.HandleKeyDown(Key.Tab, null);
        _input.HandleKeyDown(Key.Down, null);
        _input.HandleKeyDown(Key.ShiftLeft, null);
        _input.HandleKeyDown(Key.Tab, null);
        _input.HandleKeyUp(Key.ShiftLeft);
        _input.HandleKeyDown(Key.Space, null);
        _input.BeginFrame();
        Assert.Equal(
            Itera.Engine.UI.Toolkit.UiKeys.Next
                | Itera.Engine.UI.Toolkit.UiKeys.Down
                | Itera.Engine.UI.Toolkit.UiKeys.Previous
                | Itera.Engine.UI.Toolkit.UiKeys.Press,
            _input.MenuKeys
        );
        _input.BeginFrame();
        Assert.Equal(Itera.Engine.UI.Toolkit.UiKeys.None, _input.MenuKeys);

        var box = new TypedFocus();
        _menu.TextFocus = box;
        _input.HandleKeyDown(Key.Enter, null);
        _input.HandleKeyDown(Key.Tab, null);
        _input.BeginFrame();
        Assert.Equal([Key.Enter], box.Keys);
        Assert.Equal(Itera.Engine.UI.Toolkit.UiKeys.Next, _input.MenuKeys);

        // With the menu shut the keys are the game's.
        _menu.IsOpen = false;
        _menu.TextFocus = null;
        _input.HandleKeyDown(Key.Tab, null);
        _input.BeginFrame();
        Assert.Equal(Itera.Engine.UI.Toolkit.UiKeys.None, _input.MenuKeys);
    }

    [Fact]
    public void While_the_menu_is_open_the_right_button_reaches_its_pointer()
    {
        _menu.IsOpen = true;
        _input.HandleMouseDown(MouseButton.Right);
        _input.BeginFrame();
        Assert.True(_input.MenuPointer.RightPressed);
        _input.HandleMouseUp(MouseButton.Right);
        _input.BeginFrame();
        Assert.True(_input.MenuPointer.RightReleased);
        Assert.False(_input.MenuPointer.RightPressed);
    }

    /// <summary>Captures the pointer as the control port does, with no window.</summary>
    private void Capture()
    {
        _input.SetInjectedCapture(true);
        _input.Update();
        Assert.True(_input.PointerCaptured);
    }

    private sealed class FakeMenu : IMenuInput
    {
        public bool IsOpen { get; set; }

        public ITextInput? TextFocus { get; set; }

        public int Escapes { get; private set; }

        public bool CapturingBinding { get; set; }

        public List<InputBinding> Bound { get; } = [];

        public void Escape() => Escapes++;

        public void BindingPressed(InputBinding binding) => Bound.Add(binding);
    }

    private sealed class ConsoleFocus : IKeyboardFocus
    {
        public bool Enabled => true;

        public bool IsOpen { get; private set; }

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public bool KeyDown(Key key, bool control) => false;

        public void KeyUp(Key key) { }

        public void Character(Rune character) { }

        public void Paste(string text) { }

        public void Scroll(float steps) { }

        public void ReleaseKeys() { }
    }

    private sealed class TypedFocus : ITextInput
    {
        public string Typed { get; private set; } = "";

        public List<Key> Keys { get; } = [];

        public List<Key> Released { get; } = [];

        public bool KeyDown(Key key, bool control)
        {
            Keys.Add(key);
            return false;
        }

        public void KeyUp(Key key) => Released.Add(key);

        public void Character(Rune character) => Typed += character.ToString();

        public void Paste(string text) => Typed += text;
    }
}
