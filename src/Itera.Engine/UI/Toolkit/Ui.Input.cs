using System.Runtime.InteropServices;
using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// The input matched against the last layout, at each <see cref="Begin(float, float, float, in UiInput)"/>: the element
/// under the pointer (the last drawn first, within what its scroll areas show; a passive one is passed over, a disabled
/// one or a box that blocks stops the search), a press on it, a click when the button comes up over the element it went
/// down on, a slider dragged by the held button (also off it), the wheel scrolling the innermost scroll area under the
/// pointer that can move that way, and the keyboard's focus (Tab and Shift+Tab in the order written, the arrows to the
/// nearest element that way, Left and Right moving a focused slider, Enter or Space pressing; a focus moved by a key is
/// scrolled into view). A press on a text box gives it the typing (<see cref="Typing"/>); a press anywhere else takes it
/// away. Tweens ease values kept by id (<see cref="Tween"/>). What the code reads back is each element's
/// <see cref="UiState"/>.
/// </summary>
public sealed partial class Ui
{
    /// <summary>How far one step of the wheel scrolls, in units.</summary>
    public const float WheelUnits = 60;

    /// <summary>A focused slider moves by this share of its range for each Left or Right.</summary>
    public const float SliderKeyShare = 0.05f;

    /// <summary>The most layouts a focus moved by a key takes to scroll into view through nested scroll areas.</summary>
    private const int RevealLayouts = 4;

    private readonly Dictionary<int, UiMemory> _memory = [];
    private UiHit[] _hits = new UiHit[64];
    private int _hitCount;
    private int _hovered;
    private int _pressed;
    private int _clicked;
    private int _rightPressed;
    private int _rightClicked;
    private int _changed;
    private float _changedValue;
    private int _focused;
    private bool _focusShown;
    private int _reveal;
    private int _typing;
    private int _entered;
    private System.Numerics.Vector2 _lastPointer;
    private bool _pointerSeen;
    private double _seconds;
    private bool _animating;
    private HashCode _tweens;

    /// <summary>The element the pointer is over (0: none).</summary>
    public int Hovered => _hovered;

    /// <summary>The element the pointer came onto in this frame by moving (0: none): for a soft click.</summary>
    public int Entered => _entered;

    /// <summary>The element clicked or pressed by a key in this frame (0: none): for a press's click.</summary>
    public int Clicked => _clicked;

    /// <summary>The element with the keyboard's focus (0: none).</summary>
    public int Focused => _focused;

    /// <summary>True when the focus was moved by a key, so the style shows it; a press with the pointer hides it.</summary>
    public bool FocusShown => _focusShown;

    /// <summary>The text box that has the typing, or null: the layer gives it the keys (<c>IFrameInput.SetTextFocus</c>).</summary>
    public TextEntry? Typing { get; private set; }

    /// <summary>A tween is still moving, or a focus is being scrolled into view: lay out and draw again next frame.</summary>
    public bool Animating => _animating;

    /// <summary>
    /// The screen pixels the last layout draws on: the union of what each element that draws covers within what its
    /// scroll areas show (containers without a panel draw nothing).
    /// </summary>
    public SKRectI DrawnBounds { get; private set; }

    /// <summary>
    /// Starts declaring a UI for a screen of <paramref name="width"/> x <paramref name="height"/> pixels at
    /// <paramref name="unitPixels"/> pixels a unit (<see cref="UnitPixelsFor"/>), with <paramref name="input"/> matched
    /// against the last layout.
    /// </summary>
    public void Begin(float width, float height, float unitPixels, in UiInput input)
    {
        Begin(width, height, unitPixels);
        _seconds = input.Seconds;
        _animating = false;
        _tweens = default;
        Match(in input);
    }

    /// <summary>Takes the typing away from the text box that has it.</summary>
    public void StopTyping() => _typing = 0;

    /// <summary>
    /// Forgets the pointer: nothing hovered or pressed, and the next pointer seen does not count as moving onto what it
    /// is over (a layer hidden and shown again clicks for the pointer only once it moves).
    /// </summary>
    public void LosePointer()
    {
        _hovered = 0;
        _pressed = 0;
        _pointerSeen = false;
    }

    /// <summary>Gives the keyboard's focus to element <paramref name="id"/> (0: to none), shown and scrolled into view as if a key moved it.</summary>
    public void Focus(int id)
    {
        _focused = id;
        _focusShown = id != 0;
        _reveal = id != 0 ? RevealLayouts : 0;
    }

    /// <summary>
    /// A value kept for <paramref name="id"/> (its own id, not an element's) that eases to <paramref name="target"/> over
    /// <paramref name="seconds"/> from wherever it is when the target changes; it starts at its first target. While one
    /// moves, the layer lays out and draws each frame.
    /// </summary>
    public float Tween(int id, float target, float seconds)
    {
        ref var memory = ref CollectionsMarshal.GetValueRefOrAddDefault(_memory, id, out _);
        if (!memory.Tweens || seconds <= 0)
        {
            memory.Tweens = true;
            memory.From = target;
            memory.Target = target;
            memory.Seconds = Math.Max(0, seconds);
            memory.Start = _seconds;
        }
        else if (memory.Target != target)
        {
            memory.From = TweenValue(in memory);
            memory.Target = target;
            memory.Seconds = seconds;
            memory.Start = _seconds;
        }

        var value = TweenValue(in memory);
        _animating |= value != memory.Target;
        _tweens.Add(value);
        return value;
    }

    private float TweenValue(in UiMemory memory)
    {
        if (memory.Seconds <= 0)
        {
            return memory.Target;
        }

        var t = (float)Math.Clamp((_seconds - memory.Start) / memory.Seconds, 0, 1);
        return t >= 1 ? memory.Target : memory.From + (memory.Target - memory.From) * t * t * (3 - 2 * t);
    }

    /// <summary>Matches the frame's input against the last layout.</summary>
    private void Match(in UiInput input)
    {
        _clicked = 0;
        _rightClicked = 0;
        _changed = 0;
        _entered = 0;
        // Only a pointer seen before that moves comes onto an element: not one shown again, nor a layout moving under it.
        var moved = input.PointerShown && _pointerSeen && input.At != _lastPointer;
        var over = input.PointerShown ? HitAt(input.At.X, input.At.Y) : -1;
        var hovered = over >= 0 ? _hits[over].Id : 0;
        if (hovered != _hovered)
        {
            _entered = moved ? hovered : 0;
            _hovered = hovered;
        }

        _lastPointer = input.At;
        _pointerSeen = input.PointerShown;
        if (input.Pressed && input.PointerShown)
        {
            _pressed = hovered;
            _focusShown = false;
            _reveal = 0;
            _typing = over >= 0 && _hits[over].Kind == UiKind.TextBox ? hovered : 0;
            if (hovered != 0)
            {
                _focused = hovered;
            }
        }

        if (_pressed != 0 && (input.Down || input.Pressed) && HitOf(_pressed) is >= 0 and var slider)
        {
            if (_hits[slider].Kind == UiKind.Slider)
            {
                Slide(slider, input.At.X);
            }
        }

        if (input.Released || !input.Down)
        {
            if (input.Released && _pressed != 0 && _pressed == hovered && ClicksOnRelease(over))
            {
                _clicked = _pressed;
            }

            _pressed = 0;
        }

        if (input.RightPressed && input.PointerShown)
        {
            _rightPressed = hovered;
        }

        if (input.RightReleased)
        {
            _rightClicked = _rightPressed != 0 && _rightPressed == hovered ? _rightPressed : 0;
            _rightPressed = 0;
        }

        if (input.Wheel != 0 && input.PointerShown)
        {
            Wheel(input.At.X, input.At.Y, input.Wheel);
        }

        if (input.Keys != UiKeys.None)
        {
            Keys(input.Keys);
        }
    }

    /// <summary>A button, a switch, a pressable row, or an area clicks; a slider and a text box take the press instead.</summary>
    private bool ClicksOnRelease(int hit) =>
        hit >= 0 && _hits[hit].Kind is UiKind.Button or UiKind.Switch or UiKind.Area or UiKind.Pressable;

    private static bool Interactive(UiKind kind) =>
        kind is UiKind.Button or UiKind.Switch or UiKind.Slider or UiKind.TextBox or UiKind.Area or UiKind.Pressable;

    /// <summary>
    /// The element the pointer is on at a point, the last drawn first and within what its scroll areas show: one that
    /// takes the pointer; -1 when a disabled one, a box that blocks, or nothing is there.
    /// </summary>
    private int HitAt(float x, float y)
    {
        for (var i = _hitCount - 1; i >= 0; i--)
        {
            ref readonly var hit = ref _hits[i];
            if (!hit.Rect.Contains(x, y) || !hit.Clip.Contains(x, y) || (hit.Marks & UiMarks.Passive) != 0)
            {
                continue;
            }

            if (Interactive(hit.Kind))
            {
                return (hit.Marks & UiMarks.Disabled) != 0 || hit.Id == 0 ? -1 : i;
            }

            if (hit.Blocks)
            {
                return -1;
            }
        }

        return -1;
    }

    /// <summary>Element <paramref name="id"/>'s place in the last layout's hits; -1 when it was not laid out.</summary>
    private int HitOf(int id)
    {
        for (var i = 0; i < _hitCount; i++)
        {
            if (_hits[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A slider's value at the pointer's <paramref name="x"/>.</summary>
    private void Slide(int hit, float x)
    {
        ref readonly var slider = ref _hits[hit];
        var share = slider.Rect.Width > 0 ? Math.Clamp((x - slider.Rect.Left) / slider.Rect.Width, 0, 1) : 0;
        _changed = slider.Id;
        _changedValue = slider.Least + share * (slider.Most - slider.Least);
    }

    /// <summary>
    /// The wheel scrolls the innermost scroll area under the pointer that can move that way (a short list inside a long
    /// page leaves the page to scroll).
    /// </summary>
    private void Wheel(float x, float y, float steps)
    {
        var innermost = -1;
        for (var i = 0; i < _hitCount; i++)
        {
            ref readonly var hit = ref _hits[i];
            if (
                hit.Kind != UiKind.Scroll
                || hit.Id == 0
                || !hit.Rect.Contains(x, y)
                || !hit.Clip.Contains(x, y)
                || (innermost >= 0 && hit.Depth <= _hits[innermost].Depth)
            )
            {
                continue;
            }

            var room = hit.Content - hit.View;
            var canMove = steps < 0 ? hit.Offset < room - 0.5f : hit.Offset > 0.5f;
            if (room > 0.5f && canMove)
            {
                innermost = i;
            }
        }

        if (innermost >= 0)
        {
            ref var memory = ref CollectionsMarshal.GetValueRefOrAddDefault(_memory, _hits[innermost].Id, out _);
            memory.Scrolls = true;
            memory.Scroll = Math.Max(0, _hits[innermost].Offset - steps * WheelUnits * UnitPixels);
        }
    }

    /// <summary>The navigation keys: the focus moved, a focused slider stepped, the focused element pressed.</summary>
    private void Keys(UiKeys keys)
    {
        _focusShown = true;
        var before = _focused;
        var focused = HitOf(_focused);
        if (focused < 0 || !Focusable(focused))
        {
            focused = -1;
        }

        if ((keys & UiKeys.Next) != 0)
        {
            focused = Step(focused, forward: true);
        }

        if ((keys & UiKeys.Previous) != 0)
        {
            focused = Step(focused, forward: false);
        }

        if (focused >= 0 && _hits[focused].Kind == UiKind.Slider && (keys & (UiKeys.Left | UiKeys.Right)) != 0)
        {
            ref readonly var hit = ref _hits[focused];
            var step = SliderKeyShare * (hit.Most - hit.Least) * ((keys & UiKeys.Right) != 0 ? 1 : -1);
            _changed = hit.Id;
            _changedValue = Math.Clamp(SliderValue(hit.Id) + step, hit.Least, hit.Most);
            keys &= ~(UiKeys.Left | UiKeys.Right);
        }

        foreach (var (key, dx, dy) in s_directions)
        {
            if ((keys & key) != 0)
            {
                focused = focused < 0 ? Step(-1, forward: true) : Nearest(focused, dx, dy);
            }
        }

        _focused = focused >= 0 ? _hits[focused].Id : 0;
        if (_focused != before)
        {
            _reveal = RevealLayouts;
            if (_typing != _focused)
            {
                // The focus left the text box by a key: the typing goes with it.
                _typing = 0;
            }
        }

        if ((keys & UiKeys.Press) != 0 && focused >= 0)
        {
            if (_hits[focused].Kind == UiKind.TextBox)
            {
                _typing = _focused;
            }
            else if (_hits[focused].Kind != UiKind.Slider)
            {
                _clicked = _focused;
            }
        }
    }

    private static readonly (UiKeys Key, int Dx, int Dy)[] s_directions =
    [
        (UiKeys.Left, -1, 0),
        (UiKeys.Right, 1, 0),
        (UiKeys.Up, 0, -1),
        (UiKeys.Down, 0, 1),
    ];

    private bool Focusable(int hit) =>
        Interactive(_hits[hit].Kind)
        && (_hits[hit].Marks & (UiMarks.Disabled | UiMarks.Passive)) == 0
        && _hits[hit].Id != 0;

    /// <summary>The next (or previous) focusable element in the order written, round to the first; -1 for none.</summary>
    private int Step(int from, bool forward)
    {
        var at = from >= 0 ? _hits[from].Index : (forward ? -1 : int.MaxValue);
        var best = -1;
        var wrap = -1;
        for (var i = 0; i < _hitCount; i++)
        {
            if (!Focusable(i))
            {
                continue;
            }

            var index = _hits[i].Index;
            if (forward ? index > at : index < at)
            {
                if (best < 0 || (forward ? index < _hits[best].Index : index > _hits[best].Index))
                {
                    best = i;
                }
            }

            if (wrap < 0 || (forward ? index < _hits[wrap].Index : index > _hits[wrap].Index))
            {
                wrap = i;
            }
        }

        return best >= 0 ? best : wrap;
    }

    /// <summary>
    /// The focusable element nearest <paramref name="from"/> in direction (<paramref name="dx"/>, <paramref name="dy"/>):
    /// the least distance that way plus twice the distance across it, between rectangles' centres; itself when none.
    /// </summary>
    private int Nearest(int from, int dx, int dy)
    {
        var origin = _hits[from].Rect;
        var best = from;
        var bestScore = float.PositiveInfinity;
        for (var i = 0; i < _hitCount; i++)
        {
            if (i == from || !Focusable(i))
            {
                continue;
            }

            var rect = _hits[i].Rect;
            var along = (rect.MidX - origin.MidX) * dx + (rect.MidY - origin.MidY) * dy;
            if (along <= 0.5f)
            {
                continue;
            }

            var across = Math.Abs(dx != 0 ? rect.MidY - origin.MidY : rect.MidX - origin.MidX);
            var score = along + 2 * across;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>A slider's value as last declared.</summary>
    private float SliderValue(int id) => _byId.TryGetValue(id, out var index) ? _nodes[index].Value : 0;

    /// <summary>Element <paramref name="index"/>'s state from the input matched at this layout's start.</summary>
    private UiState StateOf(int index)
    {
        ref readonly var node = ref _nodes[index];
        var id = node.Id;
        if (id == 0 || (node.Marks & (UiMarks.Disabled | UiMarks.Passive)) != 0)
        {
            return new UiState(id, Rect(id), Value: node.Value);
        }

        var changed = id == _changed;
        return new UiState(
            id,
            Rect(id),
            Hovered: id == _hovered,
            Pressed: id == _pressed && id == _hovered,
            Clicked: id == _clicked,
            Focused: id == _focused,
            Dragging: id == _pressed,
            Changed: changed,
            Value: changed ? _changedValue : node.Value,
            Typing: id == _typing,
            RightClicked: id == _rightClicked
        );
    }

    /// <summary>
    /// After a layout: each element's subtree's end, the hits in the order drawn (the flow, then anchored elements), the
    /// state of elements no longer declared dropped, a focus moved by a key scrolled into view, and the area drawn on.
    /// </summary>
    private void AfterLayout()
    {
        for (var i = 0; i < _count; i++)
        {
            _nodes[i].End = i + 1;
        }

        for (var i = _count - 1; i >= 0; i--)
        {
            if (_nodes[i].Parent != NoNode)
            {
                ref var parent = ref _nodes[_nodes[i].Parent];
                parent.End = Math.Max(parent.End, _nodes[i].End);
            }
        }

        if (_hits.Length < _count)
        {
            _hits = new UiHit[_nodes.Length];
        }

        _hitCount = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < _count; i++)
            {
                if (_nodes[i].Floating == (pass == 1))
                {
                    AddHit(i);
                }
            }
        }

        _hovered = Declared(_hovered);
        _pressed = Declared(_pressed);
        _focused = Declared(_focused);
        _typing = Declared(_typing);
        Typing = _typing != 0 ? _entries[_byId[_typing]] : null;
        if (_reveal > 0 && _focused != 0)
        {
            _reveal = Reveal(_byId[_focused]) ? _reveal - 1 : 0;
            _animating |= _reveal > 0;
        }

        var screen = new SKRect(0, 0, ScreenWidth, ScreenHeight);
        var bounds = SKRect.Empty;
        for (var i = 0; i < _count; i++)
        {
            ref readonly var node = ref _nodes[i];
            if (
                (IsContainer(node.Kind) && node.Kind != UiKind.Pressable && !node.Box.Panel)
                || node.Kind == UiKind.Space
            )
            {
                continue;
            }

            var rect = SKRect.Intersect(SKRect.Intersect(RectOf(i), node.Clip), screen);
            if (rect.Width > 0 && rect.Height > 0)
            {
                bounds = bounds.IsEmpty ? rect : SKRect.Union(bounds, rect);
            }
        }

        DrawnBounds = SKRectI.Round(bounds);
    }

    private void AddHit(int index)
    {
        ref readonly var node = ref _nodes[index];
        var pad = Padding(in node);
        _hits[_hitCount++] = new UiHit
        {
            Index = index,
            Id = node.Id,
            Kind = node.Kind,
            Marks = node.Marks,
            Rect = RectOf(index),
            Clip = node.Clip,
            Depth = node.Depth,
            Least = node.Least,
            Most = node.Most,
            Blocks = node.Box.Blocks,
            Content = node.Content,
            View = Math.Max(0, node.H - pad.Down),
            Offset = node.Scrolled,
        };
    }

    /// <summary><paramref name="id"/> when it was declared in this layout (enabled, taking the pointer), else 0.</summary>
    private int Declared(int id) =>
        id != 0
        && _byId.TryGetValue(id, out var index)
        && (_nodes[index].Marks & (UiMarks.Disabled | UiMarks.Passive)) == 0
            ? id
            : 0;

    /// <summary>
    /// Moves the scroll areas holding element <paramref name="index"/>, the innermost first, so it shows at the next
    /// layout; true when one moved (the next layout checks again, for the areas outside).
    /// </summary>
    private bool Reveal(int index)
    {
        var rect = RectOf(index);
        var moved = false;
        for (var parent = _nodes[index].Parent; parent != NoNode; parent = _nodes[parent].Parent)
        {
            ref readonly var area = ref _nodes[parent];
            if (area.Kind != UiKind.Scroll || area.Id == 0)
            {
                continue;
            }

            var pad = Padding(in area);
            var top = area.Y + pad.Top;
            var bottom = area.Y + area.H - pad.Bottom;
            var move =
                rect.Top < top ? rect.Top - top
                : rect.Bottom > bottom ? Math.Min(rect.Bottom - bottom, rect.Top - top)
                : 0;
            var room = Math.Max(0, area.Content - (bottom - top));
            var target = Math.Clamp(area.Scrolled + move, 0, room);
            if (Math.Abs(target - area.Scrolled) > 0.5f)
            {
                ref var memory = ref CollectionsMarshal.GetValueRefOrAddDefault(_memory, area.Id, out _);
                memory.Scrolls = true;
                memory.Scroll = target;
                moved = true;
            }

            // The next area out sees the element where this one will show it.
            rect.Offset(0, area.Scrolled - target);
        }

        return moved;
    }

    /// <summary>The offset kept for scroll area <paramref name="id"/>, or 0.</summary>
    private bool ScrollOf(int id, out float offset)
    {
        if (_memory.TryGetValue(id, out var memory) && memory.Scrolls)
        {
            offset = memory.Scroll;
            return true;
        }

        offset = 0;
        return false;
    }

    /// <summary>Keeps scroll area <paramref name="id"/>'s offset at <paramref name="offset"/>, where one is kept.</summary>
    private void HoldScroll(int id, float offset)
    {
        ref var memory = ref CollectionsMarshal.GetValueRefOrNullRef(_memory, id);
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref memory) && memory.Scrolls)
        {
            memory.Scroll = offset;
        }
    }
}
