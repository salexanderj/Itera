using Itera.Engine.Input;
using SkiaSharp;

namespace Itera.Engine.UI.Menu;

/// <summary>
/// A vertical scroll bar beside a list of rows of which only some show: a track, and a thumb as long as the share of
/// rows shown, placed by the first row shown. A press on the thumb drags it; a press on the track above or below it
/// pages by the rows shown; the wheel moves a row a step (its owner passes the wheel on while the pointer is over the
/// list too, <see cref="Scroll"/>). Its owner lays it out (<see cref="Track"/>) and draws it. Allocates nothing. Main
/// thread.
/// </summary>
public sealed class ScrollBar
{
    /// <summary>The thumb's shortest length, as a share of the track's width, so a long list's thumb can still be caught.</summary>
    private const float ShortestThumb = 1.5f;

    private float _grabbedAt;
    private float _wheel;

    /// <summary>Where it is drawn and read, in framebuffer pixels.</summary>
    public SKRect Track { get; set; }

    /// <summary>The list's rows.</summary>
    public int Rows { get; private set; }

    /// <summary>The rows shown at once.</summary>
    public int Shown { get; private set; }

    /// <summary>The first row shown.</summary>
    public int First { get; private set; }

    /// <summary>The most <see cref="First"/> can be: 0 when every row shows.</summary>
    public int MostFirst => Math.Max(0, Rows - Shown);

    /// <summary>True while the thumb is dragged.</summary>
    public bool Dragging { get; private set; }

    /// <summary>True when every row shows, so there is nothing to scroll (it may be drawn, but does nothing).</summary>
    public bool Idle => MostFirst == 0;

    /// <summary>Sets the list's size, keeping the first row shown within it; true when <see cref="First"/> changed.</summary>
    public bool SetRows(int rows, int shown)
    {
        Rows = Math.Max(0, rows);
        Shown = Math.Max(1, shown);
        return MoveTo(First);
    }

    /// <summary>Shows from row <paramref name="first"/> (kept within the list); true when it changed.</summary>
    public bool MoveTo(int first)
    {
        var clamped = Math.Clamp(first, 0, MostFirst);
        if (clamped == First)
        {
            return false;
        }

        First = clamped;
        return true;
    }

    /// <summary>
    /// The wheel turned by <paramref name="steps"/> (positive away from the user, which shows earlier rows): a row a
    /// step, parts of a step kept for the next. True when <see cref="First"/> changed.
    /// </summary>
    public bool Scroll(float steps)
    {
        _wheel += steps;
        var whole = (int)_wheel;
        _wheel -= whole;
        return whole != 0 && MoveTo(First - whole);
    }

    /// <summary>The thumb's box: the whole track when every row shows.</summary>
    public SKRect Thumb()
    {
        var track = Track;
        if (Rows <= Shown || Rows == 0)
        {
            return track;
        }

        var length = Math.Max(track.Height * Shown / Rows, Math.Min(track.Height, ShortestThumb * track.Width));
        var top = track.Top + (track.Height - length) * First / MostFirst;
        return new SKRect(track.Left, top, track.Right, top + length);
    }

    /// <summary>
    /// Ends a drag without a release: when its owner hides it, so the next showing
    /// does not follow the pointer.
    /// </summary>
    public void EndDrag() => Dragging = false;

    /// <summary>
    /// Reads <paramref name="input"/>: a press on the thumb starts a drag, a press on the track above or below it pages,
    /// a drag moves the thumb with the input, and a release ends it. Returns true when the bar took the input (a
    /// press on it, or a drag going on), so its owner does nothing else with it; <paramref name="changed"/> is true when
    /// <see cref="First"/> changed.
    /// </summary>
    public bool Update(in UiPointer input, out bool changed)
    {
        changed = false;
        if (Dragging)
        {
            if (!input.Down || input.Released)
            {
                Dragging = false;
                return true;
            }

            var track = Track;
            var thumb = Thumb();
            var travel = track.Height - thumb.Height;
            if (travel > 0)
            {
                var top = input.Position.Y - _grabbedAt - track.Top;
                changed = MoveTo((int)MathF.Round(top / travel * MostFirst));
            }

            return true;
        }

        if (!input.Pressed || !Track.Contains(input.Position.X, input.Position.Y))
        {
            return false;
        }

        if (Idle)
        {
            return true;
        }

        var box = Thumb();
        if (input.Position.Y < box.Top)
        {
            changed = MoveTo(First - Shown);
        }
        else if (input.Position.Y > box.Bottom)
        {
            changed = MoveTo(First + Shown);
        }
        else
        {
            Dragging = true;
            _grabbedAt = input.Position.Y - box.Top;
        }

        return true;
    }

    /// <summary>
    /// Draws the track in <paramref name="style"/>'s field colour and the thumb in its button colour (its hover colour
    /// while dragged), both outlined in its ink, with <paramref name="fill"/> and <paramref name="stroke"/>, the owner's
    /// paints; nothing but the track when every row shows.
    /// </summary>
    public void Draw(SKCanvas canvas, SKPaint fill, SKPaint stroke, MenuStyle style, float corner)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(stroke);
        ArgumentNullException.ThrowIfNull(style);
        fill.Color = style.Field;
        canvas.DrawRoundRect(Track, corner, corner, fill);
        stroke.Color = style.Ink;
        canvas.DrawRoundRect(Track, corner, corner, stroke);
        if (Idle)
        {
            return;
        }

        var thumb = Thumb();
        fill.Color = Dragging ? style.ButtonHover : style.Button;
        canvas.DrawRoundRect(thumb, corner, corner, fill);
        canvas.DrawRoundRect(thumb, corner, corner, stroke);
    }
}
