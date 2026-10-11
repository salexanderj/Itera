using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.UI.Toolkit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="UiFonts"/>' sizing rule: the text <c>Engine:Ui:FontPixels</c> units high, a unit a pixel of a 1080-pixel
/// screen scaled with the screen's height and the UI scale, never under 10 pixels; the UI scale the toolkit's (the
/// player's setting), and a layer's font resized when either changes (<see cref="LayerFont"/>).
/// </summary>
public sealed class UiFontsTests
{
    [Fact]
    public void The_text_size_follows_the_screens_height_and_the_ui_scale()
    {
        var options = new EngineOptions();
        options.Ui.FontPixels = 18;
        using var fonts = new UiFonts(Options.Create(options), NullLogger<UiFonts>.Instance);

        Assert.Equal(18, fonts.Pixels(1080), 3);
        Assert.Equal(12, fonts.Pixels(720), 3);
        Assert.Equal(24, fonts.Pixels(1440), 3);
        Assert.Equal(48, fonts.Pixels(1440, share: 2), 3);
        // Never under 10 pixels, however small the screen.
        Assert.Equal(Ui.LeastTextPixels, fonts.Pixels(400));

        // The toolkit's scale is the fonts': the settings' slider moves both.
        using var toolkit = new UiToolkit(
            fonts,
            MenuStyle.Plain,
            Options.Create(options),
            NullLogger<UiToolkit>.Instance
        );
        toolkit.Scale = 1.5f;
        Assert.Equal(1.5f, fonts.UiScale);
        Assert.Equal(27, fonts.Pixels(1080), 3);
        Assert.Equal(toolkit.UnitPixels(720), fonts.UnitPixels(720));
    }

    [Fact]
    public void A_layers_font_is_resized_when_the_screen_or_the_scale_changes()
    {
        using var fonts = new UiFonts(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
        using var font = new LayerFont(fonts, share: 2);

        font.Fit(720);
        Assert.Equal(fonts.Pixels(720, 2), font.Font.Size, 3);
        Assert.False(font.Fit(720));

        Assert.True(font.Fit(1440));
        Assert.Equal(fonts.Pixels(1440, 2), font.Font.Size, 3);
        Assert.Equal(fonts.UnitPixels(1440), font.Unit);

        fonts.UiScale = 0.75f;
        Assert.True(font.Fit(1440));
        Assert.Equal(fonts.Pixels(1440, 2), font.Font.Size, 3);
    }
}
