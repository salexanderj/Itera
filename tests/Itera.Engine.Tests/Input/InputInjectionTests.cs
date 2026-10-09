using System.Numerics;
using Itera.Engine.Input;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.Input;

/// <summary>Input the control port sends (<see cref="InputService.InjectKey"/> and the rest), without a window.</summary>
public sealed class InputInjectionTests : IDisposable
{
    private readonly WindowService _window = new(
        Options.Create(new EngineOptions()),
        NullLogger<WindowService>.Instance
    );
    private readonly Focus _focus = new();
    private readonly InputService _input;

    public InputInjectionTests()
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
    public void A_tap_is_pressed_in_one_frame_and_one_step_and_not_held()
    {
        _input.InjectKey(Key.Space, down: true);
        _input.InjectKey(Key.Space, down: false);

        _input.BeginFrame();
        _input.BeginTick();
        Assert.True(_input.Frame.WasPressed(Key.Space));
        Assert.True(_input.Tick.WasPressed(Key.Space));
        Assert.False(_input.Tick.IsDown(Key.Space));

        _input.BeginFrame();
        _input.BeginTick();
        Assert.False(_input.Frame.WasPressed(Key.Space));
        Assert.False(_input.Tick.WasPressed(Key.Space));
    }

    [Fact]
    public void A_held_key_and_button_stay_held_until_let_go()
    {
        _input.InjectKey(Key.W, down: true);
        _input.InjectButton(MouseButton.Right, down: true);
        for (var i = 0; i < 3; i++)
        {
            _input.BeginTick();
            Assert.True(_input.Tick.IsDown(Key.W));
            Assert.True(_input.Tick.IsDown(MouseButton.Right));
        }

        _input.InjectKey(Key.W, down: false);
        _input.InjectButton(MouseButton.Right, down: false);
        _input.BeginTick();
        Assert.False(_input.Tick.IsDown(Key.W));
        Assert.False(_input.Tick.IsDown(MouseButton.Right));
    }

    [Fact]
    public void Keys_go_to_the_open_console_and_nothing_reaches_the_game_while_loading()
    {
        _input.InjectKey(Key.GraveAccent, down: true);
        _input.InjectKey(Key.GraveAccent, down: false);
        Assert.True(_focus.IsOpen);
        _input.InjectKey(Key.W, down: true);
        _input.BeginTick();
        Assert.False(_input.Tick.IsDown(Key.W));
        Assert.Contains(Key.W, _focus.Keys);
        _input.InjectKey(Key.Escape, down: true);
        Assert.False(_focus.IsOpen);

        _input.SetGameInputBlocked(true);
        _input.InjectKey(Key.D, down: true);
        _input.InjectButton(MouseButton.Left, down: true);
        _input.BeginTick();
        Assert.False(_input.Tick.IsDown(Key.D));
        Assert.False(_input.Tick.WasPressed(MouseButton.Left));
    }

    [Fact]
    public void An_injected_capture_looks_without_focus_and_Escape_frees_it_without_quitting()
    {
        _input.SetInjectedCapture(true);
        _input.Update();
        Assert.True(_input.PointerCaptured);

        _input.InjectLook(new Vector2(30, -4));
        _input.BeginFrame();
        Assert.True(_input.Frame.PointerCaptured);
        Assert.Equal(new Vector2(30, -4), _input.Frame.MouseDelta);

        _input.InjectKey(Key.Escape, down: true);
        _input.InjectKey(Key.Escape, down: false);
        _input.Update();
        Assert.False(_input.PointerCaptured);
        Assert.False(_window.ExitRequested);

        // Freed, so the next Escape asks to quit, as with a real pointer.
        _input.InjectKey(Key.Escape, down: true);
        Assert.True(_window.ExitRequested);
    }

    [Fact]
    public void Capture_off_frees_it()
    {
        _input.SetInjectedCapture(true);
        _input.Update();
        _input.SetInjectedCapture(false);
        _input.Update();

        Assert.False(_input.PointerCaptured);
    }

    /// <summary>
    /// A capture undone before it applied leaves nothing behind: a real click later, without the window's focus, does
    /// not capture.
    /// </summary>
    [Fact]
    public void A_capture_undone_before_it_applies_leaves_no_capture_for_later_clicks()
    {
        _input.SetInjectedCapture(true);
        _input.SetInjectedCapture(false);
        _input.Update();
        Assert.False(_input.PointerCaptured);

        _input.HandleMouseDown(MouseButton.Left);
        _input.HandleMouseUp(MouseButton.Left);
        _input.Update();

        Assert.False(_input.PointerCaptured);
    }

    [Fact]
    public void Injecting_allocates_nothing()
    {
        _input.SetInjectedCapture(true);
        _input.Update();
        Step();

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            Step();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private void Step()
    {
        _input.InjectKey(Key.W, down: true);
        _input.InjectButton(MouseButton.Left, down: true);
        _input.InjectLook(new Vector2(1, 1));
        _input.Update();
        _input.BeginFrame();
        _input.BeginTick();
        _input.InjectKey(Key.W, down: false);
        _input.InjectButton(MouseButton.Left, down: false);
    }

    private sealed class Focus : IKeyboardFocus
    {
        public bool Enabled => true;

        public bool IsOpen { get; private set; }

        public List<Key> Keys { get; } = [];

        public void Open() => IsOpen = true;

        public void Close() => IsOpen = false;

        public void KeyDown(Key key, bool control) => Keys.Add(key);

        public void KeyUp(Key key) { }

        public void Character(char character) { }

        public void Paste(string text) { }

        public void Scroll(float steps) { }

        public void ReleaseKeys() { }
    }
}
