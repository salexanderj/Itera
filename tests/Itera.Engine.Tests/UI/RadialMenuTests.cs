using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Input;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="RadialMenu"/>: the ring's twelve buttons at their hours with 1 at the top, the middle circle's top half
/// and lower quarters, all 1.3 times the size of the first, and nothing in the gaps or between the circle and the
/// ring; a soft click as the pointer moves onto a button, once, and none as it opens under a still pointer; a press and
/// a release on one button choosing it with the fuller click; the hovered button in the hover colour and the selected
/// one sunk; icons drawn in their slots in the label's ink; drawn again only on a change; and no allocation after the
/// first draw.
/// </summary>
public sealed class RadialMenuTests : IDisposable
{
    private const int Width = 1920;
    private const int Height = 1080;
    private static readonly Vector2 s_centre = new(Width / 2f, Height / 2f);

    /// <summary>The menu's size, which its shares are of: 1.3 times the height.</summary>
    private const float Scale = RadialMenu.ScaleShare * Height;

    private readonly UiFonts _fonts = new(Options.Create(new EngineOptions()), NullLogger<UiFonts>.Instance);
    private readonly AudioService _audio = MenuLayerTests.QueuingAudio();
    private readonly RadialMenu _menu;
    private readonly SKBitmap _bitmap = new(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
    private readonly SKCanvas _canvas;

    public RadialMenuTests()
    {
        _menu = new RadialMenu(_fonts, _audio);
        _canvas = new SKCanvas(_bitmap);
    }

    public void Dispose()
    {
        _canvas.Dispose();
        _bitmap.Dispose();
        _menu.Dispose();
        _audio.Dispose();
        _fonts.Dispose();
    }

    [Fact]
    public void The_ring_buttons_lie_at_their_hours_and_the_middle_is_a_top_half_and_two_lower_quarters()
    {
        // Number n (slot n - 1) is n - 1 hours clockwise from the top.
        for (var number = 1; number <= 12; number++)
        {
            Assert.Equal(number - 1, RadialMenu.Hit(AtHour(number - 1, 0.2f), Width, Height));
        }

        // 1 at the top, 4 at the right, 7 at the bottom, 10 at the left.
        Assert.Equal(0, RadialMenu.Hit(s_centre + new Vector2(0, -0.2f * Scale), Width, Height));
        Assert.Equal(3, RadialMenu.Hit(s_centre + new Vector2(0.2f * Scale, 0), Width, Height));
        Assert.Equal(6, RadialMenu.Hit(s_centre + new Vector2(0, 0.2f * Scale), Width, Height));
        Assert.Equal(9, RadialMenu.Hit(s_centre + new Vector2(-0.2f * Scale, 0), Width, Height));

        Assert.Equal(RadialMenu.Top, RadialMenu.Hit(s_centre + new Vector2(10, -0.05f * Scale), Width, Height));
        Assert.Equal(
            RadialMenu.LowerLeft,
            RadialMenu.Hit(s_centre + new Vector2(-0.04f, 0.04f) * Scale, Width, Height)
        );
        Assert.Equal(
            RadialMenu.LowerRight,
            RadialMenu.Hit(s_centre + new Vector2(0.04f, 0.05f) * Scale, Width, Height)
        );

        // Nothing in the middle (where the pointer starts), on the lines between the middle's buttons, between the
        // circle and the ring, past the ring, or on the gap between two buttons; just past the gap, the button.
        Assert.Equal(-1, RadialMenu.Hit(s_centre, Width, Height));
        Assert.Equal(-1, RadialMenu.Hit(s_centre + new Vector2(0, 30), Width, Height));
        Assert.Equal(-1, RadialMenu.Hit(s_centre + new Vector2(-30, 0), Width, Height));
        Assert.Equal(-1, RadialMenu.Hit(AtHour(4, 0.095f), Width, Height));
        Assert.Equal(-1, RadialMenu.Hit(AtHour(4, 0.3f), Width, Height));
        Assert.Equal(-1, RadialMenu.Hit(AtHour(0.5f, 0.2f), Width, Height));
        Assert.Equal(1, RadialMenu.Hit(AtHour(0.6f, 0.2f), Width, Height));
        Assert.Equal(0, RadialMenu.Hit(AtHour(0.4f, 0.2f), Width, Height));
    }

    [Fact]
    public void Moving_onto_a_button_clicks_softly_once_and_opening_under_a_still_pointer_does_not()
    {
        _menu.Open(s_centre);
        Assert.Equal(-1, Point(s_centre));
        Assert.Equal(-1, _menu.Hovered);
        Assert.Equal(0, Clicks(EngineSounds.MenuHover));

        Point(AtHour(3, 0.2f));
        Assert.Equal(3, _menu.Hovered);
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));
        Point(AtHour(3, 0.2f));
        Point(AtHour(3.2f, 0.22f));
        Assert.Equal(1, Clicks(EngineSounds.MenuHover));

        // Off onto a gap, silent, and back.
        Point(AtHour(3.5f, 0.2f));
        Assert.Equal(-1, _menu.Hovered);
        Point(AtHour(3, 0.2f));
        Assert.Equal(2, Clicks(EngineSounds.MenuHover));

        // Opened again with the pointer resting on a button: it is under the pointer, but nothing clicks.
        _menu.Close();
        _menu.Open(AtHour(6, 0.2f));
        Point(AtHour(6, 0.2f));
        Assert.Equal(6, _menu.Hovered);
        Assert.Equal(2, Clicks(EngineSounds.MenuHover));
        Assert.Equal(0, Clicks(EngineSounds.MenuPress));
    }

    [Fact]
    public void A_press_and_a_release_on_one_button_choose_it_with_the_fuller_click()
    {
        _menu.Open(s_centre);
        var left = s_centre + new Vector2(-0.04f, 0.04f) * Scale;
        Point(s_centre);
        Assert.Equal(-1, Update(new UiPointer(left, true, true, false, 0)));
        Assert.Equal(RadialMenu.LowerLeft, Update(new UiPointer(left, false, false, true, 0)));
        Assert.Equal(1, Clicks(EngineSounds.MenuPress));

        // Pressed on one, let go on another: nothing.
        Update(new UiPointer(AtHour(1, 0.2f), true, true, false, 0));
        Assert.Equal(-1, Update(new UiPointer(AtHour(2, 0.2f), false, false, true, 0)));
        // Pressed and let go on nothing: nothing.
        Update(new UiPointer(s_centre, true, true, false, 0));
        Assert.Equal(-1, Update(new UiPointer(s_centre, false, false, true, 0)));
        Assert.Equal(1, Clicks(EngineSounds.MenuPress));

        // Closed, it reads nothing.
        _menu.Close();
        Update(new UiPointer(left, true, true, false, 0));
        Assert.Equal(-1, Update(new UiPointer(left, false, false, true, 0)));
    }

    [Fact]
    public void The_hovered_button_is_drawn_in_the_hover_colour_and_the_selected_one_sunk_with_icons_in_the_ink()
    {
        var style = MenuStyle.Plain;
        var drawn = new List<(SKPoint Centre, float Size, SKColor Ink)>();
        _menu.SetIcon(RadialMenu.LowerLeft, (_, centre, size, ink, _) => drawn.Add((centre, size, ink)));
        _menu.Selected = RadialMenu.LowerRight;
        _menu.Open(s_centre);
        Point(s_centre);
        Point(AtHour(3, 0.2f));
        Draw(style);

        // A ring button's face near its inner edge, away from its number.
        Near(style.ButtonHover, Pixel(AtHour(3, 0.12f)));
        Near(style.Button, Pixel(AtHour(9, 0.12f)));
        // The middle's faces, away from their icons: the lower right sunk, the lower left and the top plain.
        Near(style.Field, Pixel(s_centre + new Vector2(0.075f, 0.02f) * Scale));
        Near(style.Button, Pixel(s_centre + new Vector2(-0.075f, 0.02f) * Scale));
        Near(style.Button, Pixel(s_centre + new Vector2(0.06f, -0.02f) * Scale));
        // Nothing past the ring or between the circle and the ring.
        Assert.Equal(0, Pixel(AtHour(3, 0.3f)).Alpha);
        Assert.Equal(0, Pixel(AtHour(3, 0.095f)).Alpha);

        // The lower left's icon, inside it, in the label's ink; then hovered, in the hover label's.
        var icon = Assert.Single(drawn);
        var circle = RadialMenu.CircleShare * Scale;
        Assert.InRange(icon.Centre.X, s_centre.X - circle, s_centre.X);
        Assert.InRange(icon.Centre.Y, s_centre.Y, s_centre.Y + circle);
        Assert.InRange(icon.Size, 0.4f * circle, 0.7f * circle);
        Assert.Equal(style.ButtonText, icon.Ink);
        Point(s_centre + new Vector2(-0.04f, 0.04f) * Scale);
        Draw(style);
        Assert.Equal(style.ButtonTextHover, drawn[^1].Ink);
        Assert.True(RadialMenu.Bounds(Width, Height).Contains(SKRectI.Ceiling(Ring())));
    }

    [Fact]
    public void It_is_drawn_again_only_when_its_look_changes()
    {
        _menu.Open(s_centre);
        Point(s_centre);
        Draw(MenuStyle.Plain);
        Assert.False(_menu.Dirty);

        Point(s_centre);
        Assert.False(_menu.Dirty);
        // The dot moved, on the same nothing.
        Point(s_centre + new Vector2(0, 3));
        Assert.True(_menu.Dirty);
        Draw(MenuStyle.Plain);
        _menu.Selected = -1;
        Assert.False(_menu.Dirty);
        _menu.Selected = RadialMenu.Top;
        Assert.True(_menu.Dirty);
        Draw(MenuStyle.Plain);
        _menu.Close();
        Assert.True(_menu.Dirty);
    }

    [Fact]
    public void Updating_clicking_and_drawing_allocate_nothing_after_the_first_draw()
    {
        _menu.Open(s_centre);
        Point(s_centre);
        Draw(MenuStyle.Plain);
        // Warmed up: the first calls' allocations are .NET compiling them.
        for (var i = 0; i < 50; i++)
        {
            var at = AtHour(1 + i % 12, 0.2f);
            Point(at);
            Update(new UiPointer(at, true, true, false, 0));
            Update(new UiPointer(at, false, false, true, 0));
            Draw(MenuStyle.Plain);
        }

        _audio.ClearQueued();
        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            var at = AtHour(1 + i % 12, 0.2f);
            Point(at);
            Update(new UiPointer(at, true, true, false, 0));
            Update(new UiPointer(at, false, false, true, 0));
            Draw(MenuStyle.Plain);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The point <paramref name="share"/> of the menu's size out from the middle towards hour <paramref name="hour"/>.</summary>
    private static Vector2 AtHour(float hour, float share)
    {
        var angle = hour * MathF.PI / 6;
        return s_centre + share * Scale * new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
    }

    private static SKRect Ring()
    {
        var reach = RadialMenu.OuterRadius(Height);
        return new SKRect(s_centre.X - reach, s_centre.Y - reach, s_centre.X + reach, s_centre.Y + reach);
    }

    private int Point(Vector2 at) => Update(new UiPointer(at, false, false, false, 0));

    private int Update(UiPointer pointer) => _menu.Update(in pointer, Width, Height);

    private void Draw(MenuStyle style)
    {
        _canvas.Clear(SKColors.Transparent);
        _menu.Draw(_canvas, Width, Height, style);
        _canvas.Flush();
    }

    private SKColor Pixel(Vector2 at) => _bitmap.GetPixel((int)at.X, (int)at.Y);

    private static void Near(SKColor expected, SKColor actual)
    {
        var close =
            Math.Abs(expected.Red - actual.Red) <= 3
            && Math.Abs(expected.Green - actual.Green) <= 3
            && Math.Abs(expected.Blue - actual.Blue) <= 3
            && Math.Abs(expected.Alpha - actual.Alpha) <= 3;
        Assert.True(close, $"expected {expected}, drawn {actual}");
    }

    /// <summary>The clicks of recipe <paramref name="name"/> queued so far.</summary>
    private int Clicks(string name)
    {
        Assert.True(_audio.Recipes.TryFind(name, out var id));
        var count = 0;
        foreach (var sound in _audio.QueuedEvents)
        {
            count += sound.Recipe == id ? 1 : 0;
        }

        return count;
    }
}
