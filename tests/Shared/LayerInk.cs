using Itera.Engine.UI;
using SkiaSharp;

namespace Itera.TestSupport;

/// <summary>
/// What a UI layer draws outside its <see cref="IUiLayer.Bounds"/>. <c>UISurface</c> clips each layer to its bounds and
/// redraws only the area that changed, so a layer's ink past them would be cut off; each layer's tests check none is.
/// </summary>
internal static class LayerInk
{
    /// <summary>
    /// The pixels <paramref name="layer"/> draws outside its bounds on a cleared, unclipped surface of
    /// <paramref name="width"/> x <paramref name="height"/>, and the pixels it draws in all (which must be some, so the
    /// check looks at a drawn layer).
    /// </summary>
    public static (int Outside, int Drawn) Outside(IUiLayer layer, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            layer.Draw(canvas, width, height);
            canvas.Flush();
        }

        var bounds = layer.Bounds(width, height);
        var outside = 0;
        var drawn = 0;
        var pixels = bitmap.GetPixelSpan();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                if (pixels[offset + 3] == 0)
                {
                    continue;
                }

                drawn++;
                outside += bounds.Contains(x, y) ? 0 : 1;
            }
        }

        return (outside, drawn);
    }
}
