using Itera.Engine.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.GLFW;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Itera.Engine.Windowing;

/// <summary>
/// Owns the OS window, or stands in for it without one (<c>Engine:Window:Mode=Offscreen</c>: no events, the size from
/// the options, and a headless surface). All calls must happen on the main thread, except <see cref="Wake"/>.
/// </summary>
public sealed partial class WindowService : IDisposable
{
    private readonly WindowSettings _settings;
    private readonly ILogger<WindowService> _logger;
    private IWindow? _window;
    private Glfw? _glfw;

    /// <summary>Set by <see cref="Wake"/>; what <see cref="WaitEvents"/> sleeps on.</summary>
    private readonly AutoResetEvent _wake = new(false);
    private bool _abandoned;

    public WindowService(IOptions<EngineOptions> options, ILogger<WindowService> logger)
    {
        _settings = options.Value.Window;
        _logger = logger;
    }

    /// <summary>Raised after the framebuffer changes size, with the new size in pixels.</summary>
    public event Action<Vector2D<int>>? FramebufferResized;

    /// <summary>Raised when the window gains (true) or loses (false) input focus.</summary>
    public event Action<bool>? FocusChanged;

    /// <summary>
    /// True while the window has input focus. Assumed true after creation, because GLFW focuses a new window and not
    /// every platform reports that first focus.
    /// </summary>
    public bool IsFocused { get; private set; } = true;

    /// <summary>
    /// The refresh rate of the screen the window is on, in Hz, or 0 when unknown (before the window exists, or when no
    /// screen says): the screen whose size the window's framebuffer has (full screen), the highest of those if several
    /// do, and otherwise the highest of every screen. GLFW cannot tell which screen holds a window under Wayland (on
    /// 2026-10-05 it named the 100 Hz screen for a window full screen on the 240 Hz one), and the highest never holds
    /// dynamic render scaling under a rate the screen could show. Read when the window is made and when its state or
    /// size changes, which a move to another screen in full screen brings.
    /// </summary>
    public int RefreshRate { get; private set; }

    /// <summary>The window. Throws before <see cref="Initialize"/>, and in a run without a window.</summary>
    public IWindow Window => _window ?? throw new InvalidOperationException("There is no window.");

    /// <summary>True while an OS window exists: not before <see cref="Initialize"/>, and never offscreen.</summary>
    public bool HasWindow => _window is not null;

    /// <summary>True when the run draws without a window (<c>Engine:Window:Mode=Offscreen</c>).</summary>
    public bool Offscreen => _settings.Mode == WindowMode.Offscreen;

    /// <summary>False when the run has no graphics at all (<c>Engine:Window:Mode=None</c>): no Vulkan, nothing drawn.</summary>
    public bool Graphics => _settings.Mode != WindowMode.None;

    /// <summary>
    /// The least time between two frames the render thread starts, in seconds: offscreen a frame cap
    /// (<c>Engine:Window:OffscreenFps</c>), since nothing paces a headless surface; 0 with a window, which presenting paces.
    /// </summary>
    public double FrameCapSeconds => Offscreen ? 1.0 / _settings.OffscreenFps : 0;

    /// <summary>True once the OS asked the window to close; never without a window.</summary>
    public bool IsClosing => _window?.IsClosing ?? false;

    /// <summary>Where the Vulkan surface comes from: the window's, or a headless one offscreen. After <see cref="Initialize"/>.</summary>
    public ISurfaceSource SurfaceSource =>
        Offscreen
            ? new HeadlessSurfaceSource()
            : new WindowSurfaceSource(
                Window.VkSurface ?? throw new InvalidOperationException("The window has no Vulkan surface.")
            );

    /// <summary>
    /// Puts the window over its whole screen, or back in a window; the resize follows as any resize does. Main thread;
    /// nothing before the window exists.
    /// </summary>
    public void SetFullscreen(bool fullscreen)
    {
        if (_window is not null)
        {
            _window.WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal;
        }
    }

    /// <summary>Set when the user closes the window or the game asks to exit.</summary>
    public bool ExitRequested { get; private set; }

    /// <summary>Why <see cref="ExitRequested"/> was set, for the shutdown log. Null while no exit is requested.</summary>
    public string? ExitReason { get; private set; }

    /// <summary>
    /// Framebuffer size in pixels. Differs from the logical size under display scaling. Offscreen, the options' size.
    /// </summary>
    public Vector2D<int> FramebufferSize =>
        _window is null ? new Vector2D<int>(_settings.Width, _settings.Height) : Window.FramebufferSize;

    /// <summary>
    /// Framebuffer pixels per unit of the window's size across, 1 to 4: 2 on a Retina Mac, 1 where they agree. Null before
    /// the window exists.
    /// </summary>
    public float? ContentScale =>
        _window is { } window && window.Size.X > 0
            ? Math.Clamp(window.FramebufferSize.X / (float)window.Size.X, 1, 4)
            : null;

    /// <summary>Creates the window without entering Silk.NET's own loop; offscreen, none.</summary>
    public void Initialize(bool fullscreen = false)
    {
        if (Offscreen)
        {
            LogOffscreen(_settings.Width, _settings.Height);
            return;
        }

        if (!Graphics)
        {
            LogNoGraphics();
            return;
        }

        // libsdl2 may be present on the system; make sure Silk.NET picks GLFW.
        Silk.NET.Windowing.Window.PrioritizeGlfw();

        var options = WindowOptions.DefaultVulkan with
        {
            Title = _settings.Title,
            Size = new Vector2D<int>(_settings.Width, _settings.Height),
            IsEventDriven = false,
            WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal,
        };

        _window = Silk.NET.Windowing.Window.Create(options);
        _window.FramebufferResize += OnFramebufferResize;
        _window.FocusChanged += OnFocusChanged;
        _window.StateChanged += OnStateChanged;
        _window.Closing += OnClosing;
        _window.Initialize();
        _glfw = Glfw.GetApi();

        if (_window.VkSurface is null)
        {
            throw new InvalidOperationException("The windowing backend did not provide a Vulkan surface.");
        }

        var backend = $"GLFW {_glfw.GetVersionString()}";
        LogWindowCreated(_window.Size.X, _window.Size.Y, _window.FramebufferSize.X, _window.FramebufferSize.Y, backend);
        ReadRefreshRate();
    }

    /// <summary>Reads the refresh rate of the window's screen (<see cref="RefreshRate"/>), and logs it when it changed.</summary>
    private void ReadRefreshRate()
    {
        if (_window is null)
        {
            return;
        }

        var size = _window.FramebufferSize;
        var matched = 0;
        var matchedName = "";
        var highest = 0;
        var highestName = "";
        foreach (var monitor in Silk.NET.Windowing.Monitor.GetMonitors(_window))
        {
            var mode = monitor.VideoMode;
            var rate = mode.RefreshRate ?? 0;
            if (rate > highest)
            {
                highest = rate;
                highestName = monitor.Name;
            }

            if (mode.Resolution == size && rate > matched)
            {
                matched = rate;
                matchedName = monitor.Name;
            }
        }

        var (refresh, screen, how) =
            matched > 0 ? (matched, matchedName, "its size") : (highest, highestName, "the highest");
        if (refresh != RefreshRate)
        {
            RefreshRate = refresh;
            LogRefreshRate(screen, refresh, how);
        }
    }

    /// <summary>
    /// The window's title as the options set it (<c>Engine:Window:Title</c>), before any <see cref="SetTitle"/>.
    /// </summary>
    public string BaseTitle => _settings.Title;

    /// <summary>
    /// Sets the window's title, for debug readouts until the HUD exists (a tool's readout names what it shows). Main
    /// thread; call it when the text changes, not every frame.
    /// </summary>
    public void SetTitle(string title)
    {
        if (_window is not null)
        {
            _window.Title = title;
        }
    }

    /// <summary>Asks the loop to stop after the current frame.</summary>
    /// <param name="reason">Short description for the log, for example "Escape key".</param>
    /// <param name="exitCode">The process's exit code, when the run ends cleanly; the first request's counts.</param>
    public void RequestExit(string reason, int exitCode = 0)
    {
        if (!ExitRequested)
        {
            ExitCode = exitCode;
        }

        ExitReason ??= reason;
        ExitRequested = true;
    }

    /// <summary>
    /// The exit code the first <see cref="RequestExit"/> asked for (the console's <c>quit CODE</c>, or 1 for a startup
    /// script that failed); 0 otherwise.
    /// </summary>
    public int ExitCode { get; private set; }

    /// <summary>Pumps OS events. Input and resize callbacks fire from here. Nothing without a window.</summary>
    public void PollEvents()
    {
        _window?.DoEvents();
    }

    /// <summary>
    /// Handles the OS events waiting, like <see cref="PollEvents"/>, sleeps until <see cref="Wake"/> is called or
    /// <paramref name="timeoutSeconds"/> pass, then handles those that came meanwhile, so the steps and the frame after
    /// the wake read them (they would wait for the next iteration, a frame's input
    /// up to a step old). The loop uses it when there is nothing to draw, so it does not spin.
    /// </summary>
    /// <remarks>
    /// GLFW's own wait (<c>glfwWaitEventsTimeout</c>, GLFW 3.4.0 on Wayland) once kept the main thread at full load
    /// inside the call for 16 minutes, from a window focus change on (2026-09-27), and never returned. Its polling
    /// returns at once, so the sleep is the loop's own, with a poll on each side of it.
    /// </remarks>
    public void WaitEvents(double timeoutSeconds)
    {
        PollEvents();
        // A timeout under a millisecond still yields the core.
        _wake.WaitOne(Math.Max(1, (int)(timeoutSeconds * 1000)));
        PollEvents();
    }

    /// <summary>
    /// Makes a <see cref="WaitEvents"/> in progress, or the next one, return at once. Any thread, also after
    /// <see cref="Dispose"/>, when it does nothing.
    /// </summary>
    public void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
            // The loop has ended; nothing waits.
        }
    }

    /// <summary>
    /// Leaves the window to the OS at exit instead of destroying it: the render thread is stuck inside a present on the
    /// window's surface, and destroying the surface under it is unsafe. Main thread, at shutdown.
    /// </summary>
    public void Abandon()
    {
        _abandoned = true;
    }

    public void Dispose()
    {
        _wake.Dispose();
        if (_window is null || _abandoned)
        {
            return;
        }

        _window.Reset();
        _window.Dispose();
        _window = null;
    }

    private void OnFramebufferResize(Vector2D<int> size)
    {
        LogFramebufferResized(size.X, size.Y);
        ReadRefreshRate();
        FramebufferResized?.Invoke(size);
    }

    private void OnFocusChanged(bool focused)
    {
        LogFocusChanged(focused);
        IsFocused = focused;
        FocusChanged?.Invoke(focused);
    }

    private void OnStateChanged(WindowState state)
    {
        LogStateChanged(state);
        ReadRefreshRate();
    }

    private void OnClosing()
    {
        LogCloseRequested();
        RequestExit("window closed");
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Window created: {Width}x{Height} logical, {FbWidth}x{FbHeight} framebuffer, {Backend}"
    )]
    private partial void LogWindowCreated(int width, int height, int fbWidth, int fbHeight, string backend);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Window: none (offscreen): drawn at {Width}x{Height} into a headless surface; input only from the control port"
    )]
    private partial void LogOffscreen(int width, int height);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Window: none, and no graphics (Engine:Window:Mode=None): the steps and the frame group run, nothing is drawn"
    )]
    private partial void LogNoGraphics();

    [LoggerMessage(Level = LogLevel.Information, Message = "Window: refresh rate {Hz} Hz, of screen {Screen} ({How})")]
    private partial void LogRefreshRate(string screen, int hz, string how);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Framebuffer resized to {Width}x{Height}")]
    private partial void LogFramebufferResized(int width, int height);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Window focus: {Focused}")]
    private partial void LogFocusChanged(bool focused);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Window state: {State}")]
    private partial void LogStateChanged(WindowState state);

    [LoggerMessage(Level = LogLevel.Information, Message = "Window close requested")]
    private partial void LogCloseRequested();
}
