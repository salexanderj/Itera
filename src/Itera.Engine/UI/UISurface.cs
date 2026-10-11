using System.Runtime.InteropServices;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>The rows of the UI one <see cref="UISurface.Draw"/> changed, and whether it shows anything.</summary>
/// <param name="RowStart">First changed row.</param>
/// <param name="Rows">Changed rows; 0 when nothing changed.</param>
/// <param name="Visible">True when any layer shows anything.</param>
public readonly record struct UiDraw(int RowStart, int Rows, bool Visible);

/// <summary>
/// The UI's pixels: a Skia CPU surface the size of the frame's image, in memory this class owns (four bytes a pixel,
/// Skia's BGRA 8888 premultiplied, rows one after another), which <c>UiPass</c> copies to the GPU and draws over the
/// picture. It draws its <see cref="IUiLayer"/>s in order, each clipped to its <see cref="IUiLayer.Bounds"/>. It redraws
/// only the area of the layers that changed (a layer dirty, appearing, going, or with new bounds: what it covers now and
/// covered before), clearing that area and drawing again every visible layer that meets it, and reports that area's rows
/// (a small bar's step under a toolbar once redrew and sent the whole screen).
/// The whole surface when the size changes or after <see cref="Invalidate"/>. An unchanged UI costs nothing per frame.
/// Main thread.
/// </summary>
public sealed unsafe partial class UISurface : IDisposable
{
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
    /// Updates the layers, then repaints the area of those that changed on the surface at <paramref name="width"/> x
    /// <paramref name="height"/> (the frame's image), and returns its rows. Once per frame.
    /// </summary>
    public UiDraw Draw(in FrameTime time, int width, int height)
    {
        if (_surface is null || width != Width || height != Height)
        {
            CreateSurface(width, height);
        }

        var full = new SKRectI(0, 0, Width, Height);
        var area = _redrawAll ? full : SKRectI.Empty;
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
                area = Union(area, Union(bounds, _drawnBounds[i]));
            }

            _wasVisible[i] = shown;
        }

        if (area == SKRectI.Empty || _surface is null)
        {
            return new UiDraw(0, 0, visible);
        }

        var canvas = _surface.Canvas;
        canvas.Save();
        canvas.ClipRect(area);
        canvas.Clear(SKColors.Transparent);
        for (var i = 0; i < _layers.Length; i++)
        {
            var bounds = _bounds[i];
            _drawnBounds[i] = bounds;
            if (!bounds.IntersectsWith(area))
            {
                continue;
            }

            // A layer never leaves pixels outside its bounds, which a later redraw of another area would not clear.
            canvas.Save();
            canvas.ClipRect(bounds);
            _layers[i].Draw(canvas, Width, Height);
            canvas.Restore();
        }

        canvas.Restore();
        canvas.Flush();
        _redrawAll = false;
        return new UiDraw(area.Top, area.Height, visible);
    }

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
