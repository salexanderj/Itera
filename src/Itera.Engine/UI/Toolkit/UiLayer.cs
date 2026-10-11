using Itera.Engine.Input;
using Itera.Modding;
using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// A UI layer described in code (<see cref="Build"/>) with the toolkit: each frame it is shown, its UI is declared
/// with the frame's input matched against the last layout (<see cref="Ui"/>), laid out, and drawn again only when the
/// result changed (a hash of every element and its state, an area's version) or the layer was invalidated
/// (<see cref="Invalidate"/>). Elements are drawn by the style (<see cref="Ui.Style"/>, the toolkit's by default); an
/// <see cref="Ui.Area"/> by <see cref="DrawArea"/>. The pointer is the one menus and panels read
/// (<see cref="IFrameInput.MenuPointer"/>): its position always, its button and the wheel only while the menu is open, the
/// game's panel is set (<see cref="IFrameInput.SetPanel"/>), or the drawn pointer is on, so a HUD layer that takes clicks
/// sets a panel while it does; a layer under the match's (not <see cref="AboveMatch"/>) gets no pointer while the game's
/// input is blocked (the menu open, the loading screen). Navigation keys come from <see cref="Keys"/>. A text box clicked
/// gets the typing (<see cref="IFrameInput.SetTextFocus"/>) until a press elsewhere, the layer is hidden, or the input
/// takes it back. The toolkit's clicks play as the pointer comes onto an element and as one is clicked. A declaration
/// that throws is logged (once until one works again), and the layer shows nothing until one does; a style or an area's
/// drawing that throws is logged the same way, leaving what was drawn before it. Main thread.
/// </summary>
public abstract class UiLayer : IUiLayer, IDisposable
{
    /// <summary>The most scroll areas inside one another that get their bars drawn.</summary>
    private const int MostNestedBars = 16;

    private readonly UiToolkit _toolkit;
    private readonly IFrameInput? _input;
    private readonly int[] _barOrder = new int[MostNestedBars];
    private readonly int[] _barEnd = new int[MostNestedBars];
    private readonly bool[] _barFloating = new bool[MostNestedBars];
    private int _bars;
    private int _width;
    private int _height;
    private int _drawnHash;
    private bool _shownLast;
    private bool _failed;
    private bool _reported;
    private bool _drawReported;
    private bool _invalidated;
    private TextEntry? _gaveTyping;
    private bool _disposed;

    /// <param name="toolkit">What toolkit layers share: the measurer, the painter, the style, the scale, the clicks.</param>
    /// <param name="input">The frame's input; none in tests that give <see cref="ReadInput"/> themselves.</param>
    protected UiLayer(UiToolkit toolkit, IFrameInput? input = null)
    {
        ArgumentNullException.ThrowIfNull(toolkit);
        _toolkit = toolkit;
        _input = input;
        Ui = toolkit.CreateUi();
    }

    /// <summary>The UI this layer declares and draws.</summary>
    public Ui Ui { get; }

    /// <summary>What toolkit layers share.</summary>
    protected UiToolkit Toolkit => _toolkit;

    /// <summary>Over the match's layers (a menu, a panel over the HUD); under them by default.</summary>
    public virtual bool AboveMatch => false;

    /// <summary>True while the layer shows; its UI is declared and its input matched only then.</summary>
    public virtual bool Visible => true;

    public bool Dirty { get; private set; }

    /// <summary>
    /// The navigation keys pressed this frame (Tab, the arrows, Enter, Space): the menu's (<see cref="IFrameInput.MenuKeys"/>)
    /// for a layer that is the run's menu (<see cref="Menu.IMenu"/>); none for another, whose game gives its own.
    /// </summary>
    protected virtual UiKeys Keys => this is Menu.IMenu && _input is not null ? _input.MenuKeys : UiKeys.None;

    /// <summary>Draws the layer again at the next frame, whatever changed: what an area shows came from elsewhere.</summary>
    public void Invalidate() => _invalidated = true;

    /// <summary>
    /// The frame's input for the UI: by default from <see cref="IFrameInput"/>, the pointer shown while it is not
    /// captured for look (or the drawn pointer is on) and the game's input not blocked (a layer over the match's is not
    /// held back by that), with <see cref="Keys"/>. Tests override it.
    /// </summary>
    protected virtual UiInput ReadInput(double seconds)
    {
        if (_input is null)
        {
            return new UiInput(Keys: Keys, Seconds: seconds);
        }

        var shown =
            (!_input.Frame.PointerCaptured || _input.DrawnPointerOn) && (AboveMatch || !_input.GameInputBlocked);
        return shown
            ? UiInput.From(_input.MenuPointer, true, Keys, seconds)
            : new UiInput(Keys: Keys, Seconds: seconds);
    }

    /// <summary>Declares the layer's UI, between <see cref="Ui.Begin(float, float, float, in UiInput)"/> and <see cref="Ui.End"/>.</summary>
    protected abstract void Build(Ui ui);

    /// <summary>Draws an <see cref="Ui.Area"/> element: the game's own drawing, with the painter's fills and text.</summary>
    protected virtual void DrawArea(UiPainter painter, in UiElement element) { }

    /// <summary>
    /// A text box got the typing (<paramref name="entry"/>), or lost it (null; <paramref name="before"/> had it): by
    /// default given through <see cref="IFrameInput.SetTextFocus"/> and taken back with
    /// <see cref="IFrameInput.ClearTextFocus"/>.
    /// </summary>
    protected virtual void GiveTyping(TextEntry? entry, TextEntry? before)
    {
        if (entry is not null)
        {
            _input?.SetTextFocus(entry);
        }
        else if (before is not null)
        {
            _input?.ClearTextFocus(before);
        }
    }

    /// <summary>
    /// True while <paramref name="entry"/> still has the typing where it was given (by default, the input's text focus);
    /// false when that took it back (its Escape), so the UI stops it too.
    /// </summary>
    protected virtual bool StillTyping(TextEntry entry) => _input is null || ReferenceEquals(_input.TextFocus, entry);

    public void Update(in FrameTime time) => Update(in time, _width, _height);

    public void Update(in FrameTime time, int width, int height)
    {
        var resized = width != _width || height != _height;
        _width = width;
        _height = height;
        if (!Visible || width <= 0 || height <= 0)
        {
            Dirty = _shownLast;
            if (_shownLast)
            {
                // Hidden: the typing goes back, and the pointer is forgotten until the layer shows again.
                Ui.StopTyping();
                Ui.LosePointer();
            }

            _shownLast = false;
            FollowTyping(shown: false);
            return;
        }

        try
        {
            var input = ReadInput(time.TotalSeconds);
            Ui.Begin(width, height, _toolkit.UnitPixels(height), in input);
            Build(Ui);
            Ui.End();
            Recovered();
        }
        catch (Exception exception)
        {
            // The game's code threw: logged, and the layer shows nothing until it declares without.
            Failed(exception);
        }

        if (!_failed)
        {
            if (Ui.Entered != 0 && Ui.KindOf(Ui.Entered) != UiKind.TextBox)
            {
                _toolkit.PlayHover();
            }

            if (Ui.Clicked != 0)
            {
                _toolkit.PlayPress();
            }
        }

        FollowTyping(shown: !_failed);
        Dirty = resized || !_shownLast || _failed || _invalidated || Ui.Hash != _drawnHash || Ui.Animating;
        _shownLast = true;
    }

    public virtual SKRectI Bounds(int width, int height)
    {
        if (_failed)
        {
            return SKRectI.Empty;
        }

        // Room round what is drawn for the style's outlines past the elements (the focus's).
        var bounds = Ui.DrawnBounds;
        var margin = (int)MathF.Ceiling(Ui.UnitPixels * Ui.Style.OverdrawUnits);
        bounds.Inflate(margin, margin);
        return SKRectI.Intersect(bounds, new SKRectI(0, 0, width, height));
    }

    public void Draw(SKCanvas canvas, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (_failed)
        {
            return;
        }

        var painter = _toolkit.Painter;
        var screen = new SKRect(0, 0, width, height);
        painter.Begin(canvas, Ui);
        var saves = canvas.SaveCount;
        try
        {
            _bars = 0;
            var count = Ui.DrawnCount;
            for (var i = 0; i < count; i++)
            {
                var element = Ui.Drawn(i);
                EndBars(painter, element.Index, element.Floating);
                DrawElement(painter, in element, screen);
                if (element.Kind == UiKind.Scroll && _bars < MostNestedBars)
                {
                    _barOrder[_bars] = i;
                    _barEnd[_bars] = element.End;
                    _barFloating[_bars] = element.Floating;
                    _bars++;
                }
            }

            EndBars(painter, int.MaxValue, floating: false);
            _drawnHash = Ui.Hash;
            _invalidated = false;
            _drawReported = false;
        }
        catch (Exception exception)
        {
            // A style or the game's drawing threw: what was drawn stays, logged once until a drawing works again.
            canvas.RestoreToCount(saves);
            if (!_drawReported)
            {
                _drawReported = true;
                _toolkit.DrawFailed(GetType().Name, exception);
            }
        }
        finally
        {
            painter.End();
        }

        Dirty = false;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Takes the typing back from a text box of this layer that has it.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (disposing && _gaveTyping is { } typing)
        {
            GiveTyping(null, typing);
            _gaveTyping = null;
        }
    }

    private void DrawElement(UiPainter painter, in UiElement element, SKRect screen)
    {
        var canvas = painter.Canvas;
        var clipped = element.Clip != screen;
        if (clipped)
        {
            canvas.Save();
            canvas.ClipRect(element.Clip);
        }

        var style = Ui.Style;
        if (element.Kind == UiKind.Area)
        {
            if (element.Panel)
            {
                style.DrawPanel(painter, in element);
            }

            DrawArea(painter, in element);
            if (element.Focused)
            {
                style.DrawFocus(painter, in element);
            }
        }
        else
        {
            style.Draw(painter, in element);
        }

        if (clipped)
        {
            canvas.Restore();
        }
    }

    /// <summary>
    /// Draws the bars of the scroll areas whose elements are all drawn by element <paramref name="index"/> (or by the
    /// switch to anchored elements), each within what the areas outside it show, over their content.
    /// </summary>
    private void EndBars(UiPainter painter, int index, bool floating)
    {
        while (_bars > 0 && (index >= _barEnd[_bars - 1] || floating != _barFloating[_bars - 1]))
        {
            _bars--;
            var area = Ui.Drawn(_barOrder[_bars]);
            var canvas = painter.Canvas;
            canvas.Save();
            canvas.ClipRect(area.Clip);
            Ui.Style.DrawScrollBar(painter, in area);
            canvas.Restore();
        }
    }

    private void Failed(Exception exception)
    {
        _failed = true;
        if (!_reported)
        {
            _reported = true;
            _toolkit.BuildFailed(GetType().Name, exception);
        }
    }

    /// <summary>The layer declared without throwing: a later failure is logged again.</summary>
    private void Recovered()
    {
        _failed = false;
        _reported = false;
    }

    /// <summary>Gives the typing to the text box that has it in the UI, or takes it back, as it changes.</summary>
    private void FollowTyping(bool shown)
    {
        var typing = shown ? Ui.Typing : null;
        if (typing is not null && ReferenceEquals(typing, _gaveTyping) && !StillTyping(typing))
        {
            // Taken back where it was given (its Escape): the UI stops typing too.
            Ui.StopTyping();
            _gaveTyping = null;
            return;
        }

        if (!ReferenceEquals(typing, _gaveTyping))
        {
            GiveTyping(typing, _gaveTyping);
            _gaveTyping = typing;
        }
    }
}
