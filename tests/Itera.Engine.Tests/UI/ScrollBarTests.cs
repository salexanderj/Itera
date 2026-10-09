using System.Numerics;
using Itera.Engine.Input;
using Itera.Engine.UI.Menu;
using SkiaSharp;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="ScrollBar"/>: the first row shown stays within the list; the wheel moves a row a step, keeping parts of
/// a step; a press above or below the thumb pages; a press on the thumb drags it with the pointer until the release;
/// presses elsewhere, and a release after a page, are not its own; with every row shown it takes presses on it but moves nothing.
/// </summary>
public sealed class ScrollBarTests
{
    private readonly ScrollBar _bar = new() { Track = new SKRect(100, 100, 120, 500) };

    [Fact]
    public void The_first_row_stays_within_the_list()
    {
        _bar.SetRows(10, 4);
        Assert.Equal(6, _bar.MostFirst);
        Assert.True(_bar.MoveTo(50));
        Assert.Equal(6, _bar.First);

        // A shorter list moves it back.
        Assert.True(_bar.SetRows(8, 4));
        Assert.Equal(4, _bar.First);
        Assert.True(_bar.SetRows(3, 4));
        Assert.Equal(0, _bar.First);
        Assert.True(_bar.Idle);
    }

    [Fact]
    public void The_wheel_moves_a_row_a_step_and_keeps_parts_of_a_step()
    {
        _bar.SetRows(10, 4);
        Assert.True(_bar.Scroll(-2));
        Assert.Equal(2, _bar.First);
        Assert.False(_bar.Scroll(-0.5f));
        Assert.True(_bar.Scroll(-0.5f));
        Assert.Equal(3, _bar.First);
        Assert.True(_bar.Scroll(5));
        Assert.Equal(0, _bar.First);
    }

    [Fact]
    public void A_press_on_the_track_pages_and_on_the_thumb_drags_it()
    {
        _bar.SetRows(10, 4);
        var thumb = _bar.Thumb();
        Assert.Equal(100, thumb.Top);
        Assert.Equal(160, thumb.Height, 3);

        // Below the thumb: a page down.
        Assert.True(_bar.Update(Press(110, 400), out var changed));
        Assert.True(changed);
        Assert.Equal(4, _bar.First);
        // Its release is no drag's: not the bar's.
        Assert.False(_bar.Update(Release(110, 400), out _));
        Assert.False(_bar.Dragging);

        // On the thumb: dragged to the bottom, then let go.
        thumb = _bar.Thumb();
        var grab = thumb.MidY;
        Assert.True(_bar.Update(Press(110, grab), out changed));
        Assert.False(changed);
        Assert.True(_bar.Dragging);
        Assert.True(_bar.Update(Held(300, grab + 400), out changed));
        Assert.True(changed);
        Assert.Equal(6, _bar.First);
        Assert.True(_bar.Update(Release(300, grab + 400), out _));
        Assert.False(_bar.Dragging);

        // Elsewhere: not its own.
        Assert.False(_bar.Update(Press(50, 300), out changed));
        Assert.False(changed);
    }

    /// <summary>Its owner ends a drag when it hides the bar, with no release.</summary>
    [Fact]
    public void Ending_a_drag_lets_the_pointer_go()
    {
        _bar.SetRows(10, 4);
        var grab = _bar.Thumb().MidY;
        Assert.True(_bar.Update(Press(110, grab), out _));
        Assert.True(_bar.Dragging);

        _bar.EndDrag();

        Assert.False(_bar.Dragging);
        Assert.False(_bar.Update(Held(110, grab + 300), out var changed));
        Assert.False(changed);
        Assert.Equal(0, _bar.First);
    }

    [Fact]
    public void With_every_row_shown_it_takes_presses_but_moves_nothing()
    {
        _bar.SetRows(3, 4);
        Assert.Equal(_bar.Track, _bar.Thumb());
        Assert.True(_bar.Update(Press(110, 300), out var changed));
        Assert.False(changed);
        Assert.False(_bar.Dragging);
    }

    private static UiPointer Press(float x, float y) => new(new Vector2(x, y), true, true, false, 0);

    private static UiPointer Held(float x, float y) => new(new Vector2(x, y), true, false, false, 0);

    private static UiPointer Release(float x, float y) => new(new Vector2(x, y), false, false, true, 0);
}
