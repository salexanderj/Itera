using System.Globalization;
using System.Numerics;
using Itera.Engine.Audio;
using Itera.Engine.Input;
using SkiaSharp;

namespace Itera.Engine.UI.Menu;

/// <summary>
/// Draws a slot's icon centred on <paramref name="centre"/>, about <paramref name="size"/> pixels across, in
/// <paramref name="ink"/> on a button of colour <paramref name="face"/> (for lines drawn over the ink). Called only while
/// the menu draws, so it may draw with paints of its own made once.
/// </summary>
public delegate void RadialIcon(SKCanvas canvas, SKPoint centre, float size, SKColor ink, SKColor face);

/// <summary>
/// A radial menu in the middle of the screen a ring of <see cref="RingSlots"/> curved
/// buttons of equal size around the outside, numbered 1 to 12 clockwise from the top (slot 0 shows 1, at the top, and slot 11 shows 12, at eleven o'clock), and a circle in the middle split into three
/// buttons: its top half (<see cref="Top"/>) and its lower half's two quarters
/// (<see cref="LowerLeft"/>, <see cref="LowerRight"/>): 15 buttons. It is read with a pointer (<see cref="UiPointer"/>, a
/// drawn one in a match: <see cref="IFrameInput.SetDrawnPointer"/>), which it draws as a dot. The button under the
/// pointer is drawn in the style's hover colours and clicks softly as the pointer comes onto it
/// (<see cref="EngineSounds.MenuHover"/>); a press and a release on one button choose it, with a fuller click
/// (<see cref="EngineSounds.MenuPress"/>), both at the listener. <see cref="Selected"/> is drawn sunk, as the settings
/// page's shown tab is. Each slot may carry an icon (<see cref="SetIcon"/>). Its sizes are shares of
/// <see cref="ScaleShare"/> of the screen's height (placeholders); its layout is worked out from the frame's size given
/// to each call, so it needs no draw before its first update. Its paths, paints, font, and numbers are made once for a
/// size, so it allocates nothing after its first draw. What opening, closing, and a choice do is its owner's. Main
/// thread.
/// </summary>
public sealed class RadialMenu : IDisposable
{
    /// <summary>The buttons in the ring.</summary>
    public const int RingSlots = 12;

    /// <summary>The middle circle's lower half's left quarter.</summary>
    public const int LowerLeft = RingSlots;

    /// <summary>The middle circle's lower half's right quarter.</summary>
    public const int LowerRight = RingSlots + 1;

    /// <summary>The middle circle's top half.</summary>
    public const int Top = RingSlots + 2;

    /// <summary>Every button: the ring's and the middle's three.</summary>
    public const int Slots = RingSlots + 3;

    /// <summary>
    /// The size the shares below are of, as a share of the screen's height: 1.3, the menu made 30 % larger.
    /// </summary>
    public const float ScaleShare = 1.3f;

    /// <summary>The middle circle's radius, as a share of the menu's size (<see cref="ScaleShare"/>).</summary>
    public const float CircleShare = 0.085f;

    /// <summary>The ring's inner radius, as a share of the menu's size.</summary>
    public const float RingInnerShare = 0.105f;

    /// <summary>The ring's outer radius, as a share of the menu's size.</summary>
    public const float RingOuterShare = 0.28f;

    /// <summary>The gap between two buttons, as a share of the menu's size.</summary>
    public const float GapShare = 0.004f;

    /// <summary>The numbers' size, as a share of the menu's size.</summary>
    public const float NumberShare = 0.024f;

    /// <summary>How far in from the ring's outer edge the numbers' middles are, as a share of the menu's size.</summary>
    public const float NumberInsetShare = 0.03f;

    /// <summary>The dot's radius, as a share of the menu's size.</summary>
    private const float DotShare = 0.006f;

    /// <summary>The outlines' width, as a share of the menu's size.</summary>
    public const float OutlineShare = 0.003f;

    private readonly UiFonts _fonts;
    private readonly AudioService _audio;
    private readonly RadialIcon?[] _icons = new RadialIcon?[Slots];
    private readonly SKPath?[] _paths = new SKPath?[Slots];

    // The numbers, made once for the font: drawing a string allocates on every call.
    private readonly TextBlobs _numbers = new(Numbers());
    private int _pathsWidth;
    private int _pathsHeight;
    private SKFont? _font;
    private SKPaint? _fill;
    private SKPaint? _stroke;
    private SKPaint? _text;
    private int _hoverSound = -1;
    private int _pressSound = -1;
    private int _pressed = -1;
    private Vector2 _lastPointer;
    private bool _pointerSeen;
    private Vector2 _dot;
    private int _selected = -1;
    private bool _dirty = true;
    private bool _disposed;

    /// <param name="fonts">The UI's typeface, for the numbers.</param>
    /// <param name="audio">Where the clicks play.</param>
    public RadialMenu(UiFonts fonts, AudioService audio)
    {
        _fonts = fonts;
        _audio = audio;
    }

    /// <summary>True while it shows.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The slot under the pointer, or -1.</summary>
    public int Hovered { get; private set; } = -1;

    /// <summary>The slot drawn sunk (what its owner says is chosen now), or -1.</summary>
    public int Selected
    {
        get => _selected;
        set
        {
            if (value != _selected)
            {
                _selected = value;
                _dirty = true;
            }
        }
    }

    /// <summary>True when its look changed since it was last drawn.</summary>
    public bool Dirty => _dirty;

    /// <summary>Gives slot <paramref name="slot"/> an icon, or takes it away (null).</summary>
    public void SetIcon(int slot, RadialIcon? icon)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Slots);
        _icons[slot] = icon;
        _dirty = true;
    }

    /// <summary>
    /// Shows it with the pointer at <paramref name="start"/> (framebuffer pixels; a drawn pointer's start) and nothing
    /// under it yet: the button there, if any, is found at the next update without a click, as the pointer has not moved.
    /// </summary>
    public void Open(Vector2 start)
    {
        IsOpen = true;
        Hovered = -1;
        _pressed = -1;
        _lastPointer = start;
        _pointerSeen = true;
        _dot = new Vector2(MathF.Round(start.X), MathF.Round(start.Y));
        _dirty = true;
    }

    /// <summary>Hides it.</summary>
    public void Close()
    {
        if (IsOpen)
        {
            IsOpen = false;
            _dirty = true;
        }
    }

    /// <summary>
    /// The slot at <paramref name="position"/> (framebuffer pixels) on a frame of <paramref name="width"/> x
    /// <paramref name="height"/>, or -1: one of the middle circle's buttons, a button of the ring by its angle, or nothing
    /// in the gaps, on the lines between the middle's buttons, between the circle and the ring, or past the ring.
    /// </summary>
    public static int Hit(Vector2 position, int width, int height) =>
        RadialRing.Hit(position, Centre(width, height), Scale(height));

    /// <summary>The menu's size in pixels on a frame <paramref name="height"/> pixels high: what its shares are of.</summary>
    public static float Scale(int height) => ScaleShare * height;

    /// <summary>The ring's outer radius in pixels on a frame <paramref name="height"/> pixels high.</summary>
    public static float OuterRadius(int height) => RingOuterShare * Scale(height);

    /// <summary>
    /// Reads <paramref name="input"/> on a frame of <paramref name="width"/> x <paramref name="height"/>: the slot under
    /// it, a soft click when it moves onto a button, and the slot chosen (a press and a release on it, with a fuller
    /// click), or -1. Does nothing while closed.
    /// </summary>
    public int Update(in UiPointer input, int width, int height)
    {
        if (!IsOpen)
        {
            return -1;
        }

        var moved = _pointerSeen && input.Position != _lastPointer;
        _lastPointer = input.Position;
        _pointerSeen = true;
        var dot = new Vector2(MathF.Round(input.Position.X), MathF.Round(input.Position.Y));
        if (dot != _dot)
        {
            _dot = dot;
            _dirty = true;
        }

        var hover = Hit(input.Position, width, height);
        if (hover != Hovered)
        {
            if (hover >= 0 && moved)
            {
                Click(EngineSounds.MenuHover, ref _hoverSound);
            }

            Hovered = hover;
            _dirty = true;
        }

        if (input.Pressed)
        {
            _pressed = hover;
        }

        if (!input.Released)
        {
            return -1;
        }

        var pressed = _pressed;
        _pressed = -1;
        if (pressed < 0 || pressed != hover)
        {
            return -1;
        }

        Click(EngineSounds.MenuPress, ref _pressSound);
        return pressed;
    }

    /// <summary>The pixels it may cover on a frame of <paramref name="width"/> x <paramref name="height"/>: the ring's square.</summary>
    public static SKRectI Bounds(int width, int height) =>
        SKRectI.Ceiling(RadialRing.Bounds(Centre(width, height), Scale(height)));

    /// <summary>Draws it, when open, in <paramref name="style"/>'s colours; clears <see cref="Dirty"/>.</summary>
    public void Draw(SKCanvas canvas, int width, int height, MenuStyle style)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(style);
        _dirty = false;
        if (!IsOpen)
        {
            return;
        }

        Prepare(width, height);
        var centre = Centre(width, height);
        var scale = Scale(height);
        for (var slot = 0; slot < Slots; slot++)
        {
            var hover = slot == Hovered;
            var selected = slot == _selected;
            var face =
                hover ? style.ButtonHover
                : selected ? style.Field
                : style.Button;
            _fill!.Color = face;
            canvas.DrawPath(_paths[slot], _fill);
            _stroke!.Color = style.Ink;
            canvas.DrawPath(_paths[slot], _stroke);
            var ink =
                hover ? style.ButtonTextHover
                : selected ? style.Text
                : style.ButtonText;
            if (slot < RingSlots)
            {
                var at = RadialRing.NumberAt(centre, slot, scale);
                _text!.Color = ink;
                var baseline = at.Y - (_font!.Metrics.Ascent + _font.Metrics.Descent) / 2;
                _numbers.Draw(canvas, slot, at.X, baseline, SKTextAlign.Center, _font, _text);
            }

            _icons[slot]
                ?.Invoke(
                    canvas,
                    ToPoint(RadialRing.IconAt(centre, slot, scale)),
                    RadialRing.IconSize(slot, scale),
                    ink,
                    face
                );
        }

        // The pointer: a light dot ringed in ink, seen on every button's colour.
        var dot = DotShare * scale;
        _fill!.Color = style.ButtonTextHover;
        canvas.DrawCircle(_dot.X, _dot.Y, dot, _fill);
        _stroke!.Color = style.Ink;
        canvas.DrawCircle(_dot.X, _dot.Y, dot, _stroke);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RadialRing.Free(_paths);
        _numbers.Dispose();
        _font?.Dispose();
        _fill?.Dispose();
        _stroke?.Dispose();
        _text?.Dispose();
    }

    /// <summary>The menu's middle: the frame's.</summary>
    private static Vector2 Centre(int width, int height) => new(width / 2f, height / 2f);

    private static SKPoint ToPoint(Vector2 point) => new(point.X, point.Y);

    /// <summary>The paths, paints, and font for a frame of this size, made again only when the size changes.</summary>
    private void Prepare(int width, int height)
    {
        _fill ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        _stroke ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
        _text ??= new SKPaint { IsAntialias = true };
        if (_paths[0] is not null && width == _pathsWidth && height == _pathsHeight)
        {
            return;
        }

        _pathsWidth = width;
        _pathsHeight = height;
        var scale = Scale(height);
        _stroke.StrokeWidth = Math.Max(1.5f, OutlineShare * scale);
        _font?.Dispose();
        _font = new SKFont(_fonts.Mono, Math.Max(10, NumberShare * scale)) { Embolden = true };

        RadialRing.Build(_paths, Centre(width, height), scale);
    }

    /// <summary>
    /// Plays the click <paramref name="name"/> where the listener is, as the menu's are played; <paramref name="id"/>
    /// keeps its recipe's id once found. With no such recipe (the audio not started, as in tests) nothing plays.
    /// </summary>
    private void Click(string name, ref int id)
    {
        if (id < 0 && !_audio.Recipes.TryFind(name, out id))
        {
            id = -1;
            return;
        }

        _audio.Play(new SoundEvent { Recipe = id, AtListener = true });
    }

    private static string[] Numbers()
    {
        var numbers = new string[RingSlots];
        for (var i = 0; i < numbers.Length; i++)
        {
            numbers[i] = (i + 1).ToString(CultureInfo.InvariantCulture);
        }

        return numbers;
    }
}
