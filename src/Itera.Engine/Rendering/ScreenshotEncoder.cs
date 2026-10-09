using SkiaSharp;

namespace Itera.Engine.Rendering;

/// <summary>
/// Turns a copied picture into a PNG file: cut to the request's crop, scaled, alpha made opaque. Its bytes are the
/// swapchain's, 4 a pixel in blue-green-red or red-green-blue order, already sRGB-encoded; they go into the file as
/// they are. Any thread.
/// </summary>
public static class ScreenshotEncoder
{
    /// <summary>
    /// Writes <paramref name="pixels"/> (<paramref name="width"/> by <paramref name="height"/>, rows packed) to the
    /// request's path. Returns the written size. Changes <paramref name="pixels"/>' alpha. Throws on a crop outside the
    /// picture or a file that cannot be written.
    /// </summary>
    public static (int Width, int Height) Write(
        byte[] pixels,
        int width,
        int height,
        bool blueFirst,
        ScreenshotRequest request
    )
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(request);
        if (pixels.Length < width * height * 4)
        {
            throw new ArgumentException("fewer bytes than the picture's size", nameof(pixels));
        }

        // The swapchain's alpha is whatever the passes left; the file shows the picture as the screen does.
        for (var i = 3; i < width * height * 4; i += 4)
        {
            pixels[i] = 255;
        }

        var (x, y, cutWidth, cutHeight) = request.Crop ?? (0, 0, width, height);
        if (x < 0 || y < 0 || cutWidth <= 0 || cutHeight <= 0 || x + cutWidth > width || y + cutHeight > height)
        {
            throw new ArgumentException(
                $"the crop {x},{y} {cutWidth}x{cutHeight} is not inside the {width}x{height} picture"
            );
        }

        var info = new SKImageInfo(
            width,
            height,
            blueFirst ? SKColorType.Bgra8888 : SKColorType.Rgba8888,
            SKAlphaType.Opaque
        );
        using var whole = new SKBitmap();
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixels,
            System.Runtime.InteropServices.GCHandleType.Pinned
        );
        try
        {
            if (!whole.InstallPixels(info, handle.AddrOfPinnedObject(), width * 4))
            {
                throw new InvalidOperationException("Skia did not take the picture's pixels");
            }

            using var cut = new SKBitmap();
            if (!whole.ExtractSubset(cut, new SKRectI(x, y, x + cutWidth, y + cutHeight)))
            {
                throw new InvalidOperationException("Skia did not cut the picture");
            }

            var outWidth = Math.Max(1, (int)MathF.Round(cutWidth * request.Scale));
            var outHeight = Math.Max(1, (int)MathF.Round(cutHeight * request.Scale));
            using var scaled =
                outWidth == cutWidth && outHeight == cutHeight
                    ? cut.Copy()
                    : cut.Resize(
                        new SKImageInfo(outWidth, outHeight, SKColorType.Rgba8888, SKAlphaType.Opaque),
                        new SKSamplingOptions(SKCubicResampler.Mitchell)
                    );
            if (scaled is null)
            {
                throw new InvalidOperationException("Skia did not scale the picture");
            }

            using var data = scaled.Encode(SKEncodedImageFormat.Png, 100);
            var folder = Path.GetDirectoryName(Path.GetFullPath(request.Path));
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            using (var file = File.Create(request.Path))
            {
                data.SaveTo(file);
            }

            return (outWidth, outHeight);
        }
        finally
        {
            handle.Free();
        }
    }
}
