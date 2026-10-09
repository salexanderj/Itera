using Itera.Engine.Commands;
using Itera.Modding;
using SkiaSharp;

namespace Itera.Engine.UI;

/// <summary>
/// Draws the <see cref="DevConsole"/> while it is open: the top <see cref="HeightShare"/> of the screen on a dark,
/// see-through panel, the scrollback with long lines wrapped (a line of two columns, as help's, wrapped within each
/// column side by side) and each kind of line in its colour, and the line being typed after a <c>&gt;</c> prompt with a
/// cursor that blinks every half second (steady while typing). Main thread.
/// </summary>
public sealed class ConsoleLayer : IUiLayer, IDisposable
{
    /// <summary>The share of the screen's height the console covers.</summary>
    public const float HeightShare = 0.4f;

    /// <summary>Fewest characters a line's second column gets; with less, the line is drawn as one column.</summary>
    internal const int MinColumnRoom = 20;

    private const float Padding = 8f;
    private const string Prompt = "> ";

    private readonly DevConsole _console;
    private readonly UiFonts _fonts;
    private readonly List<string> _rows = [];
    private readonly List<string> _rightRows = [];
    private SKFont? _font;
    private SKPaint? _panel;
    private SKPaint? _edge;
    private SKPaint? _text;
    private int _drawnVersion = -1;
    private bool _cursorShown;
    private bool _drawnCursor;
    private bool _disposed;

    public ConsoleLayer(DevConsole console, UiFonts fonts)
    {
        _console = console;
        _fonts = fonts;
    }

    public bool Visible => _console.IsOpen;

    /// <summary>Over the HUD, which it covers while open.</summary>
    public bool AboveMatch => true;

    public bool Dirty => _console.Version != _drawnVersion || _cursorShown != _drawnCursor;

    public void Update(in FrameTime time)
    {
        _console.Update(time.TotalSeconds);
        var sinceTyped = time.TotalSeconds - _console.LastTyped;
        _cursorShown = sinceTyped < 0.5 || ((long)(sinceTyped * 2) & 1) == 0;
    }

    public SKRectI Bounds(int width, int height) => new(0, 0, width, PanelHeight(height));

    public void Draw(SKCanvas canvas, int width, int height)
    {
        _drawnVersion = _console.Version;
        _drawnCursor = _cursorShown;
        var font = Font;
        _panel ??= new SKPaint { Color = new SKColor(10, 12, 16, 235) };
        _edge ??= new SKPaint { Color = new SKColor(90, 110, 140, 255) };
        _text ??= new SKPaint { IsAntialias = true };

        var panelHeight = PanelHeight(height);
        canvas.DrawRect(new SKRect(0, 0, width, panelHeight), _panel);
        canvas.DrawRect(new SKRect(0, panelHeight - 2, width, panelHeight), _edge);

        var metrics = font.Metrics;
        var lineHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent + metrics.Leading);
        var charWidth = font.MeasureText("M");
        var columns = Math.Max(8, (int)((width - 2 * Padding) / charWidth));

        // The typed line at the bottom, scrolled sideways so the cursor stays in view.
        var baseline = panelHeight - Padding - metrics.Descent - 2;
        var line = _console.Line;
        var room = Math.Max(1, columns - Prompt.Length - 1);
        var first = Math.Max(0, _console.Cursor - room);
        var shown = line.Length - first > room ? line.Substring(first, room) : line[first..];
        _text.Color = new SKColor(250, 250, 250);
        canvas.DrawText(Prompt + shown, Padding, baseline, SKTextAlign.Left, font, _text);
        if (_cursorShown)
        {
            var x = Padding + (Prompt.Length + _console.Cursor - first) * charWidth;
            canvas.DrawRect(new SKRect(x, baseline + metrics.Ascent, x + 2, baseline + metrics.Descent), _text);
        }

        // The scrollback above it, newest at the bottom, from the scrolled-to line up.
        var y = baseline - lineHeight;
        var top = Padding - metrics.Ascent;
        if (_console.ScrollOffset > 0)
        {
            _text.Color = new SKColor(140, 150, 170);
            var more = $"-- {_console.ScrollOffset} newer line(s) below --";
            canvas.DrawText(more, Padding, y, SKTextAlign.Left, font, _text);
            y -= lineHeight;
        }

        for (var i = _console.ScrollbackCount - 1 - _console.ScrollOffset; i >= 0 && y >= top; i--)
        {
            var entry = _console.ScrollbackLine(i);
            _text.Color = Colour(entry.Kind);
            var right = Padding + Layout(entry, columns, _rows, _rightRows) * charWidth;
            for (var row = Math.Max(_rows.Count, _rightRows.Count) - 1; row >= 0 && y >= top; row--)
            {
                if (row < _rows.Count)
                {
                    canvas.DrawText(_rows[row], Padding, y, SKTextAlign.Left, font, _text);
                }

                if (row < _rightRows.Count)
                {
                    canvas.DrawText(_rightRows[row], right, y, SKTextAlign.Left, font, _text);
                }

                y -= lineHeight;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _font?.Dispose();
        _panel?.Dispose();
        _edge?.Dispose();
        _text?.Dispose();
    }

    private SKFont Font => _font ??= new SKFont(_fonts.Mono, _fonts.Pixels);

    private static int PanelHeight(int height) => Math.Max(1, (int)(height * HeightShare));

    private static SKColor Colour(ConsoleLineKind kind) =>
        kind switch
        {
            ConsoleLineKind.Warning => new SKColor(240, 200, 90),
            ConsoleLineKind.Error => new SKColor(245, 95, 85),
            ConsoleLineKind.Answer => new SKColor(170, 215, 255),
            ConsoleLineKind.Failure => new SKColor(255, 150, 110),
            _ => new SKColor(205, 208, 214),
        };

    /// <summary>
    /// Wraps <paramref name="line"/> for a console <paramref name="columns"/> characters wide: a line of one column into
    /// <paramref name="left"/> alone; a line of two into <paramref name="left"/> within its first column (rows after
    /// the first indented by its <see cref="ConsoleLine.Indent"/>) and <paramref name="right"/> within the rest, or,
    /// where the second would get fewer than <see cref="MinColumnRoom"/> characters, joined into
    /// <paramref name="left"/>. Returns the character the right rows start at.
    /// </summary>
    internal static int Layout(in ConsoleLine line, int columns, List<string> left, List<string> right)
    {
        right.Clear();
        if (line.Column <= 0)
        {
            Wrap(line.Text, columns, 0, left);
            return 0;
        }

        if (columns - line.Column < MinColumnRoom)
        {
            Wrap(CommandContext.Columns(line.Text, line.Right, line.Column), columns, 0, left);
            return 0;
        }

        Wrap(line.Text, line.Column - 2, line.Indent, left);
        Wrap(line.Right, columns - line.Column, 0, right);
        return line.Column;
    }

    /// <summary>
    /// Splits <paramref name="text"/> into <paramref name="rows"/> of at most <paramref name="width"/> characters, rows
    /// after the first starting with <paramref name="indent"/> spaces (at most half the width): at the last space or
    /// after the last '|' (a usage's choices), whichever comes later, else in the middle of a word.
    /// </summary>
    internal static void Wrap(string text, int width, int indent, List<string> rows)
    {
        rows.Clear();
        width = Math.Max(1, width);
        indent = Math.Clamp(indent, 0, width / 2);
        var start = 0;
        var room = width;
        var prefix = "";
        while (text.Length - start > room)
        {
            var space = text.LastIndexOf(' ', start + room, room);
            var bar = text.LastIndexOf('|', start + room - 1, room - 1) + 1;
            var end = Math.Max(space, bar);
            if (end <= start)
            {
                end = start + room;
            }

            rows.Add(prefix + text[start..end]);
            start = end;
            while (start < text.Length && text[start] == ' ')
            {
                start++;
            }

            room = width - indent;
            prefix = new string(' ', indent);
        }

        rows.Add(prefix + text[start..]);
    }
}
