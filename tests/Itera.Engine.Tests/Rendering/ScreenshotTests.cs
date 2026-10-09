using Itera.Engine.Rendering;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Vulkan;
using SkiaSharp;

namespace Itera.Engine.Tests.Rendering;

/// <summary>
/// The screenshot's file (<see cref="ScreenshotEncoder"/>), the one-at-a-time rule (<see cref="Screenshots"/>), and the
/// render thread's collect allocating nothing without a screenshot (<see cref="ScreenshotPass"/>).
/// </summary>
public sealed class ScreenshotTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "itera-screenshot-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Blue_first_bytes_come_out_as_their_colours_with_alpha_opaque()
    {
        // 2x1: a red pixel and a green one, in B G R A order, with alpha 0.
        byte[] pixels = [0, 0, 255, 0, 0, 255, 0, 0];
        var path = Path.Combine(_folder, "bgra.png");

        var size = ScreenshotEncoder.Write(pixels, 2, 1, blueFirst: true, new ScreenshotRequest(path));

        Assert.Equal((2, 1), size);
        using var image = SKBitmap.Decode(path);
        Assert.Equal(new SKColor(255, 0, 0, 255), image.GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 255, 0, 255), image.GetPixel(1, 0));
    }

    [Fact]
    public void Red_first_bytes_are_read_in_their_order()
    {
        byte[] pixels = [0, 0, 255, 7];
        var path = Path.Combine(_folder, "rgba.png");

        ScreenshotEncoder.Write(pixels, 1, 1, blueFirst: false, new ScreenshotRequest(path));

        using var image = SKBitmap.Decode(path);
        Assert.Equal(new SKColor(0, 0, 255, 255), image.GetPixel(0, 0));
    }

    [Fact]
    public void The_crop_is_cut_before_the_scale()
    {
        // 8x4: the left half black, the right half white.
        var pixels = new byte[8 * 4 * 4];
        for (var y = 0; y < 4; y++)
        {
            for (var x = 4; x < 8; x++)
            {
                pixels.AsSpan((y * 8 + x) * 4, 4).Fill(255);
            }
        }

        var path = Path.Combine(_folder, "crop.png");
        var size = ScreenshotEncoder.Write(pixels, 8, 4, true, new ScreenshotRequest(path, 0.5f, (4, 0, 4, 4)));

        Assert.Equal((2, 2), size);
        using var image = SKBitmap.Decode(path);
        Assert.Equal(2, image.Width);
        Assert.Equal(new SKColor(255, 255, 255, 255), image.GetPixel(0, 0));
        Assert.Equal(new SKColor(255, 255, 255, 255), image.GetPixel(1, 1));
    }

    [Fact]
    public void A_crop_outside_the_picture_is_refused()
    {
        var pixels = new byte[4 * 4 * 4];
        var path = Path.Combine(_folder, "outside.png");

        Assert.Throws<ArgumentException>(() =>
            ScreenshotEncoder.Write(pixels, 4, 4, true, new ScreenshotRequest(path, 1, (2, 2, 4, 4)))
        );
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void One_screenshot_at_a_time_and_each_frame_takes_it_once()
    {
        var screenshots = new Screenshots(_folder);
        var first = new ScreenshotRequest(screenshots.NewPath());
        var second = new ScreenshotRequest(screenshots.NewPath());

        Assert.Null(screenshots.Request(first));
        Assert.NotNull(screenshots.Request(second));
        Assert.Same(first, screenshots.TakeForFrame());
        Assert.Null(screenshots.TakeForFrame());
        // Still on its way: refused until it is written or failed.
        Assert.NotNull(screenshots.Request(second));
        first.Fail("test");
        Assert.Null(screenshots.Request(second));
        Assert.StartsWith(_folder, first.Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Format.B8G8R8A8Srgb, true, true)]
    [InlineData(Format.B8G8R8A8Unorm, true, true)]
    [InlineData(Format.R8G8B8A8Srgb, true, false)]
    [InlineData(Format.A2B10G10R10UnormPack32, false, false)]
    public void Only_formats_of_8_bits_a_channel_are_read(Format format, bool read, bool blueFirst)
    {
        Assert.Equal(read, ScreenshotPass.TryBlueFirst(format, out var blue));
        Assert.Equal(blueFirst, blue);
    }

    [Fact]
    public void Collecting_with_no_screenshot_allocates_nothing()
    {
        // The render thread collects every slot after its fence; with nothing copied it touches no Vulkan object.
        using var pass = new ScreenshotPass(null!, null!, null!, NullLogger<ScreenshotPass>.Instance);
        pass.Collect(0);

        var before = SystemAllocation.StartMeasurement();
        for (var slot = 0; slot < FrameSync.FramesInFlight; slot++)
        {
            pass.Collect(slot);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
