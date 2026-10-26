using System.Runtime.InteropServices;
using Itera.Engine.Rendering;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>The bands of rows of the UI one <see cref="UISurface.Draw"/> changed, and whether it shows anything.</summary>
/// <param name="Bands">The changed rows, in up to <see cref="UiBands.MostBands"/> bands; none when nothing changed.</param>
/// <param name="Visible">True when any layer shows anything.</param>
public readonly record struct UiDraw(UiBands Bands, bool Visible)
{
    /// <summary>One band of <paramref name="rows"/> rows from <paramref name="rowStart"/>; none when it has no rows.</summary>
    public UiDraw(int rowStart, int rows, bool visible)
        : this(UiBands.One(rowStart, rows), visible) { }

    /// <summary>The changed rows of every band; 0 when nothing changed.</summary>
    public int Rows => Bands.Rows;
}

/// <summary>
/// The UI's pixels: a Skia CPU surface the size of the frame's image, in memory this class owns (four bytes a pixel,
/// Skia's BGRA 8888 premultiplied, rows one after another), which <c>UiPass</c> copies to the GPU and draws over the
/// picture. It draws its <see cref="IUiLayer"/>s in order, each clipped to its <see cref="IUiLayer.Bounds"/>. It redraws
/// only the areas of the layers that changed (a layer dirty, appearing, going, or with new bounds: what it covers now and
/// covered before), clearing each area and drawing again every visible layer that meets it, and reports the areas' rows
/// (a small bar's step under a toolbar once redrew and sent the whole screen). Changes near one another share an area,
/// and there are at most <see cref="UiBands.MostBands"/>, so a HUD that changes in several corners does not redraw and
/// send everything between them. The whole surface when the size changes or after <see cref="Invalidate"/>. An
/// unchanged UI costs nothing per frame. Main thread.
/// </summary>
public sealed unsafe partial class UISurface : IDisposable
{
    /// <summary>Pixels between two changed areas below which they are redrawn as one.</summary>
    private const int AreaGap = 16;

    private readonly IUiLayer[] _below;
    private readonly IUiLayer[] _above;
    private IUiLayer[] _layers;
    private bool[] _wasVisible;

    /// <summary>Each layer's bounds when it was last drawn, within the surface; empty while it is hidden.</summary>
    private SKRectI[] _drawnBounds;

    /// <summary>Each layer's bounds this frame, within the surface; empty while it is hidden.</summary>
    private SKRectI[] _bounds;
    private readonly ILogger<UISurface> _logger;
    private SKSurface? _surface;
    private byte* _pixels;
    private bool _redrawAll;
    private int _buffers;
    private bool _disposed;

    public UISurface(IEnumerable<IUiLayer> layers, ILogger<UISurface> logger)
    {
        IUiLayer[] run = [.. layers];
        _below = [.. run.Where(layer => !layer.AboveMatch)];
        _above = [.. run.Where(layer => layer.AboveMatch)];
        _layers = [.. _below, .. _above];
        _wasVisible = new bool[_layers.Length];
        _drawnBounds = new SKRectI[_layers.Length];
        _bounds = new SKRectI[_layers.Length];
        _logger = logger;
    }

    /// <summary>The layers drawn, in order (later on top): the run's under the match's, the match's, the run's over them.</summary>
    public IReadOnlyList<IUiLayer> Layers => _layers;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>The surface's pixels, <see cref="Width"/> x <see cref="Height"/> x 4 bytes.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels is null ? default : new ReadOnlySpan<byte>(_pixels, Width * Height * 4);

    /// <summary>Creates the surface at <paramref name="width"/> x <paramref name="height"/> to prove the native library works.</summary>
    public void Initialize(int width, int height)
    {
        CreateSurface(width, height);
        LogInitialized(Width, Height, _layers.Length);
    }

    /// <summary>
    /// Draws <paramref name="layers"/>, the match's (<see cref="IMatchUiLayer"/>), between the run's layers below and above
    /// the match, in their order; an empty list when no match plays. The next <see cref="Draw"/> repaints every row. When a
    /// match starts and ends.
    /// </summary>
    public void SetMatchLayers(IReadOnlyList<IUiLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        _layers = [.. _below, .. layers, .. _above];
        _wasVisible = new bool[_layers.Length];
        _drawnBounds = new SKRectI[_layers.Length];
        _bounds = new SKRectI[_layers.Length];
        Invalidate();
    }

    /// <summary>Makes the next <see cref="Draw"/> repaint and report every row.</summary>
    public void Invalidate()
    {
        _redrawAll = true;
    }

    /// <summary>
    /// Follows the GPU's copy of the pixels: when <paramref name="generation"/> (<c>UiPass.Generation</c>) moved, its
    /// buffers were made again and hold nothing, so the next <see cref="Draw"/> repaints and reports every row.
    /// </summary>
    public void FollowBuffers(int generation)
    {
        if (generation != _buffers)
        {
            _buffers = generation;
            Invalidate();
        }
    }

    /// <summary>
    /// Updates the layers, then repaints the areas of those that changed on the surface at <paramref name="width"/> x
    /// <paramref name="height"/> (the frame's image), and returns their rows. Once per frame.
    /// </summary>
    public UiDraw Draw(in FrameTime time, int width, int height)
    {
        if (_surface is null || width != Width || height != Height)
        {
            CreateSurface(width, height);
        }

        var full = new SKRectI(0, 0, Width, Height);
        Span<SKRectI> areas = stackalloc SKRectI[UiBands.MostBands];
        var count = 0;
        if (_redrawAll)
        {
            areas[count++] = full;
        }

        var visible = false;
        for (var i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            layer.Update(in time, Width, Height);
            var shown = layer.Visible;
            visible |= shown;
            var bounds = shown ? Within(layer.Bounds(Width, Height), full) : SKRectI.Empty;
            _bounds[i] = bounds;
            if (shown != _wasVisible[i] || (shown && (layer.Dirty || bounds != _drawnBounds[i])))
            {
                count = AddArea(areas, count, Union(bounds, _drawnBounds[i]));
            }

            _wasVisible[i] = shown;
        }

        if (count == 0 || _surface is null)
        {
            return new UiDraw(default, visible);
        }

        var canvas = _surface.Canvas;
        var bands = default(UiBands);
        for (var a = 0; a < count; a++)
        {
            var area = areas[a];
            canvas.Save();
            canvas.ClipRect(area);
            canvas.Clear(SKColors.Transparent);
            for (var i = 0; i < _layers.Length; i++)
            {
                var bounds = _bounds[i];
                if (!bounds.IntersectsWith(area))
                {
                    continue;
                }

                // A layer never leaves pixels outside its bounds, which a later redraw of another area would not clear.
                // A layer that meets two areas is drawn into each.
                canvas.Save();
                canvas.ClipRect(bounds);
                _layers[i].Draw(canvas, Width, Height);
                canvas.Restore();
            }

            canvas.Restore();
            bands = bands.With(area.Top, area.Height);
        }

        _bounds.AsSpan().CopyTo(_drawnBounds);
        canvas.Flush();
        _redrawAll = false;
        return new UiDraw(bands, visible);
    }

    /// <summary>
    /// Adds <paramref name="area"/> to the first <paramref name="count"/> of <paramref name="areas"/>, merged with every
    /// area it meets or comes within <see cref="AreaGap"/> of, until none does. With no room left it is merged into the
    /// area that grows least. Returns the new count.
    /// </summary>
    private static int AddArea(Span<SKRectI> areas, int count, SKRectI area)
    {
        if (area == SKRectI.Empty)
        {
            return count;
        }

        for (var i = 0; i < count; )
        {
            if (Near(areas[i], area))
            {
                area = SKRectI.Union(areas[i], area);
                areas[i] = areas[--count];
                i = 0;
            }
            else
            {
                i++;
            }
        }

        if (count < areas.Length)
        {
            areas[count] = area;
            return count + 1;
        }

        var least = 0;
        var leastGrowth = long.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var growth = Size(SKRectI.Union(areas[i], area)) - Size(areas[i]);
            if (growth < leastGrowth)
            {
                least = i;
                leastGrowth = growth;
            }
        }

        // The grown area may now come near another, so it is added again in the room it left.
        var grown = SKRectI.Union(areas[least], area);
        areas[least] = areas[--count];
        return AddArea(areas, count, grown);
    }

    /// <summary>True when <paramref name="a"/> and <paramref name="b"/> meet or are within <see cref="AreaGap"/> of each other.</summary>
    private static bool Near(SKRectI a, SKRectI b) =>
        a.Left <= b.Right + AreaGap
        && b.Left <= a.Right + AreaGap
        && a.Top <= b.Bottom + AreaGap
        && b.Top <= a.Bottom + AreaGap;

    private static long Size(SKRectI rect) => (long)rect.Width * rect.Height;

    /// <summary>The part of <paramref name="bounds"/> within <paramref name="full"/>; <see cref="SKRectI.Empty"/> when none.</summary>
    private static SKRectI Within(SKRectI bounds, SKRectI full)
    {
        var inside = SKRectI.Intersect(bounds, full);
        return inside.Width > 0 && inside.Height > 0 ? inside : SKRectI.Empty;
    }

    /// <summary>The smallest rectangle holding both; <see cref="SKRectI.Empty"/> adds nothing.</summary>
    private static SKRectI Union(SKRectI a, SKRectI b)
    {
        if (a == SKRectI.Empty)
        {
            return b;
        }

        return b == SKRectI.Empty ? a : SKRectI.Union(a, b);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FreeSurface();
    }

    private void CreateSurface(int width, int height)
    {
        FreeSurface();
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        var rowBytes = Width * 4;
        _pixels = (byte*)NativeMemory.AllocZeroed((nuint)rowBytes * (nuint)Height);
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface =
            SKSurface.Create(info, (nint)_pixels, rowBytes)
            ?? throw new InvalidOperationException($"Skia could not make a {Width}x{Height} surface.");
        Array.Clear(_drawnBounds);
        _redrawAll = true;
    }

    private void FreeSurface()
    {
        _surface?.Dispose();
        _surface = null;
        if (_pixels is not null)
        {
            NativeMemory.Free(_pixels);
            _pixels = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "UI: Skia CPU surface {Width}x{Height}, {Layers} layer(s)")]
    private partial void LogInitialized(int width, int height, int layers);
}
