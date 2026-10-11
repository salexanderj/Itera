using System.Diagnostics;
using System.Numerics;
using System.Text;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;
using Silk.NET.Input;

namespace Itera.Engine.Input;

/// <summary>
/// Wraps Silk.NET input on the main thread. Key, button, movement, and scroll events are collected and handed to frame
/// systems as <see cref="Frame"/> and to fixed systems as <see cref="Tick"/>. A left click in the focused window hides
/// and locks the pointer for mouse look (raw, unaccelerated motion where GLFW offers it). Escape goes to the menu
/// (<see cref="IMenuInput"/>) at once and frees the pointer; without a menu it frees the pointer, and Escape while it is
/// free requests exit (<see cref="PointerCapture"/>).
/// While the menu is open it takes the left button's clicks and the wheel, and none captures the pointer. While the drawn
/// pointer is on (a radial menu's, <see cref="SetDrawnPointer"/>), the captured mouse moves it instead of the view, and
/// its buttons click it instead of reaching the game. While a game's panel is set (<see cref="SetPanel"/>, such as a
/// map screen), the pointer is free, its left clicks and the wheel go to <see cref="MenuPointer"/> without capturing it,
/// and Escape goes to the panel; keys still reach the game. While a game's text box has the typing
/// (<see cref="SetTextFocus"/>), keys and characters go to it instead of the game. A game's screens close on Escape
/// before the menu opens (<see cref="AddEscapeHandler"/>). ` opens the <see cref="IKeyboardFocus"/> (the in-game
/// console) when there is one: until it closes (` or Escape), keys, characters, and the wheel go to it instead of the
/// game, clicks do nothing, and the pointer is free; it is captured again on closing if it was captured on opening.
/// Blocking at the events, not the snapshots, means nothing typed reaches the game later. The control port's
/// <c>input</c> sends keys, buttons, and look movement through the same paths (<see cref="InjectKey"/>,
/// <see cref="InjectButton"/>, <see cref="InjectLook"/>), and captures the pointer without the window's focus or the
/// real pointer (<see cref="SetInjectedCapture"/>).
/// </summary>
public sealed partial class InputService : IFrameInput, ITickInput, IDisposable
{
    private readonly WindowService _windowService;
    private readonly ILogger<InputService> _logger;
    private readonly InputAccumulator _accumulator = new();
    private readonly PointerCapture _pointer = new();
    private IInputContext? _input;
    private IMouse? _mouse;
    private IKeyboardFocus? _focus;
    private IMenuInput? _menu;
    private IPanelInput? _panel;
    private ITextInput? _text;

    /// <summary>
    /// True when characters come from the window's own callback as whole code points (<see cref="OnCodePoint"/>), not
    /// from Silk.NET's <c>KeyChar</c>, which cuts one past U+FFFF to 16 bits.
    /// </summary>
    private bool _codePoints;

    /// <summary>A game's screens that Escape closes before the menu opens, oldest first (<see cref="AddEscapeHandler"/>).</summary>
    private readonly List<IEscapeHandler> _escapeHandlers = [];

    /// <summary>The handlers as Escape went down, so one that removes another as it closes skips none.</summary>
    private IEscapeHandler[] _asking = [];

    /// <summary>The key a text box repeats while it is held (<see cref="ITextInput.KeyDown"/>), or none.</summary>
    private Key _repeatKey = Key.Unknown;
    private bool _repeatControl;
    private double _repeatAt;

    /// <summary>The text box <see cref="_repeatKey"/> went down in; the repeat stops when the typing goes elsewhere.</summary>
    private ITextInput? _repeatTarget;

    /// <summary>True when the pointer was captured as the panel was set: clearing it captures the pointer again.</summary>
    private bool _panelRecapture;

    /// <summary>True when that capture was the control port's (<see cref="SetInjectedCapture"/>).</summary>
    private bool _panelRecaptureInjected;
    private Vector2 _pointerPosition;
    private Vector2? _injectedPointer;
    private bool _uiDown;
    private bool _uiPressed;
    private bool _uiReleased;
    private bool _uiRightDown;
    private bool _uiRightPressed;
    private bool _uiRightReleased;
    private float _uiScroll;

    /// <summary>The menu's navigation keys pressed since the last frame, and the frame's (<see cref="MenuKeys"/>).</summary>
    private UI.Toolkit.UiKeys _menuKeys;
    private UI.Toolkit.UiKeys _frameMenuKeys;
    private bool _shiftHeld;
    private UiPointer _uiPointer;
    private bool _gameInputBlocked;
    private bool _dropNextCharacter;
    private bool _recaptureOnClose;
    private bool _controlHeld;

    /// <summary>True while ` is held, so a repeated key-down from the platform does not toggle the console again.</summary>
    private bool _graveHeld;
    private InputSnapshot _frame;
    private InputSnapshot _tick;
    private Vector2 _lastMousePosition;
    private bool _mousePositionKnown;
    private bool _captured;

    /// <summary>True while the control port holds the capture: the real pointer is left alone, and focus is not needed.</summary>
    private bool _injectedCapture;
    private Vector2 _capturedTravel;
    private long _capturedSince;

    /// <summary>True while the drawn pointer is on (<see cref="SetDrawnPointer"/>).</summary>
    private bool _drawn;
    private Vector2 _drawnCentre;
    private float _drawnRadius;
    private Vector2 _drawnPosition;

    public InputService(WindowService windowService, ILogger<InputService> logger)
    {
        _windowService = windowService;
        _logger = logger;
    }

    /// <summary>Seconds a text box's key is held before it repeats (<see cref="ITextInput.KeyDown"/>).</summary>
    public const double RepeatDelaySeconds = 0.4;

    /// <summary>Repeats a second of a text box's held key after <see cref="RepeatDelaySeconds"/>.</summary>
    public const double RepeatsPerSecond = 30;

    /// <summary>The clock a text box's repeats are timed by, in seconds; a test sets its own.</summary>
    internal Func<double> Clock { get; set; } = static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>The input of the frame being built. Set by <see cref="BeginFrame"/>; read by frame systems.</summary>
    public ref readonly InputSnapshot Frame => ref _frame;

    /// <summary>The input of the fixed step being run. Set by <see cref="BeginTick"/>; read by fixed systems.</summary>
    public ref readonly InputSnapshot Tick => ref _tick;

    /// <summary>
    /// The pointer as the menu reads it in the frame being built (<see cref="IMenuInput"/>): where it is in framebuffer
    /// pixels, and the left button and the wheel while the menu is open; while the drawn pointer is on, where it is and
    /// the left button (<see cref="SetDrawnPointer"/>). Set by <see cref="BeginFrame"/>.
    /// </summary>
    public UiPointer MenuPointer => _uiPointer;

    /// <inheritdoc />
    public bool DrawnPointerOn => _drawn;

    /// <inheritdoc />
    public void SetDrawnPointer(bool shown, Vector2 centre, float radius)
    {
        if (shown == _drawn)
        {
            return;
        }

        _drawn = shown;
        if (!shown)
        {
            _uiDown = false;
            return;
        }

        _drawnCentre = centre;
        _drawnRadius = Math.Max(0, radius);
        _drawnPosition = centre;
        _uiDown = false;
        _uiPressed = false;
        _uiReleased = false;
        // The left button is the drawn pointer's now: an attack held on it stops. The other buttons keep their state, so
        // an action bound to one (the radial menu's own key, Sprint) stays held.
        _accumulator.ButtonUp(MouseButton.Left);
    }

    /// <inheritdoc />
    public void SetPanel(IPanelInput? panel)
    {
        if (ReferenceEquals(panel, _panel))
        {
            return;
        }

        var opening = _panel is null;
        _panel = panel;
        _uiDown = false;
        if (panel is not null)
        {
            if (opening)
            {
                _panelRecapture = _captured;
                _panelRecaptureInjected = _injectedCapture;
                // An attack held on the left button as the panel opens stops; the free pointer is the panel's. Other
                // buttons keep their state.
                _accumulator.ButtonUp(MouseButton.Left);
                _pointer.Release();
            }

            return;
        }

        var recapture =
            _panelRecapture && _menu is not { IsOpen: true } && _focus is not { IsOpen: true } && !_gameInputBlocked;
        if (recapture && _panelRecaptureInjected)
        {
            SetInjectedCapture(true);
        }
        else if (recapture && _windowService.IsFocused)
        {
            _pointer.Capture();
        }

        _panelRecapture = false;
        _panelRecaptureInjected = false;
    }

    /// <inheritdoc />
    public void ClearPanel(IPanelInput panel)
    {
        if (ReferenceEquals(panel, _panel))
        {
            SetPanel(null);
        }
    }

    /// <inheritdoc />
    public UI.Toolkit.UiKeys MenuKeys => _frameMenuKeys;

    /// <inheritdoc />
    public ITextInput? TextFocus => _text;

    /// <inheritdoc />
    public void SetTextFocus(ITextInput? text)
    {
        if (ReferenceEquals(text, _text))
        {
            return;
        }

        if (text is not null && _text is null)
        {
            // The keys are the box's now: a player walking stops, and their releases still reach the game.
            _accumulator.ReleaseKeys();
        }

        _text = text;
    }

    /// <inheritdoc />
    public void ClearTextFocus(ITextInput text)
    {
        if (ReferenceEquals(text, _text))
        {
            SetTextFocus(null);
        }
    }

    /// <inheritdoc />
    public void AddEscapeHandler(IEscapeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_escapeHandlers.Contains(handler))
        {
            _escapeHandlers.Add(handler);
        }
    }

    /// <inheritdoc />
    public void RemoveEscapeHandler(IEscapeHandler handler) => _escapeHandlers.Remove(handler);

    /// <summary>
    /// A match ended: what its game set here goes with it (its text box, its Escape handlers, its panel, and the drawn
    /// pointer), so nothing of an ended match is kept or asked. Main thread.
    /// </summary>
    public void EndMatch()
    {
        _text = null;
        _repeatKey = Key.Unknown;
        _repeatTarget = null;
        _escapeHandlers.Clear();
        _panelRecapture = false;
        SetPanel(null);
        SetDrawnPointer(false, default, 0);
    }

    /// <summary>True while a game's panel takes the free pointer (<see cref="SetPanel"/>).</summary>
    public bool PanelSet => _panel is not null;

    /// <inheritdoc />
    public Vector2 MouseSinceTick => _gameInputBlocked ? Vector2.Zero : _accumulator.MouseSinceTick;

    /// <summary>
    /// True while the game gets no input (the loading screen): its snapshots are empty, keys and clicks are not
    /// collected, and a click does not capture the pointer. Escape still reaches the menu, and the console still opens.
    /// </summary>
    public bool GameInputBlocked => _gameInputBlocked;

    /// <summary>Blocks the game's input or lets it through again (<see cref="GameInputBlocked"/>).</summary>
    public void SetGameInputBlocked(bool blocked)
    {
        if (blocked && !_gameInputBlocked)
        {
            _accumulator.ReleaseAll();
            _pointer.Release();
        }

        _gameInputBlocked = blocked;
    }

    /// <summary>True while the pointer is hidden and locked for mouse look.</summary>
    public bool PointerCaptured => _captured;

    /// <summary>
    /// Captures the pointer for look, or frees it, for the control port: without the window's focus, and leaving the real
    /// pointer alone. Applied at the next <see cref="Update"/>; Escape (or the menu opening) frees it as it frees a real
    /// capture. Main thread.
    /// </summary>
    public void SetInjectedCapture(bool captured)
    {
        if (captured)
        {
            if (!_captured)
            {
                _injectedCapture = true;
                _pointer.Capture();
            }
        }
        else if (_injectedCapture)
        {
            _pointer.Release();
        }
    }

    /// <summary>
    /// A key goes down or up, as if pressed on the keyboard: the console, when open, gets it instead. The console key
    /// also types its character, as a keyboard's does, which opening the console drops (no other key types one: the
    /// control port's <c>type</c> does). Main thread.
    /// </summary>
    public void InjectKey(Key key, bool down)
    {
        if (down)
        {
            HandleKeyDown(key, null);
            if (key == Key.GraveAccent)
            {
                HandleCharacter('`');
            }
        }
        else
        {
            HandleKeyUp(key);
        }
    }

    /// <summary>
    /// A mouse button goes down or up, as if clicked, except that a left click on a free pointer does not capture it
    /// (<see cref="SetInjectedCapture"/> does). Main thread.
    /// </summary>
    public void InjectButton(MouseButton button, bool down)
    {
        if (!down)
        {
            HandleMouseUp(button);
            return;
        }

        if (
            MenuTakesClick(button)
            || _focus is { IsOpen: true }
            || _gameInputBlocked
            || PanelTakesClick(button)
            || DrawnTakesClick(button)
        )
        {
            return;
        }

        if (_pointer.ButtonDown(button, focused: false))
        {
            _accumulator.ButtonDown(button);
        }
    }

    /// <summary>
    /// The pointer moves by <paramref name="delta"/> screen pixels: look, while captured, or the drawn pointer while it is
    /// on. Main thread.
    /// </summary>
    public void InjectLook(Vector2 delta) => Moved(delta);

    /// <summary>What ` opens: the in-game console. Set once, before the main loop.</summary>
    public void SetKeyboardFocus(IKeyboardFocus focus) => _focus = focus;

    /// <summary>
    /// The menu: while it is open it takes the pointer's clicks and the wheel (<see cref="MenuPointer"/>), a click never
    /// captures the pointer, and its text box takes the keys; Escape goes to it at once, freeing the pointer, and never
    /// quits. Set once, before the main loop.
    /// </summary>
    public void SetMenu(IMenuInput menu) => _menu = menu;

    /// <summary>
    /// Puts the pointer at <paramref name="position"/> (framebuffer pixels) for the menu, for the control port; the real
    /// pointer's next move takes it back. Main thread.
    /// </summary>
    public void InjectPointer(Vector2 position) => _injectedPointer = position;

    /// <summary>
    /// Creates the input context for the window. Call after the window exists. Without a window (offscreen) there are no
    /// devices: input comes only from the control port's injections.
    /// </summary>
    public void Initialize()
    {
        if (!_windowService.HasWindow)
        {
            LogDevices(0, 0, 0);
            return;
        }

        _input = _windowService.Window.CreateInput();
        // After the input context, whose own character callback this replaces.
        _codePoints = _windowService.SetCharacterCallback(OnCodePoint);
        LogCharacters(_codePoints ? "the window's callback, whole code points" : "Silk.NET's KeyChar, 16 bits");
        _input.ConnectionChanged += OnConnectionChanged;
        _windowService.FocusChanged += OnFocusChanged;

        foreach (var keyboard in _input.Keyboards)
        {
            Subscribe(keyboard);
        }

        foreach (var mouse in _input.Mice)
        {
            Subscribe(mouse);
        }

        // Silk.NET lists every GLFW joystick slot as a gamepad, connected or not; count only connected devices.
        var keyboards = _input.Keyboards.Count(d => d.IsConnected);
        var mice = _input.Mice.Count(d => d.IsConnected);
        var gamepads = _input.Gamepads.Count(d => d.IsConnected);
        LogDevices(keyboards, mice, gamepads);
    }

    /// <summary>
    /// Captures or frees the pointer as the clicks and Escape presses since the last call asked; losing focus frees it.
    /// Call once per main-loop iteration, after window events. Events themselves arrive while the window pumps them.
    /// </summary>
    public void Update()
    {
        RepeatText(Clock());
        var wanted = _pointer.Update(_injectedCapture || (_mouse is not null && _windowService.IsFocused));
        if (wanted != _captured)
        {
            SetCaptured(wanted);
        }

        if (!wanted)
        {
            // A control-port capture undone before it applied leaves no flag to capture later clicks without focus.
            _injectedCapture = false;
        }
    }

    /// <summary>Turns the input collected since the previous frame into <see cref="Frame"/>. Call once per frame built.</summary>
    public void BeginFrame()
    {
        var frame = _accumulator.TakeFrame(_captured);
        _frame = _gameInputBlocked ? default : frame;
        var position = _drawn ? _drawnPosition : FramebufferPointer();
        _uiPointer = new UiPointer(
            position,
            _uiDown,
            _uiPressed,
            _uiReleased,
            _uiScroll,
            _uiRightPressed,
            _uiRightReleased
        );
        _uiPressed = false;
        _uiReleased = false;
        _uiRightPressed = false;
        _uiRightReleased = false;
        _uiScroll = 0;
        _frameMenuKeys = _menu is { IsOpen: true } ? _menuKeys : UI.Toolkit.UiKeys.None;
        _menuKeys = UI.Toolkit.UiKeys.None;
    }

    /// <summary>
    /// Where the pointer is in framebuffer pixels, which the UI is drawn in: GLFW gives window coordinates, which differ
    /// under display scaling.
    /// </summary>
    private Vector2 FramebufferPointer()
    {
        if (_injectedPointer is { } injected)
        {
            return injected;
        }

        return _pointerPosition * WindowToFramebuffer();
    }

    /// <summary>The framebuffer's pixels per window pixel each way: 1 without a window, more under display scaling.</summary>
    private Vector2 WindowToFramebuffer()
    {
        if (_input is null)
        {
            return Vector2.One;
        }

        var window = _windowService.Window.Size;
        var framebuffer = _windowService.Window.FramebufferSize;
        return window.X > 0 && window.Y > 0
            ? new Vector2((float)framebuffer.X / window.X, (float)framebuffer.Y / window.Y)
            : Vector2.One;
    }

    /// <summary>
    /// Turns the input collected since the previous fixed step into <see cref="Tick"/>. Call once per fixed step, before
    /// the fixed systems run.
    /// </summary>
    public void BeginTick()
    {
        var tick = _accumulator.TakeTick(_captured);
        _tick = _gameInputBlocked ? default : tick;
    }

    public void Dispose()
    {
        if (_input is null)
        {
            return;
        }

        if (_captured)
        {
            SetCaptured(false);
        }

        foreach (var keyboard in _input.Keyboards)
        {
            Unsubscribe(keyboard);
        }

        foreach (var mouse in _input.Mice)
        {
            Unsubscribe(mouse);
        }

        _windowService.FocusChanged -= OnFocusChanged;
        _input.ConnectionChanged -= OnConnectionChanged;
        _input.Dispose();
        _input = null;
        _mouse = null;
    }

    private void SetCaptured(bool captured)
    {
        var mode = CursorMode.Normal;
        if (!_injectedCapture)
        {
            var cursor = _mouse!.Cursor;
            if (captured)
            {
                mode = cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw : CursorMode.Disabled;
            }

            cursor.CursorMode = mode;
        }
        else if (!captured)
        {
            // Escape, the console, or the loading screen freed it: the control port's hold ends too.
            _injectedCapture = false;
        }

        if (!captured)
        {
            // A free pointer is the real one: the drawn pointer ends with the capture.
            SetDrawnPointer(false, default, 0);
        }

        _captured = captured;
        // The first position after a mode change can jump (GLFW moves the virtual cursor); start from it instead.
        _mousePositionKnown = false;
        if (captured)
        {
            // Movement of the free pointer is not look; neither frames nor fixed steps may turn by it.
            _accumulator.ClearMouseDelta();
            _capturedTravel = Vector2.Zero;
            _capturedSince = Stopwatch.GetTimestamp();
            LogPointerCaptured(mode);
        }
        else
        {
            // Movement that kept arriving while the pointer was locked shows the lock worked, even at the window edge.
            var seconds = Stopwatch.GetElapsedTime(_capturedSince).TotalSeconds;
            LogPointerReleased(seconds, _capturedTravel.X, _capturedTravel.Y);
        }
    }

    private void Subscribe(IKeyboard keyboard)
    {
        keyboard.KeyDown += OnKeyDown;
        keyboard.KeyUp += OnKeyUp;
        if (!_codePoints)
        {
            keyboard.KeyChar += OnKeyChar;
        }
    }

    private void Unsubscribe(IKeyboard keyboard)
    {
        keyboard.KeyDown -= OnKeyDown;
        keyboard.KeyUp -= OnKeyUp;
        keyboard.KeyChar -= OnKeyChar;
    }

    private void Subscribe(IMouse mouse)
    {
        mouse.MouseDown += OnMouseDown;
        mouse.MouseUp += OnMouseUp;
        mouse.MouseMove += OnMouseMove;
        mouse.Scroll += OnScroll;
        _mouse ??= mouse;
    }

    private void Unsubscribe(IMouse mouse)
    {
        mouse.MouseDown -= OnMouseDown;
        mouse.MouseUp -= OnMouseUp;
        mouse.MouseMove -= OnMouseMove;
        mouse.Scroll -= OnScroll;
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode) => HandleKeyDown(key, keyboard);

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode) => HandleKeyUp(key);

    /// <summary>Silk.NET's character, cut to 16 bits: only without the window's own callback (<see cref="_codePoints"/>).</summary>
    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        if (!char.IsSurrogate(character))
        {
            HandleCharacter(character);
        }
    }

    private void OnMouseDown(IMouse mouse, MouseButton button) => HandleMouseDown(button);

    private void OnScroll(IMouse mouse, ScrollWheel wheel) => HandleScroll(wheel.Y);

    /// <summary>A key went down. <paramref name="keyboard"/> gives the clipboard for Ctrl+V; null has none (tests).</summary>
    internal void HandleKeyDown(Key key, IKeyboard? keyboard)
    {
        if (IsShortcutKey(key, OperatingSystem.IsMacOS()))
        {
            _controlHeld = true;
        }

        if (key is Key.ShiftLeft or Key.ShiftRight)
        {
            _shiftHeld = true;
        }

        if (key == Key.GraveAccent && _focus is { Enabled: true })
        {
            if (_graveHeld)
            {
                _dropNextCharacter = true;
                return;
            }

            _graveHeld = true;
        }

        _dropNextCharacter = false;
        if (_focus is { IsOpen: true } open)
        {
            if (key is Key.GraveAccent or Key.Escape)
            {
                CloseFocus(open);
                // The ` key also types a character; it must not reach the game or the next opening.
                _dropNextCharacter = key == Key.GraveAccent;
            }
            else if (key == Key.V && _controlHeld)
            {
                open.Paste(keyboard?.ClipboardText ?? "");
            }
            else
            {
                open.KeyDown(key, _controlHeld);
            }

            return;
        }

        if (key == Key.GraveAccent && _focus is { Enabled: true } focus)
        {
            // A key the menu waits for to bind is not this one: the console opens, and the menu stops waiting.
            if (_menu is { CapturingBinding: true } waiting)
            {
                waiting.BindingPressed(InputBinding.None);
            }

            OpenFocus(focus);
            return;
        }

        if (key != Key.Escape && _menu is { CapturingBinding: true } binding)
        {
            binding.BindingPressed(InputBinding.Of(key));
            return;
        }

        if (key != Key.Escape && _menu?.TextFocus is { } text && key != Key.Tab)
        {
            // The menu's text box takes the keys but Tab (` still opens the console).
            Type(text, key, keyboard);
            return;
        }

        if (
            _menu is { IsOpen: true }
            && MenuKey(key, _shiftHeld) is var navigation
            && navigation != UI.Toolkit.UiKeys.None
        )
        {
            // The menu's keyboard focus: Tab, the arrows, Enter, Space.
            _menuKeys |= navigation;
            return;
        }

        if (_text is { } typing && GameTakesInput)
        {
            // A game's text box takes every key, Escape and Enter too (` opened the console above).
            Type(typing, key, keyboard);
            return;
        }

        if (key == Key.Escape && GameTakesInput && EscapeHandled())
        {
            // A game's screen closed; the menu stays shut.
            return;
        }

        if (key == Key.Escape && _panel is { } panel && _menu is not { IsOpen: true })
        {
            // The panel takes Escape: it closes, and the menu stays shut.
            panel.Escape();
            return;
        }

        if (key == Key.Escape && _menu is { } menu)
        {
            // The menu takes Escape at once, captured pointer or not: back, resume, or the
            // menu over the match, which frees the pointer. It never reaches the game.
            _pointer.Release();
            menu.Escape();
            return;
        }

        if (!_gameInputBlocked)
        {
            _accumulator.KeyDown(key);
        }

        if (key == Key.Escape && _pointer.Escape())
        {
            // Without a menu: the first Escape frees the pointer, the next quits.
            LogEscape();
            _windowService.RequestExit("Escape key");
        }
    }

    /// <summary>A key went up.</summary>
    internal void HandleKeyUp(Key key)
    {
        if (IsShortcutKey(key, OperatingSystem.IsMacOS()))
        {
            _controlHeld = false;
        }

        if (key is Key.ShiftLeft or Key.ShiftRight)
        {
            _shiftHeld = false;
        }

        if (key == Key.GraveAccent)
        {
            _graveHeld = false;
        }

        if (key == _repeatKey)
        {
            _repeatKey = Key.Unknown;
        }

        // Held keys are released for the game too, so none stays held after the console closes.
        _accumulator.KeyUp(key);
        if (_focus is { IsOpen: true } open)
        {
            open.KeyUp(key);
        }
        else
        {
            _text?.KeyUp(key);
        }
    }

    /// <summary>A key's navigation for a menu's keyboard focus (Shift+Tab goes back); none for any other key.</summary>
    internal static UI.Toolkit.UiKeys MenuKey(Key key, bool shift) =>
        key switch
        {
            Key.Tab => shift ? UI.Toolkit.UiKeys.Previous : UI.Toolkit.UiKeys.Next,
            Key.Left => UI.Toolkit.UiKeys.Left,
            Key.Right => UI.Toolkit.UiKeys.Right,
            Key.Up => UI.Toolkit.UiKeys.Up,
            Key.Down => UI.Toolkit.UiKeys.Down,
            Key.Enter or Key.KeypadEnter or Key.Space => UI.Toolkit.UiKeys.Press,
            _ => UI.Toolkit.UiKeys.None,
        };

    /// <summary>True while a game's text box and screens may take keys: the menu is shut and the game's input not blocked.</summary>
    private bool GameTakesInput => _menu is not { IsOpen: true } && !_gameInputBlocked;

    /// <summary>
    /// A key for a text box: Ctrl+V pastes the clipboard, any other goes to the box, and one the box repeats while held
    /// is sent again from <see cref="Update"/> until it comes up.
    /// </summary>
    private void Type(ITextInput box, Key key, IKeyboard? keyboard)
    {
        if (key == Key.V && _controlHeld)
        {
            box.Paste(keyboard?.ClipboardText ?? "");
            return;
        }

        var repeats = box.KeyDown(key, _controlHeld);
        _repeatKey = repeats ? key : Key.Unknown;
        _repeatControl = _controlHeld;
        _repeatTarget = box;
        _repeatAt = Clock() + RepeatDelaySeconds;
    }

    /// <summary>The text box that has the typing now: the menu's, else the game's while it may take keys; none while the console is open.</summary>
    private ITextInput? TypingTarget =>
        _focus is { IsOpen: true } ? null
        : _menu?.TextFocus is { } menu ? menu
        : GameTakesInput ? _text
        : null;

    /// <summary>
    /// Repeats a text box's held key when it is due, while the typing stays with that box; a long wait repeats a few
    /// times, not a burst of every repeat it missed.
    /// </summary>
    internal void RepeatText(double now)
    {
        if (_repeatKey == Key.Unknown)
        {
            return;
        }

        if (!ReferenceEquals(TypingTarget, _repeatTarget))
        {
            _repeatKey = Key.Unknown;
            _repeatTarget = null;
            return;
        }

        var repeats = 0;
        while (now >= _repeatAt && repeats < 4)
        {
            _repeatTarget!.KeyDown(_repeatKey, _repeatControl);
            _repeatAt += 1 / RepeatsPerSecond;
            repeats++;
        }

        if (now >= _repeatAt)
        {
            _repeatAt = now + 1 / RepeatsPerSecond;
        }
    }

    /// <summary>
    /// Escape offered to the game's screens, the newest added first: true when one took it. They are asked from a copy
    /// made as Escape went down, each only while it is still added, so one that removes others as it closes skips none
    /// and asks none twice.
    /// </summary>
    private bool EscapeHandled()
    {
        var count = _escapeHandlers.Count;
        if (_asking.Length < count)
        {
            _asking = new IEscapeHandler[Math.Max(4, 2 * count)];
        }

        _escapeHandlers.CopyTo(_asking);
        var taken = false;
        for (var i = count - 1; i >= 0 && !taken; i--)
        {
            var handler = _asking[i];
            taken = _escapeHandlers.Contains(handler) && handler.Escape();
        }

        // No ended match's screen is kept here.
        Array.Clear(_asking, 0, count);
        return taken;
    }

    /// <summary>
    /// The key held for the console's and the address box's shortcuts (paste, a word back): Ctrl, and on macOS also
    /// Command.
    /// </summary>
    internal static bool IsShortcutKey(Key key, bool macOS) =>
        key is Key.ControlLeft or Key.ControlRight || (macOS && key is Key.SuperLeft or Key.SuperRight);

    /// <summary>A character was typed (one in the Basic Multilingual Plane; tests).</summary>
    internal void HandleCharacter(char character) => HandleCharacter(new Rune(character));

    /// <summary>A character was typed.</summary>
    internal void HandleCharacter(Rune character)
    {
        if (_dropNextCharacter)
        {
            _dropNextCharacter = false;
            return;
        }

        if (_graveHeld && character.Value == '`')
        {
            // The key's repeats, while it is held after opening the console.
            return;
        }

        if (_focus is { IsOpen: true } open)
        {
            open.Character(character);
        }
        else if (_menu?.TextFocus is { } text)
        {
            text.Character(character);
        }
        else if (_text is { } typing && GameTakesInput)
        {
            typing.Character(character);
        }
    }

    /// <summary>A code point from the window's character callback: whole, also past U+FFFF; one that is no character is dropped.</summary>
    internal void OnCodePoint(uint codePoint)
    {
        if (codePoint <= int.MaxValue && Rune.TryCreate((int)codePoint, out var character))
        {
            HandleCharacter(character);
        }
    }

    private void OpenFocus(IKeyboardFocus focus)
    {
        // Keys held now would stay held for the game while their releases go to the console.
        _accumulator.ReleaseAll();
        _recaptureOnClose = _captured;
        _pointer.Release();
        _dropNextCharacter = true;
        focus.Open();
    }

    private void CloseFocus(IKeyboardFocus focus)
    {
        focus.Close();
        if (_recaptureOnClose && _windowService.IsFocused)
        {
            _pointer.Capture();
        }

        _recaptureOnClose = false;
    }

    /// <summary>A mouse button went down.</summary>
    internal void HandleMouseDown(MouseButton button)
    {
        if (
            MenuTakesClick(button)
            || _focus is { IsOpen: true }
            || _gameInputBlocked
            || PanelTakesClick(button)
            || DrawnTakesClick(button)
        )
        {
            return;
        }

        if (_pointer.ButtonDown(button, _windowService.IsFocused))
        {
            _accumulator.ButtonDown(button);
        }
    }

    /// <summary>
    /// A button went down while the menu is open (and the console closed): the menu takes it, and none captures the
    /// pointer or reaches the game. Only the left button clicks the menu; while the menu waits for a key to bind, any
    /// button goes to it as that key, and clicks nothing.
    /// </summary>
    private bool MenuTakesClick(MouseButton button)
    {
        if (_menu is not { IsOpen: true } || _focus is { IsOpen: true })
        {
            return false;
        }

        if (_menu.CapturingBinding)
        {
            _menu.BindingPressed(InputBinding.Of(button));
            return true;
        }

        if (button == MouseButton.Left)
        {
            _uiDown = true;
            _uiPressed = true;
        }
        else if (button == MouseButton.Right)
        {
            _uiRightDown = true;
            _uiRightPressed = true;
        }

        return true;
    }

    /// <summary>
    /// A button went down while a game's panel is set (and the menu and the console closed): the panel takes the left
    /// and right buttons' clicks; another reaches the game, as a key would (the panel's own toggle bound to a mouse
    /// button closes it), and the free pointer is not captured by it.
    /// </summary>
    private bool PanelTakesClick(MouseButton button)
    {
        if (_panel is null || button is not (MouseButton.Left or MouseButton.Right))
        {
            return false;
        }

        if (button == MouseButton.Left)
        {
            _uiDown = true;
            _uiPressed = true;
        }
        else
        {
            _uiRightDown = true;
            _uiRightPressed = true;
        }

        return true;
    }

    /// <summary>
    /// A button went down while the drawn pointer is on and the pointer captured: the drawn pointer takes the left
    /// button's click; another reaches the game, as a key would (the game takes no attacks while the drawn pointer is on).
    /// </summary>
    private bool DrawnTakesClick(MouseButton button)
    {
        if (!_drawn || !_captured || button != MouseButton.Left)
        {
            return false;
        }

        _uiDown = true;
        _uiPressed = true;
        return true;
    }

    private void OnMouseUp(IMouse mouse, MouseButton button) => HandleMouseUp(button);

    /// <summary>A mouse button came up: for the game, and the menu's click when it went down there.</summary>
    internal void HandleMouseUp(MouseButton button)
    {
        if (button == MouseButton.Left && _uiDown)
        {
            _uiDown = false;
            _uiReleased = true;
        }

        if (button == MouseButton.Right && _uiRightDown)
        {
            _uiRightDown = false;
            _uiRightReleased = true;
        }

        _accumulator.ButtonUp(button);
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        _pointerPosition = position;
        _injectedPointer = null;
        if (_mousePositionKnown)
        {
            Moved(position - _lastMousePosition);
        }

        _lastMousePosition = position;
        _mousePositionKnown = true;
    }

    /// <summary>
    /// The mouse moved by <paramref name="delta"/> window pixels: the drawn pointer's while it is on and the pointer
    /// captured, kept within its radius, else look.
    /// </summary>
    private void Moved(Vector2 delta)
    {
        if (_captured)
        {
            _capturedTravel += Vector2.Abs(delta);
        }

        if (!_drawn || !_captured)
        {
            _accumulator.AddMouseDelta(delta);
            return;
        }

        var offset = _drawnPosition - _drawnCentre + delta * WindowToFramebuffer();
        var length = offset.Length();
        if (length > _drawnRadius)
        {
            offset *= _drawnRadius / length;
        }

        _drawnPosition = _drawnCentre + offset;
    }

    /// <summary>The wheel turned by <paramref name="steps"/>.</summary>
    internal void HandleScroll(float steps)
    {
        if (_focus is { IsOpen: true } open)
        {
            open.Scroll(steps);
            return;
        }

        if (_menu is { IsOpen: true } || _panel is not null || (_drawn && _captured))
        {
            // The radial menu's drawn pointer takes the wheel too, which it does not use, so the game's sizes and camera
            // stay put.
            _uiScroll += steps;
            return;
        }

        _accumulator.AddScroll(steps);
    }

    private void OnFocusChanged(bool focused)
    {
        if (!focused)
        {
            // Release events for keys held now may go to another window.
            _accumulator.ReleaseAll();
            _repeatKey = Key.Unknown;
            _controlHeld = false;
            _graveHeld = false;
            _focus?.ReleaseKeys();
        }
    }

    private void OnConnectionChanged(IInputDevice device, bool connected)
    {
        // A device that appears after startup needs the same handlers as the ones present at startup.
        if (device is IKeyboard keyboard)
        {
            Unsubscribe(keyboard);
            if (connected)
            {
                Subscribe(keyboard);
            }
        }
        else if (device is IMouse mouse)
        {
            Unsubscribe(mouse);
            if (connected)
            {
                Subscribe(mouse);
            }
        }

        var kind = device switch
        {
            IKeyboard => "Keyboard",
            IMouse => "Mouse",
            IGamepad => "Gamepad",
            IJoystick => "Joystick",
            _ => "Input device",
        };

        if (connected)
        {
            LogDeviceConnected(kind, device.Index, device.Name);
        }
        else
        {
            LogDeviceDisconnected(kind, device.Index, device.Name);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Input devices: {Keyboards} keyboard(s), {Mice} mouse/mice, {Gamepads} gamepad(s)"
    )]
    private partial void LogDevices(int keyboards, int mice, int gamepads);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Input: characters from {Source}")]
    private partial void LogCharacters(string source);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exit requested by Escape (the pointer was free)")]
    private partial void LogEscape();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Pointer captured for mouse look ({Mode})")]
    private partial void LogPointerCaptured(CursorMode mode);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Pointer released after {Seconds:F1} s of mouse look; the mouse moved {TravelX:F0} px across and {TravelY:F0} px up and down"
    )]
    private partial void LogPointerReleased(double seconds, float travelX, float travelY);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} {Index} connected: {Name}")]
    private partial void LogDeviceConnected(string kind, int index, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} {Index} disconnected: {Name}")]
    private partial void LogDeviceDisconnected(string kind, int index, string name);
}
