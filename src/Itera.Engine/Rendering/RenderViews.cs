namespace Itera.Engine.Rendering;

/// <summary>A declared image view: its name, its size in texels, and how often it is drawn.</summary>
/// <param name="Name">The view's name, unique in the run.</param>
/// <param name="Width">Width of the view's image in texels.</param>
/// <param name="Height">Height of the view's image in texels.</param>
/// <param name="EveryFrames">The most often the view is drawn: once in this many frames.</param>
public readonly record struct RenderViewInfo(string Name, int Width, int Height, int EveryFrames);

/// <summary>A view shown on the screen this frame, in a rectangle of screen pixels (x and y from the top-left).</summary>
/// <param name="View">The view's index.</param>
/// <param name="X">Left edge in pixels.</param>
/// <param name="Y">Top edge in pixels.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public readonly record struct ViewPanel(int View, float X, float Y, float Width, float Height);

/// <summary>
/// Image views: views of the world that the engine draws into an image of their own (a game's minimap is the first
/// user). A game declares each at the run's start (its <c>IRunStartup.Start</c>, the run's "game" step) with a name, a
/// size in texels, and how often it is drawn. Each frame, a frame system asks for a view to be drawn with
/// <see cref="Draw"/> and a camera; the engine draws it on a frame it was asked for once at least
/// <see cref="RenderViewInfo.EveryFrames"/> frames have passed since it was last drawn. A HUD layer may show a view on
/// the screen this frame with <see cref="Show"/>. Views are drawn without shadows and particles: the world, the models,
/// the mesh assets, the sky, and the point lights. At most <see cref="MostViews"/> views. Main thread only; the frame's
/// calls allocate nothing.
/// </summary>
public sealed class RenderViews
{
    /// <summary>Views a run can declare.</summary>
    public const int MostViews = 4;

    /// <summary>The least width or height of a view, in texels.</summary>
    public const int LeastTexels = 16;

    /// <summary>The most width or height of a view, in texels.</summary>
    public const int MostTexels = 4096;

    /// <summary>The most <see cref="RenderViewInfo.EveryFrames"/> a view can have.</summary>
    public const int MostEveryFrames = 60;

    private readonly RenderViewInfo[] _infos = new RenderViewInfo[MostViews];
    private readonly bool[] _asked = new bool[MostViews];
    private readonly CameraView[] _cameras = new CameraView[MostViews];
    private readonly bool[] _everDrawn = new bool[MostViews];
    private readonly int[] _framesSinceDrawn = new int[MostViews];
    private readonly ViewPanel[] _panels = new ViewPanel[MostViews];
    private readonly int[] _panelOf = new int[MostViews];
    private int _panelCount;
    private bool _sealed;

    public RenderViews()
    {
        Array.Fill(_panelOf, -1);
    }

    /// <summary>The number of views declared.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Declares a view and returns its index (0, 1, ...). Call it at the run's start, before the engine makes the
    /// views' images; later it throws.
    /// </summary>
    /// <param name="name">The view's name, not empty and not declared before (compared ordinally).</param>
    /// <param name="width">Width in texels, <see cref="LeastTexels"/> to <see cref="MostTexels"/>.</param>
    /// <param name="height">Height in texels, <see cref="LeastTexels"/> to <see cref="MostTexels"/>.</param>
    /// <param name="everyFrames">The view is drawn once in this many frames, 1 to <see cref="MostEveryFrames"/>.</param>
    /// <exception cref="ArgumentException">The name is empty or used already, or a number is out of range.</exception>
    /// <exception cref="InvalidOperationException">
    /// There are <see cref="MostViews"/> views already, or the views are sealed.
    /// </exception>
    public int Declare(string name, int width, int height, int everyFrames = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Find(name) >= 0)
        {
            throw new ArgumentException($"A view named '{name}' is declared already.", nameof(name));
        }

        if (width is < LeastTexels or > MostTexels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                width,
                $"A view's width is {LeastTexels} to {MostTexels} texels."
            );
        }

        if (height is < LeastTexels or > MostTexels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(height),
                height,
                $"A view's height is {LeastTexels} to {MostTexels} texels."
            );
        }

        if (everyFrames is < 1 or > MostEveryFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(everyFrames),
                everyFrames,
                $"A view is drawn once in 1 to {MostEveryFrames} frames."
            );
        }

        if (_sealed)
        {
            throw new InvalidOperationException(
                "Views are declared at the run's start; the engine has made their images."
            );
        }

        if (Count == MostViews)
        {
            throw new InvalidOperationException($"A run has at most {MostViews} views.");
        }

        _infos[Count] = new RenderViewInfo(name, width, height, everyFrames);
        return Count++;
    }

    /// <summary>The index of the view named <paramref name="name"/> (compared ordinally), or -1.</summary>
    public int Find(string name)
    {
        for (var i = 0; i < Count; i++)
        {
            if (string.Equals(_infos[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The declaration of view <paramref name="view"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">No view has that index.</exception>
    public RenderViewInfo Info(int view)
    {
        CheckIndex(view);
        return _infos[view];
    }

    /// <summary>
    /// Asks for view <paramref name="view"/> to be drawn this frame from <paramref name="camera"/> (orthographic or
    /// perspective). The engine draws it if at least its <see cref="RenderViewInfo.EveryFrames"/> frames have passed
    /// since it was last drawn. A second ask in a frame replaces the first.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No view has that index.</exception>
    public void Draw(int view, in CameraView camera)
    {
        CheckIndex(view);
        _asked[view] = true;
        _cameras[view] = camera;
    }

    /// <summary>
    /// Shows view <paramref name="view"/> on the screen this frame, in a rectangle of screen pixels (<paramref name="x"/>
    /// and <paramref name="y"/> from the top-left). A second call in a frame for the same view replaces its rectangle. A
    /// call with a position that is not finite, or a size that is not finite and above 0, shows nothing and leaves an
    /// earlier rectangle of the frame as it is.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No view has that index.</exception>
    public void Show(int view, float x, float y, float width, float height)
    {
        CheckIndex(view);
        if (
            !float.IsFinite(x)
            || !float.IsFinite(y)
            || !float.IsFinite(width)
            || !float.IsFinite(height)
            || width <= 0
            || height <= 0
        )
        {
            return;
        }

        var slot = _panelOf[view];
        if (slot < 0)
        {
            slot = _panelCount++;
            _panelOf[view] = slot;
        }

        _panels[slot] = new ViewPanel(view, x, y, width, height);
    }

    /// <summary>
    /// Whether a frame system asked for view <paramref name="view"/> this frame (<see cref="Draw"/>), and the camera it
    /// asked with.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">No view has that index.</exception>
    public bool Asked(int view, out CameraView camera)
    {
        CheckIndex(view);
        camera = _cameras[view];
        return _asked[view];
    }

    /// <summary>
    /// Ends the declarations, once the engine has made the views' images (after the game step). A later
    /// <see cref="Declare"/> throws.
    /// </summary>
    internal void Seal() => _sealed = true;

    /// <summary>
    /// Starts a frame, before the frame systems: forgets the last frame's asks and panels, and counts one more frame
    /// since each view was drawn.
    /// </summary>
    internal void BeginFrame()
    {
        ClearFrame();
        for (var i = 0; i < Count; i++)
        {
            if (_framesSinceDrawn[i] < MostEveryFrames)
            {
                _framesSinceDrawn[i]++;
            }
        }
    }

    /// <summary>
    /// Whether view <paramref name="view"/> is to be drawn this frame: it was asked for, and either was never drawn or
    /// has had at least its <see cref="RenderViewInfo.EveryFrames"/> frames since. Gives the camera it was asked with.
    /// </summary>
    internal bool IsDue(int view, out CameraView camera)
    {
        CheckIndex(view);
        camera = _cameras[view];
        return _asked[view] && (!_everDrawn[view] || _framesSinceDrawn[view] >= _infos[view].EveryFrames);
    }

    /// <summary>The engine drew view <paramref name="view"/> this frame: its count of frames starts again at 0.</summary>
    internal void Drawn(int view)
    {
        CheckIndex(view);
        _everDrawn[view] = true;
        _framesSinceDrawn[view] = 0;
    }

    /// <summary>The views shown on the screen this frame, at most <see cref="MostViews"/>, in the order first shown.</summary>
    internal ReadOnlySpan<ViewPanel> Panels => _panels.AsSpan(0, _panelCount);

    /// <summary>A match ended: no asks or panels, and every view counts as never drawn.</summary>
    internal void EndMatch()
    {
        ClearFrame();
        for (var i = 0; i < MostViews; i++)
        {
            _everDrawn[i] = false;
            _framesSinceDrawn[i] = 0;
        }
    }

    private void ClearFrame()
    {
        Array.Clear(_asked);
        Array.Fill(_panelOf, -1);
        _panelCount = 0;
    }

    private void CheckIndex(int view)
    {
        if ((uint)view >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(view), view, "No view has this index.");
        }
    }
}
