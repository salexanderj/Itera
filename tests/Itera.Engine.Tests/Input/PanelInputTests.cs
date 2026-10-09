using System.Numerics;
using Itera.Engine.Input;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Input;

/// <summary>
/// <see cref="InputService"/> with a game's panel set (<see cref="IPanelInput"/>, a window drawn over the world):
/// setting it frees the pointer and lets go of a held attack; its left clicks and the wheel go to
/// <see cref="InputService.MenuPointer"/> and never capture the pointer, other buttons reach nothing, and keys still
/// reach the game; Escape goes to the panel, not the menu, and never quits; clearing it captures the pointer again only
/// when it was captured as it was set, and not while the menu or the console is open or the game's input is blocked.
/// </summary>
public sealed class PanelInputTests : IDisposable
{
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly FakeMenu _menu = new();
    private readonly FakePanel _panel = new();
    private readonly InputService _input;

    public PanelInputTests()
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
    public void Setting_a_panel_frees_the_pointer_and_lets_go_of_a_held_attack()
    {
        Capture();
        _input.HandleMouseDown(MouseButton.Left);
        _input.BeginTick();
        Assert.True(_input.Tick.IsDown(MouseButton.Left));

        _input.SetPanel(_panel);
        _input.Update();
        _input.BeginTick();

        Assert.True(_input.PanelSet);
        Assert.False(_input.PointerCaptured);
        Assert.False(_input.Tick.IsDown(MouseButton.Left));
    }

    [Fact]
    public void A_left_click_and_the_wheel_go_to_the_panel_and_other_buttons_reach_the_game_without_capturing()
    {
        _input.SetPanel(_panel);
        _input.InjectPointer(new Vector2(320, 200));
        _input.HandleMouseDown(MouseButton.Left);
        _input.HandleMouseDown(MouseButton.Right);
        _input.HandleScroll(-3);
        _input.Update();
        _input.BeginFrame();
        _input.BeginTick();

        Assert.Equal(new UiPointer(new Vector2(320, 200), true, true, false, -3), _input.MenuPointer);
        Assert.False(_input.PointerCaptured);
        Assert.False(_input.Tick.WasPressed(MouseButton.Left));
        // Another button reaches the game, as a key would (the panel's own toggle on a mouse button closes it);
        // with the pointer free, the game takes no attack from it.
        Assert.True(_input.Frame.WasPressed(MouseButton.Right));
        Assert.Equal(0, _input.Frame.Scroll);

        _input.HandleMouseUp(MouseButton.Left);
        _input.BeginFrame();
        Assert.Equal(new UiPointer(new Vector2(320, 200), false, false, true, 0), _input.MenuPointer);
    }

    /// <summary>Clearing a panel clears only the one set.</summary>
    [Fact]
    public void Clearing_a_panel_leaves_another_ones_alone()
    {
        _input.SetPanel(_panel);
        _input.ClearPanel(new FakePanel());
        Assert.True(_input.PanelSet);

        _input.ClearPanel(_panel);
        Assert.False(_input.PanelSet);
    }

    [Fact]
    public void Keys_still_reach_the_game_so_walking_goes_on()
    {
        _input.SetPanel(_panel);
        _input.HandleKeyDown(Key.W, null);
        _input.HandleKeyDown(Key.Z, null);
        _input.BeginFrame();
        _input.BeginTick();

        Assert.True(_input.Tick.IsDown(Key.W));
        Assert.True(_input.Frame.WasPressed(Key.Z));
    }

    [Fact]
    public void Escape_goes_to_the_panel_not_the_menu_and_never_quits()
    {
        Capture();
        _input.SetPanel(_panel);
        _input.Update();
        _input.HandleKeyDown(Key.Escape, null);
        _input.HandleKeyDown(Key.Escape, null);
        _input.BeginTick();

        Assert.Equal(2, _panel.Escapes);
        Assert.Equal(0, _menu.Escapes);
        Assert.False(_input.Tick.WasPressed(Key.Escape));
        Assert.False(_window.ExitRequested);

        // With the menu open (over the panel), Escape is the menu's.
        _menu.IsOpen = true;
        _input.HandleKeyDown(Key.Escape, null);
        Assert.Equal(2, _panel.Escapes);
        Assert.Equal(1, _menu.Escapes);
    }

    [Fact]
    public void Clearing_the_panel_captures_the_pointer_again_only_when_it_was_captured()
    {
        Capture();
        _input.SetPanel(_panel);
        _input.Update();
        Assert.False(_input.PointerCaptured);

        _input.SetPanel(null);
        _input.Update();
        Assert.False(_input.PanelSet);
        Assert.True(_input.PointerCaptured);

        // Free when set: still free when cleared, and a click after it captures as before.
        _input.SetInjectedCapture(false);
        _input.Update();
        _input.SetPanel(_panel);
        _input.Update();
        _input.SetPanel(null);
        _input.Update();
        Assert.False(_input.PointerCaptured);
    }

    [Fact]
    public void Clearing_the_panel_under_the_menu_or_with_the_input_blocked_leaves_the_pointer_free()
    {
        Capture();
        _input.SetPanel(_panel);
        _input.Update();
        _menu.IsOpen = true;
        _input.SetPanel(null);
        _input.Update();
        Assert.False(_input.PointerCaptured);

        _menu.IsOpen = false;
        Capture();
        _input.SetPanel(_panel);
        _input.Update();
        _input.SetGameInputBlocked(true);
        _input.SetPanel(null);
        _input.Update();
        Assert.False(_input.PointerCaptured);
    }

    [Fact]
    public void Setting_the_same_panel_again_changes_nothing()
    {
        Capture();
        _input.SetPanel(_panel);
        _input.Update();
        _input.SetPanel(_panel);
        _input.SetPanel(null);
        _input.Update();

        Assert.True(_input.PointerCaptured);
    }

    [Fact]
    public void The_frame_input_says_when_the_games_input_is_blocked()
    {
        Assert.False(Blocked(_input));

        _input.SetGameInputBlocked(true);

        Assert.True(Blocked(_input));
    }

    /// <summary>What a frame system sees: the input through <see cref="IFrameInput"/>.</summary>
    private static bool Blocked(InputService input) => Assert.IsAssignableFrom<IFrameInput>(input).GameInputBlocked;

    /// <summary>Captures the pointer as the control port does, with no window.</summary>
    private void Capture()
    {
        _input.SetInjectedCapture(true);
        _input.Update();
        Assert.True(_input.PointerCaptured);
    }

    private sealed class FakePanel : IPanelInput
    {
        public int Escapes { get; private set; }

        public void Escape() => Escapes++;
    }

    private sealed class FakeMenu : IMenuInput
    {
        public bool IsOpen { get; set; }

        public IKeyboardFocus? TextFocus => null;

        public int Escapes { get; private set; }

        public bool CapturingBinding => false;

        public void Escape() => Escapes++;

        public void BindingPressed(InputBinding binding) { }
    }
}
