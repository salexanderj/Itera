using Itera.Engine.Loading;
using Itera.Engine.Rendering;
using Itera.Engine.UI;
using Itera.Modding;
using SkiaSharp;

namespace Game;

/// <summary>
/// The minimap on the HUD: its image view (<see cref="Minimap"/>) shown in a square in the screen's top-right corner,
/// <see cref="ScreenShare"/> of the screen's height (<see cref="RenderViews.Show"/>, which the UI pass draws under the
/// UI's pixels), below the layer it keeps clear of while that shows (the engine's stats line, also top-right), with a
/// thin dark frame round it and a white arrow at its middle for the player, turned to where they look (north up). Shown
/// while <see cref="Minimap.Shown"/> and the loading screen is not drawn. Main thread.
/// </summary>
public sealed class MinimapLayer(
    Minimap minimap,
    RenderViews views,
    LoadingScreen loading,
    IUiLayer? keepClearOf = null
) : IMatchUiLayer, IDisposable
{
    /// <summary>The map's side as a share of the screen's height.</summary>
    public const float ScreenShare = 0.2f;

    /// <summary>The frame's width in pixels, outside the map.</summary>
    public const int FramePixels = 2;

    /// <summary>The arrow's length as a share of the map's side.</summary>
    private const float ArrowShare = 0.07f;

    /// <summary>The arrow is drawn again once the look has turned this far since, in radians (about a degree).</summary>
    private const float YawStep = 0.0175f;

    private SKPaint? _frame;
    private SKPaint? _arrow;
    private SKPaint? _outline;
    private SKPath? _arrowPath;
    private bool _drawn;
    private bool _shown;
    private float _drawnYaw;

    public bool Visible => minimap.Shown && !loading.Drawn;

    public bool Dirty => !_drawn || Visible != _shown || (Visible && MathF.Abs(minimap.Yaw - _drawnYaw) > YawStep);

    public void Update(in FrameTime time) { }

    /// <summary>
    /// The map's square with its frame; and, since the surface asks every frame the layer shows, at the screen's size, the
    /// map's view shown there this frame.
    /// </summary>
    public SKRectI Bounds(int width, int height)
    {
        var map = MapOn(width, height);
        if (Visible)
        {
            views.Show(minimap.View, map.Left, map.Top, map.Width, map.Height);
        }

        return SKRectI.Inflate(map, FramePixels, FramePixels);
    }

    public void Draw(SKCanvas canvas, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _drawn = true;
        _shown = Visible;
        _drawnYaw = minimap.Yaw;
        if (!_shown)
        {
            return;
        }

        _frame ??= new SKPaint
        {
            Color = new SKColor(16, 16, 16, 200),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = FramePixels,
        };
        _arrow ??= new SKPaint { Color = SKColors.White, IsAntialias = true };
        _outline ??= new SKPaint
        {
            Color = new SKColor(16, 16, 16, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            StrokeJoin = SKStrokeJoin.Round,
        };
        _arrowPath ??= ArrowPath();

        var map = MapOn(width, height);
        canvas.DrawRect(SKRect.Inflate(map, FramePixels * 0.5f, FramePixels * 0.5f), _frame);
        var length = Math.Max(map.Width * ArrowShare, 8);
        canvas.Save();
        canvas.Translate(map.MidX, map.MidY);
        // A yaw turning left (towards -x, west) turns the arrow anticlockwise on the north-up map; Skia turns clockwise.
        canvas.RotateRadians(-minimap.Yaw);
        canvas.Scale(length);
        _outline.StrokeWidth = 2 / length;
        canvas.DrawPath(_arrowPath, _outline);
        canvas.DrawPath(_arrowPath, _arrow);
        canvas.Restore();
    }

    /// <summary>
    /// The map's square on a <paramref name="width"/> x <paramref name="height"/> screen, in its top-right corner, a
    /// margin in from its edges and below <paramref name="below"/> (a pixel row other ink reaches down to).
    /// </summary>
    public static SKRectI Map(int width, int height, int below = 0)
    {
        var side = Math.Max((int)MathF.Round(height * ScreenShare), 32);
        var margin = Math.Max((int)MathF.Round(height / 60f), 8);
        return SKRectI.Create(width - margin - side, Math.Max(margin, below + margin), side, side);
    }

    /// <summary>The map's square this frame: below the layer it keeps clear of while that shows.</summary>
    private SKRectI MapOn(int width, int height) =>
        Map(width, height, keepClearOf is { Visible: true } clear ? clear.Bounds(width, height).Bottom : 0);

    public void Dispose()
    {
        _frame?.Dispose();
        _arrow?.Dispose();
        _outline?.Dispose();
        _arrowPath?.Dispose();
        _frame = null;
        _arrow = null;
        _outline = null;
        _arrowPath = null;
    }

    /// <summary>An arrowhead pointing up (-y), one long, its middle at the origin.</summary>
    private static SKPath ArrowPath()
    {
        using var path = new SKPathBuilder();
        path.MoveTo(0, -0.55f);
        path.LineTo(0.38f, 0.45f);
        path.LineTo(0, 0.25f);
        path.LineTo(-0.38f, 0.45f);
        path.Close();
        return path.Detach();
    }
}
