using Itera.Engine.UI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="UiFonts"/>' text size: the option's pixels before the window exists, then times the screen's framebuffer
/// pixels per window unit, read once and kept (a Retina Mac drew the console at half
/// size).
/// </summary>
public sealed class UiFontsTests
{
    [Fact]
    public void The_text_size_follows_the_screens_scale_once_the_window_exists()
    {
        var options = new EngineOptions();
        options.Ui.FontPixels = 18;
        float? scale = null;
        using var fonts = new UiFonts(Options.Create(options), NullLogger<UiFonts>.Instance, () => scale);

        Assert.Equal(18, fonts.Pixels);
        Assert.Equal(1, fonts.Scale);

        scale = 2;
        Assert.Equal(36, fonts.Pixels);

        // Kept: the layers made their fonts at this size.
        scale = 1;
        Assert.Equal(36, fonts.Pixels);
    }
}
