using System.Globalization;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Rendering;

/// <summary>
/// One screenshot asked for: where its PNG goes, how it is cut and scaled, and whether it shows the UI; then what came of
/// it. The main thread makes it (<see cref="Screenshots"/>); the render thread copies the picture
/// (<see cref="ScreenshotPass"/>), and a worker writes the file and finishes it. Its result fields are read once
/// <see cref="IsDone"/> is true, from any thread.
/// </summary>
public sealed class ScreenshotRequest
{
    /// <param name="path">The PNG file to write; its folder is made when missing.</param>
    /// <param name="scale">Share of the (cut) picture's size to write, 0.1 to 1.</param>
    /// <param name="crop">The part of the screen to keep, in screen pixels (x, y, width, height); null keeps it all.</param>
    /// <param name="withUi">True: the picture as shown, UI and all; false: before the UI is drawn over it.</param>
    public ScreenshotRequest(
        string path,
        float scale = 1,
        (int X, int Y, int Width, int Height)? crop = null,
        bool withUi = true
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        Scale = Math.Clamp(scale, 0.1f, 1f);
        Crop = crop;
        WithUi = withUi;
    }

    public string Path { get; }

    public float Scale { get; }

    public (int X, int Y, int Width, int Height)? Crop { get; }

    public bool WithUi { get; }

    /// <summary>True once the file is written or the request failed.</summary>
    public bool IsDone => Volatile.Read(ref _done) != 0;

    /// <summary>Why it failed, or null when it did not.</summary>
    public string? Error { get; private set; }

    /// <summary>The written image's width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>The written image's height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>The screen's size when the picture was copied.</summary>
    public (int Width, int Height) ScreenSize { get; private set; }

    private int _done;

    internal void Succeed(int width, int height, int screenWidth, int screenHeight)
    {
        Width = width;
        Height = height;
        ScreenSize = (screenWidth, screenHeight);
        Volatile.Write(ref _done, 1);
    }

    internal void Fail(string error)
    {
        if (IsDone)
        {
            return;
        }

        Error = error;
        Volatile.Write(ref _done, 1);
    }
}

/// <summary>
/// Screenshots asked for by the console's <c>screenshot</c> and the control port's <c>shot</c>: one at a time, handed
/// to the render thread in the next frame built (<see cref="TakeForFrame"/>, into <see cref="RenderFrame"/>). Main thread.
/// </summary>
public sealed class Screenshots
{
    private readonly string _folder;
    private ScreenshotRequest? _pending;
    private ScreenshotRequest? _last;

    /// <summary>False without graphics (<c>Engine:Window:Mode=None</c>): no frame would take a request.</summary>
    private readonly bool _drawn;

    public Screenshots(IOptions<EngineOptions> options)
        : this(
            System.IO.Path.Combine(options.Value.Application.DataDirectory(), "Screenshots"),
            options.Value.Window.Mode != WindowMode.None
        ) { }

    internal Screenshots(string folder, bool drawn = true)
    {
        _folder = folder;
        _drawn = drawn;
    }

    /// <summary>The folder screenshots go to when no path is given.</summary>
    public string Folder => _folder;

    /// <summary>A new file name in <see cref="Folder"/>, from the time now.</summary>
    public string NewPath() =>
        System.IO.Path.Combine(
            _folder,
            "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png"
        );

    /// <summary>
    /// Queues <paramref name="request"/> for the next frame. Returns why not, or null: another screenshot is on its way.
    /// </summary>
    public string? Request(ScreenshotRequest request)
    {
        if (!_drawn)
        {
            return "nothing is drawn without graphics (Engine:Window:Mode=None)";
        }

        ArgumentNullException.ThrowIfNull(request);
        if (_last is { IsDone: false })
        {
            return "a screenshot is already being taken";
        }

        _pending = request;
        _last = request;
        return null;
    }

    /// <summary>
    /// Takes <paramref name="request"/> back when no frame has taken it yet, failing it, so the next request is not refused
    /// for it (a control-port shot that timed out while no frames were built). One a frame took goes on.
    /// </summary>
    public void Withdraw(ScreenshotRequest request)
    {
        if (ReferenceEquals(request, _pending))
        {
            _pending = null;
            request.Fail("withdrawn: no frame took it in time");
        }
    }

    /// <summary>The screenshot the frame being built takes, once; null when none waits.</summary>
    public ScreenshotRequest? TakeForFrame()
    {
        var request = _pending;
        _pending = null;
        return request;
    }
}
