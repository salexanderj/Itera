using Itera.Engine.Loading;
using Itera.Modding;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// The aim dot the camera aims through: a white square in a black one at the screen's centre, two and four pixels
/// across on a side of an even number of pixels, three and five on an odd one (the pixels whose centres lie less than
/// <see cref="WhiteReach"/> and <see cref="BlackReach"/> from the screen's centre along each axis). A match's layer the
/// engine adds before the game's, so it is the HUD's lowest; drawn at the screen's own pixels at any render scale, and
/// hidden while the loading screen is drawn. Main thread.
/// </summary>
public sealed class AimDotLayer : IMatchUiLayer, IDisposable
{
    /// <summary>How far from the screen's centre the white square reaches, in pixels, along each axis.</summary>
    public const float WhiteReach = 1.5f;

    /// <summary>How far from the screen's centre the black square round it reaches, in pixels, along each axis.</summary>
    public const float BlackReach = 2.5f;

    private readonly LoadingScreen _loading;
    private SKPaint? _white;
    private SKPaint? _black;
    private bool _drawn;
    private bool _shown;

    public AimDotLayer(LoadingScreen loading)
    {
        _loading = loading;
    }

    public bool Visible => !_loading.Drawn;

    public bool Dirty => !_drawn || Visible != _shown;

    public void Update(in FrameTime time) { }

    public SKRectI Bounds(int width, int height)
    {
        var square = Square(width, height, BlackReach);
        return SKRectI.Inflate(square, 1, 1);
    }

    public void Draw(SKCanvas canvas, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _drawn = true;
        _shown = Visible;
        if (!_shown)
        {
            return;
        }

        _black ??= new SKPaint { Color = SKColors.Black, IsAntialias = false };
        _white ??= new SKPaint { Color = SKColors.White, IsAntialias = false };
        canvas.DrawRect(Square(width, height, BlackReach), _black);
        canvas.DrawRect(Square(width, height, WhiteReach), _white);
    }

    /// <summary>
    /// The pixels of a <paramref name="width"/> x <paramref name="height"/> screen whose centres lie less than
    /// <paramref name="reach"/> from its centre along each axis.
    /// </summary>
    public static SKRectI Square(int width, int height, float reach)
    {
        var (left, right) = Span(width, reach);
        var (top, bottom) = Span(height, reach);
        return new SKRectI(left, top, right, bottom);
    }

    public void Dispose()
    {
        _white?.Dispose();
        _black?.Dispose();
        _white = null;
        _black = null;
    }

    // The first pixel x with x + 0.5 > size / 2 - reach, and one past the last with x + 0.5 < size / 2 + reach.
    private static (int First, int End) Span(int size, float reach)
    {
        var middle = size * 0.5f - 0.5f;
        return ((int)MathF.Floor(middle - reach) + 1, (int)MathF.Ceiling(middle + reach));
    }
}
