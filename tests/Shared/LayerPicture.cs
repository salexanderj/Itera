using Itera.Engine.UI;
using SkiaSharp;

namespace Itera.TestSupport;

/// <summary>
/// A UI layer drawn alone, with no world under it: into a bitmap over a plain background, or a PNG file, so a test (or
/// an agent) can look at a layer's pixels without running the game. Call the layer's <c>Update</c> first.
/// </summary>
internal static class LayerPicture
{
    /// <summary>The background the layer is drawn over: a dark grey, as the world under a menu often is.</summary>
    public static readonly SKColor Background = new(40, 44, 52);

    /// <summary><paramref name="layer"/> drawn at <paramref name="width"/> x <paramref name="height"/> over <see cref="Background"/>.</summary>
    public static SKBitmap Render(IUiLayer layer, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(Background);
        layer.Draw(canvas, width, height);
        canvas.Flush();
        return bitmap;
    }

    /// <summary>Writes <paramref name="layer"/> drawn at <paramref name="width"/> x <paramref name="height"/> to <paramref name="path"/> as a PNG.</summary>
    public static void Save(IUiLayer layer, int width, int height, string path)
    {
        using var bitmap = Render(layer, width, height);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }
}
