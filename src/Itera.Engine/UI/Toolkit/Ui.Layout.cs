using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// The layout, in the order flexbox and Clay use: fit widths from the content up, widths from the screen down, texts
/// broken into lines within them, fit heights up, heights down, then positions; anchored elements last, against what
/// they are placed by. A fit element takes at most the room it is given (its parent's across the parent's direction,
/// the screen's at the top), so text in it wraps or is cut. Along a row, texts (and fit containers holding text) share
/// what the rest leave, each at most its own width, and growing elements start from their content and share what is
/// left after everything else by weight, each within its least and most. Nothing else shrinks: what does not fit
/// overflows from the container's start (a scroll area clips it).
/// </summary>
public sealed partial class Ui
{
    /// <summary>The smallest text drawn, in pixels, however small the screen: below it text cannot be read.</summary>
    public const float LeastTextPixels = 10;

    private const string EllipsisText = "…";

    private void Layout()
    {
        for (var i = _count - 1; i >= 0; i--)
        {
            FitWidth(i);
        }

        for (var i = 0; i < _count; i++)
        {
            ref var node = ref _nodes[i];
            if (node.Parent == NoNode || node.Box.Anchor is not null)
            {
                node.W = Across(node.Box.Width, node.FitWidth, ScreenWidth);
            }

            if (IsContainer(node.Kind))
            {
                Widths(i);
            }
        }

        for (var i = 0; i < _count; i++)
        {
            BreakLines(i);
        }

        for (var i = _count - 1; i >= 0; i--)
        {
            FitHeight(i);
        }

        for (var i = 0; i < _count; i++)
        {
            ref var node = ref _nodes[i];
            if (node.Parent == NoNode || node.Box.Anchor is not null)
            {
                node.H = Across(node.Box.Height, node.FitHeight, ScreenHeight);
            }

            if (IsContainer(node.Kind))
            {
                Heights(i);
            }
        }

        var screen = new SKRect(0, 0, ScreenWidth, ScreenHeight);
        for (var i = 0; i < _count; i++)
        {
            ref var node = ref _nodes[i];
            if (node.Floating)
            {
                continue;
            }

            if (node.Parent == NoNode)
            {
                node.X = 0;
                node.Y = 0;
                node.Clip = screen;
            }

            Place(i);
        }

        // Anchored elements once the flow is placed, in order, so one may be placed against any element before it.
        for (var i = 0; i < _count; i++)
        {
            ref var node = ref _nodes[i];
            if (node.Box.Anchor is not { } anchor)
            {
                continue;
            }

            var target = anchor.Element != 0 && _byId.TryGetValue(anchor.Element, out var at) ? RectOf(at) : screen;
            var (ax, ay) = Fractions(anchor.At);
            var (px, py) = Fractions(anchor.Point);
            node.X = target.Left + ax * target.Width - px * node.W + Px(anchor.OffsetX);
            node.Y = target.Top + ay * target.Height - py * node.H + Px(anchor.OffsetY);
            node.Clip = screen;
            var end = SubtreeEnd(i);
            for (var j = i; j < end; j++)
            {
                Place(j);
            }
        }

        Snap();
    }

    private float Px(float units) => units * UnitPixels;

    private static bool IsContainer(UiKind kind) => kind <= UiKind.Scroll;

    private static bool HasText(UiKind kind) => kind is UiKind.Text or UiKind.Button or UiKind.TextBox;

    private static bool Fits(UiSizeKind kind) => kind is UiSizeKind.Fit or UiSizeKind.Auto;

    private UiEdges Padding(in UiNode node) =>
        node.Box.Padding is { } units
            ? new UiEdges(Px(units.Left), Px(units.Top), Px(units.Right), Px(units.Bottom))
            : default;

    private float Gap(in UiNode node) => Px(node.Box.Gap ?? 0);

    /// <summary>A size kept within its least and most (units; a most of 0 is no limit).</summary>
    private float Clamp(float pixels, UiSize size)
    {
        var least = Px(size.Least);
        var most = size.Most > 0 ? Px(size.Most) : float.PositiveInfinity;
        return Math.Clamp(pixels, least, Math.Max(least, most));
    }

    /// <summary>
    /// A size in <paramref name="room"/> where it is not shared with siblings (across a container's direction, in a
    /// grid's cell, at the top): grown to all of it, a share of it, fixed, or fit to its content and at most the room.
    /// </summary>
    private float Across(UiSize size, float fit, float room) =>
        Clamp(
            size.Kind switch
            {
                UiSizeKind.Grow => room,
                UiSizeKind.Share => size.Value * room,
                UiSizeKind.Fixed => fit,
                _ => Math.Min(fit, room),
            },
            size
        );

    /// <summary>
    /// The element's width from its content and padding (a fixed one's own), its text's size and line height, and
    /// whether it may be narrower than that (<see cref="UiNode.Shrinks"/>).
    /// </summary>
    private void FitWidth(int index)
    {
        ref var node = ref _nodes[index];
        var pad = Padding(in node);
        var content = 0f;
        node.Shrinks = false;
        if (HasText(node.Kind))
        {
            node.TextPixels = Math.Max(LeastTextPixels, Px(Style.TextUnits(in node.TextSetting)));
            node.LineHeight = _measurer.LineHeight(node.TextPixels);
            content = WidestLine(Shown(index), node.TextPixels);
            node.Shrinks = node.Kind == UiKind.Text && Fits(node.Box.Width.Kind);
        }
        else if (IsContainer(node.Kind))
        {
            content = ChildrenWidth(index);
            node.Shrinks = Fits(node.Box.Width.Kind) && AnyChildShrinks(index);
        }

        node.FitWidth = Clamp(
            node.Box.Width.Kind == UiSizeKind.Fixed ? Px(node.Box.Width.Value) : content + pad.Across,
            node.Box.Width
        );
    }

    private bool AnyChildShrinks(int index)
    {
        for (var child = _nodes[index].FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            if (_nodes[child].Box.Anchor is null && _nodes[child].Shrinks)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The width of the widest of <paramref name="text"/>'s lines, broken only at line breaks.</summary>
    private float WidestLine(ReadOnlySpan<char> text, float pixels)
    {
        var widest = 0f;
        while (true)
        {
            var end = text.IndexOf('\n');
            widest = Math.Max(widest, _measurer.Width(end < 0 ? text : text[..end], pixels));
            if (end < 0)
            {
                return widest;
            }

            text = text[(end + 1)..];
        }
    }

    /// <summary>The width a container's children take: side by side in a row, the widest elsewhere, a grid's columns.</summary>
    private float ChildrenWidth(int index)
    {
        ref readonly var node = ref _nodes[index];
        var gap = Gap(in node);
        var sum = 0f;
        var widest = 0f;
        var flow = 0;
        for (var child = node.FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            if (_nodes[child].Box.Anchor is not null)
            {
                continue;
            }

            var width = _nodes[child].FitWidth;
            sum += width;
            widest = Math.Max(widest, width);
            flow++;
        }

        return node.Kind switch
        {
            UiKind.Row => sum + Math.Max(0, flow - 1) * gap,
            // Every column as wide as the widest child, also where the grid has fewer children than columns.
            UiKind.Grid => node.Columns * widest + (node.Columns - 1) * gap,
            _ => widest,
        };
    }

    /// <summary>Gives container <paramref name="index"/>'s children their widths from its own.</summary>
    private void Widths(int index)
    {
        ref readonly var node = ref _nodes[index];
        var pad = Padding(in node);
        var inner = Math.Max(0, node.W - pad.Across);
        var gap = Gap(in node);
        if (node.Kind == UiKind.Row)
        {
            Distribute(index, inner, gap, widths: true);
            return;
        }

        var room = node.Kind == UiKind.Grid ? Math.Max(0, (inner - (node.Columns - 1) * gap) / node.Columns) : inner;
        for (var child = node.FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            ref var c = ref _nodes[child];
            if (c.Box.Anchor is null)
            {
                c.W = Across(c.Box.Width, c.FitWidth, room);
            }
        }
    }

    /// <summary>
    /// Children's sizes along a container's direction (a row's widths, a column's heights) in <paramref name="inner"/>:
    /// fixed, fit, and shared children keep theirs; shrinking ones (texts in a row) share what those leave, each at
    /// most its own width; growing ones start from their content and share what is left after all of them by weight,
    /// each within its least and most, what one cannot take going to the others.
    /// </summary>
    private void Distribute(int index, float inner, float gap, bool widths)
    {
        var taken = 0f;
        var flow = 0;
        var shrinking = 0;
        for (var child = _nodes[index].FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            ref var c = ref _nodes[child];
            if (c.Box.Anchor is not null)
            {
                continue;
            }

            flow++;
            c.Settled = false;
            var size = widths ? c.Box.Width : c.Box.Height;
            var fit = widths ? c.FitWidth : c.FitHeight;
            if (size.Kind == UiSizeKind.Grow || (widths && c.Shrinks))
            {
                shrinking += size.Kind == UiSizeKind.Grow ? 0 : 1;
                // Growing children's content is taken first; shrinking children's is shared below.
                taken += size.Kind == UiSizeKind.Grow ? fit : 0;
                continue;
            }

            SetSize(ref c, widths, size.Kind == UiSizeKind.Share ? Clamp(size.Value * inner, size) : fit);
            c.Settled = true;
            taken += widths ? c.W : c.H;
        }

        var left = inner - taken - Math.Max(0, flow - 1) * gap;
        if (shrinking > 0)
        {
            left -= ShareShrinking(index, Math.Max(0, left), shrinking);
        }

        Grow(index, Math.Max(0, left), widths);
    }

    /// <summary>
    /// Shrinking children share <paramref name="room"/>: each takes at most its fit, and what one leaves goes to the
    /// others. Returns what they took.
    /// </summary>
    private float ShareShrinking(int index, float room, int count)
    {
        var used = 0f;
        var changed = true;
        while (changed && count > 0)
        {
            changed = false;
            var each = room / count;
            for (var child = _nodes[index].FirstChild; child != NoNode; child = _nodes[child].Next)
            {
                ref var c = ref _nodes[child];
                if (c.Settled || c.Box.Anchor is not null || c.Box.Width.Kind == UiSizeKind.Grow || !c.Shrinks)
                {
                    continue;
                }

                if (c.FitWidth <= each)
                {
                    c.W = c.FitWidth;
                    c.Settled = true;
                    room -= c.W;
                    used += c.W;
                    count--;
                    changed = true;
                }
            }
        }

        var share = count > 0 ? room / count : 0;
        for (var child = _nodes[index].FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            ref var c = ref _nodes[child];
            if (!c.Settled && c.Box.Anchor is null && c.Box.Width.Kind != UiSizeKind.Grow && c.Shrinks)
            {
                c.W = Clamp(share, c.Box.Width);
                c.Settled = true;
                used += c.W;
            }
        }

        return used;
    }

    /// <summary>
    /// Growing children: each its content plus a share of <paramref name="left"/> by weight, within its least and most;
    /// one held at a limit leaves the rest of the room to the others.
    /// </summary>
    private void Grow(int index, float left, bool widths)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            var weights = 0f;
            for (var child = _nodes[index].FirstChild; child != NoNode; child = _nodes[child].Next)
            {
                ref readonly var c = ref _nodes[child];
                if (!c.Settled && c.Box.Anchor is null)
                {
                    weights += Math.Max(0, (widths ? c.Box.Width : c.Box.Height).Value);
                }
            }

            for (var child = _nodes[index].FirstChild; child != NoNode; child = _nodes[child].Next)
            {
                ref var c = ref _nodes[child];
                if (c.Settled || c.Box.Anchor is not null)
                {
                    continue;
                }

                var size = widths ? c.Box.Width : c.Box.Height;
                var fit = widths ? c.FitWidth : c.FitHeight;
                var wanted = fit + (weights > 0 ? left * Math.Max(0, size.Value) / weights : 0);
                var held = Clamp(wanted, size);
                SetSize(ref c, widths, held);
                if (held != wanted)
                {
                    // Held at its limit: the others share what is left.
                    c.Settled = true;
                    left = Math.Max(0, left - (held - fit));
                    changed = true;
                    break;
                }
            }
        }
    }

    private static void SetSize(ref UiNode node, bool width, float size)
    {
        if (width)
        {
            node.W = size;
        }
        else
        {
            node.H = size;
        }
    }

    /// <summary>The characters element <paramref name="index"/> shows: its text, a text box's typing or placeholder.</summary>
    private ReadOnlySpan<char> Shown(int index)
    {
        if (_entries[index] is { } entry)
        {
            return entry.Line.Length > 0 ? entry.Line.Text.AsSpan() : (_texts[index] ?? "").AsSpan();
        }

        return (_texts[index] ?? "").AsSpan();
    }

    /// <summary>
    /// Breaks element <paramref name="index"/>'s text into lines within its width: at line breaks, and at spaces when
    /// it wraps; a line too wide, or the last kept where text is left over, ends with an ellipsis.
    /// </summary>
    private void BreakLines(int index)
    {
        ref var node = ref _nodes[index];
        node.FirstLine = _lineCount;
        node.LineCount = 0;
        if (!HasText(node.Kind))
        {
            return;
        }

        var text = Shown(index);
        var room = Math.Max(0, node.W - Padding(in node).Across);
        if (node.Kind == UiKind.TextBox)
        {
            // One line, moved within the box to show the cursor when drawn.
            AddLine(new UiLine(0, text.Length, _measurer.Width(text, node.TextPixels), false));
            node.LineCount = 1;
            return;
        }

        var wrap = node.TextSetting.Wrap;
        var most = node.TextSetting.MostLines > 0 ? node.TextSetting.MostLines : int.MaxValue;
        var start = 0;
        while (start < text.Length && node.LineCount < most)
        {
            var end = wrap ? LineEnd(text, start, room, node.TextPixels) : LineBreak(text, start);
            var rest = NextLineStart(text, end);
            var width = _measurer.Width(text[start..end], node.TextPixels);
            // The last line kept with text left after it, or a line too wide that does not wrap, ends with an ellipsis.
            var cut = (node.LineCount + 1 == most && rest < text.Length) || width > room + 0.01f;
            AddLine(cut ? Cut(text, start, end, room, node.TextPixels) : new UiLine(start, end - start, width, false));
            node.LineCount++;
            start = rest;
        }
    }

    /// <summary>Where a wrapped line from <paramref name="start"/> ends: after the last word that fits, or inside a word longer than the line.</summary>
    private int LineEnd(ReadOnlySpan<char> text, int start, float room, float pixels)
    {
        var end = start;
        var i = start;
        while (i < text.Length && text[i] != '\n')
        {
            var wordEnd = i;
            while (wordEnd < text.Length && text[wordEnd] != ' ' && text[wordEnd] != '\n')
            {
                wordEnd++;
            }

            if (_measurer.Width(text[start..wordEnd], pixels) > room + 0.01f)
            {
                // The word does not fit after the line's others; alone it breaks where it must.
                return end > start ? end : start + Fitting(text[start..wordEnd], room, pixels);
            }

            end = wordEnd;
            i = wordEnd;
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }
        }

        return end;
    }

    /// <summary>The end of the line from <paramref name="start"/> without wrapping: the next line break, or the text's end.</summary>
    private static int LineBreak(ReadOnlySpan<char> text, int start)
    {
        var at = text[start..].IndexOf('\n');
        return at < 0 ? text.Length : start + at;
    }

    /// <summary>Where the line after one ending at <paramref name="end"/> starts: past the spaces and one line break.</summary>
    private static int NextLineStart(ReadOnlySpan<char> text, int end)
    {
        var next = end;
        while (next < text.Length && text[next] == ' ')
        {
            next++;
        }

        if (next < text.Length && text[next] == '\n')
        {
            next++;
        }

        return next;
    }

    /// <summary>
    /// How many of <paramref name="text"/>'s characters fit <paramref name="room"/>, never splitting a pair; at least the
    /// first character (a line always moves on).
    /// </summary>
    private int Fitting(ReadOnlySpan<char> text, float room, float pixels)
    {
        var first = Step(text, 0);
        var fits = 0;
        var most = text.Length;
        while (fits < most)
        {
            var middle = (fits + most + 1) / 2;
            if (middle < text.Length && char.IsLowSurrogate(text[middle]))
            {
                // Not inside a pair: the length before it.
                middle--;
            }

            if (middle <= fits)
            {
                // The next whole character after what fits.
                middle = fits + Step(text, fits);
            }

            if (middle > most)
            {
                break;
            }

            if (_measurer.Width(text[..middle], pixels) <= room + 0.01f)
            {
                fits = middle;
            }
            else
            {
                most = middle - 1;
            }
        }

        return Math.Max(fits, first);
    }

    /// <summary>The length of the character at <paramref name="at"/>: 2 for a pair, else 1.</summary>
    private static int Step(ReadOnlySpan<char> text, int at) =>
        at + 1 < text.Length && char.IsSurrogatePair(text[at], text[at + 1]) ? 2 : 1;

    /// <summary>
    /// The longest start of <paramref name="text"/>[<paramref name="start"/>..<paramref name="end"/>] that fits
    /// <paramref name="room"/> with an ellipsis after it (trailing spaces dropped); nothing when even the ellipsis does
    /// not fit.
    /// </summary>
    private UiLine Cut(ReadOnlySpan<char> text, int start, int end, float room, float pixels)
    {
        var ellipsis = _measurer.Width(EllipsisText, pixels);
        if (ellipsis > room + 0.01f)
        {
            return new UiLine(start, 0, 0, false);
        }

        var line = text[start..end];
        var length =
            _measurer.Width(line, pixels) + ellipsis <= room + 0.01f
                ? line.Length
                : Fitting(line, room - ellipsis, pixels);
        while (length > 0 && line[length - 1] == ' ')
        {
            length--;
        }

        if (length > 0 && _measurer.Width(line[..length], pixels) + ellipsis > room + 0.01f)
        {
            // A single character wider than the room: the ellipsis alone.
            length = 0;
        }

        return new UiLine(start, length, _measurer.Width(line[..length], pixels) + ellipsis, true);
    }

    private void AddLine(UiLine line)
    {
        if (_lineCount == _lines.Length)
        {
            Array.Resize(ref _lines, _lines.Length * 2);
        }

        _lines[_lineCount++] = line;
    }

    /// <summary>The element's height from its lines (at least one) or children and padding (a fixed one's own).</summary>
    private void FitHeight(int index)
    {
        ref var node = ref _nodes[index];
        var pad = Padding(in node);
        var content = 0f;
        if (HasText(node.Kind))
        {
            content = Math.Max(1, node.LineCount) * node.LineHeight;
        }
        else if (IsContainer(node.Kind))
        {
            content = ChildrenHeight(index);
        }

        node.FitHeight = Clamp(
            node.Box.Height.Kind == UiSizeKind.Fixed ? Px(node.Box.Height.Value) : content + pad.Down,
            node.Box.Height
        );
    }

    /// <summary>The height a container's children take: one under another in a column, the tallest elsewhere, a grid's rows.</summary>
    private float ChildrenHeight(int index)
    {
        ref readonly var node = ref _nodes[index];
        var gap = Gap(in node);
        var sum = 0f;
        var tallest = 0f;
        var flow = 0;
        var row = 0f;
        var rows = 0f;
        for (var child = node.FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            if (_nodes[child].Box.Anchor is not null)
            {
                continue;
            }

            var height = _nodes[child].FitHeight;
            sum += height;
            tallest = Math.Max(tallest, height);
            row = Math.Max(row, height);
            flow++;
            if (node.Kind == UiKind.Grid && flow % node.Columns == 0)
            {
                rows += row;
                row = 0;
            }
        }

        var gaps = Math.Max(0, flow - 1) * gap;
        return node.Kind switch
        {
            UiKind.Column or UiKind.Scroll => sum + gaps,
            UiKind.Grid => rows + row + Math.Max(0, (flow + node.Columns - 1) / node.Columns - 1) * gap,
            _ => tallest,
        };
    }

    /// <summary>Gives container <paramref name="index"/>'s children their heights from its own.</summary>
    private void Heights(int index)
    {
        ref readonly var node = ref _nodes[index];
        var pad = Padding(in node);
        var inner = Math.Max(0, node.H - pad.Down);
        if (node.Kind is UiKind.Column or UiKind.Scroll)
        {
            Distribute(index, inner, Gap(in node), widths: false);
            return;
        }

        if (node.Kind == UiKind.Grid)
        {
            GridHeights(index);
            return;
        }

        for (var child = node.FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            ref var c = ref _nodes[child];
            if (c.Box.Anchor is null)
            {
                c.H = Across(c.Box.Height, c.FitHeight, inner);
            }
        }
    }

    /// <summary>A grid's children: each row as tall as its tallest fit child; growing children fill their row.</summary>
    private void GridHeights(int index)
    {
        ref readonly var node = ref _nodes[index];
        var rowStart = node.FirstChild;
        while (rowStart != NoNode)
        {
            var rowHeight = 0f;
            var child = rowStart;
            for (var n = 0; n < node.Columns && child != NoNode; child = _nodes[child].Next)
            {
                if (_nodes[child].Box.Anchor is null)
                {
                    rowHeight = Math.Max(rowHeight, _nodes[child].FitHeight);
                    n++;
                }
            }

            for (var c = rowStart; c != child; c = _nodes[c].Next)
            {
                ref var cell = ref _nodes[c];
                if (cell.Box.Anchor is null)
                {
                    cell.H = Across(cell.Box.Height, cell.FitHeight, rowHeight);
                }
            }

            rowStart = child;
        }
    }

    /// <summary>Places container <paramref name="index"/>'s children in it, and what its scroll areas leave them to be seen in.</summary>
    private void Place(int index)
    {
        ref readonly var node = ref _nodes[index];
        if (!IsContainer(node.Kind))
        {
            return;
        }

        var pad = Padding(in node);
        var left = node.X + pad.Left;
        var top = node.Y + pad.Top;
        var innerWidth = Math.Max(0, node.W - pad.Across);
        var innerHeight = Math.Max(0, node.H - pad.Down);
        var gap = Gap(in node);
        var clip =
            node.Kind == UiKind.Scroll
                ? SKRect.Intersect(node.Clip, new SKRect(left, top, left + innerWidth, top + innerHeight))
                : node.Clip;
        var (sumWidth, sumHeight, flow) = FlowSums(index);
        var gaps = gap * Math.Max(0, flow - 1);
        var x = left + (node.Kind == UiKind.Row ? Offset(node.Box.AlignX, innerWidth - sumWidth - gaps) : 0);
        var y = top;
        if (node.Kind == UiKind.Column)
        {
            y += Offset(node.Box.AlignY, innerHeight - sumHeight - gaps);
        }
        else if (node.Kind == UiKind.Scroll)
        {
            y += Scrolled(node.Id, sumHeight + gaps - innerHeight, node.Box.AlignY);
        }

        var cell = node.Kind == UiKind.Grid ? Math.Max(0, (innerWidth - (node.Columns - 1) * gap) / node.Columns) : 0;
        var column = 0;
        var rowHeight = 0f;
        for (var child = node.FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            ref var c = ref _nodes[child];
            if (c.Box.Anchor is not null)
            {
                continue;
            }

            c.Clip = clip;
            switch (node.Kind)
            {
                case UiKind.Row:
                    c.X = x;
                    c.Y = top + Offset(node.Box.AlignY, innerHeight - c.H);
                    x += c.W + gap;
                    break;
                case UiKind.Column:
                case UiKind.Scroll:
                    c.X = left + Offset(node.Box.AlignX, innerWidth - c.W);
                    c.Y = y;
                    y += c.H + gap;
                    break;
                case UiKind.Grid:
                    rowHeight = column == 0 ? RowHeight(child, node.Columns) : rowHeight;
                    c.X = left + column * (cell + gap) + Offset(node.Box.AlignX, cell - c.W);
                    c.Y = y + Offset(node.Box.AlignY, rowHeight - c.H);
                    column++;
                    if (column == node.Columns)
                    {
                        column = 0;
                        y += rowHeight + gap;
                    }

                    break;
                default:
                    c.X = left + Offset(node.Box.AlignX, innerWidth - c.W);
                    c.Y = top + Offset(node.Box.AlignY, innerHeight - c.H);
                    break;
            }
        }
    }

    /// <summary>
    /// How far down a scroll area's content starts, its <paramref name="overflow"/> the content's height past the area's:
    /// moved up by the offset kept for it, held within the content (and kept so); placed by the alignment when it fits.
    /// </summary>
    private float Scrolled(int id, float overflow, UiAlign align)
    {
        if (overflow <= 0)
        {
            if (_scrolls.ContainsKey(id))
            {
                _scrolls[id] = 0;
            }

            return Offset(align, -overflow);
        }

        if (!_scrolls.TryGetValue(id, out var offset))
        {
            return 0;
        }

        var held = Math.Clamp(offset, 0, overflow);
        _scrolls[id] = held;
        return -held;
    }

    /// <summary>The widths and heights of a container's children in its flow, summed, and how many there are.</summary>
    private (float Width, float Height, int Count) FlowSums(int index)
    {
        var width = 0f;
        var height = 0f;
        var count = 0;
        for (var child = _nodes[index].FirstChild; child != NoNode; child = _nodes[child].Next)
        {
            if (_nodes[child].Box.Anchor is null)
            {
                width += _nodes[child].W;
                height += _nodes[child].H;
                count++;
            }
        }

        return (width, height, count);
    }

    /// <summary>The height of a grid's row starting at <paramref name="first"/>, <paramref name="cells"/> cells of the flow long.</summary>
    private float RowHeight(int first, int cells)
    {
        var tallest = 0f;
        for (var child = first; child != NoNode && cells > 0; child = _nodes[child].Next)
        {
            if (_nodes[child].Box.Anchor is null)
            {
                tallest = Math.Max(tallest, _nodes[child].H);
                cells--;
            }
        }

        return tallest;
    }

    /// <summary>Where along <paramref name="free"/> room an aligned child starts; with no room, at the start.</summary>
    private static float Offset(UiAlign align, float free) =>
        free <= 0
            ? 0
            : align switch
            {
                UiAlign.Centre => free / 2,
                UiAlign.End => free,
                _ => 0,
            };

    private static (float X, float Y) Fractions(UiPoint point) => ((int)point % 3 / 2f, (int)point / 3 / 2f);

    /// <summary>The index after the last element inside element <paramref name="index"/> (its subtree is contiguous).</summary>
    private int SubtreeEnd(int index)
    {
        var depth = _nodes[index].Depth;
        var end = index + 1;
        while (end < _count && _nodes[end].Depth > depth)
        {
            end++;
        }

        return end;
    }

    /// <summary>Every rectangle and clip on whole pixels: each edge rounded, so neighbours meet without gaps or overlaps.</summary>
    private void Snap()
    {
        for (var i = 0; i < _count; i++)
        {
            ref var node = ref _nodes[i];
            var left = MathF.Round(node.X);
            var top = MathF.Round(node.Y);
            node.W = MathF.Round(node.X + node.W) - left;
            node.H = MathF.Round(node.Y + node.H) - top;
            node.X = left;
            node.Y = top;
            node.Clip = new SKRect(
                MathF.Round(node.Clip.Left),
                MathF.Round(node.Clip.Top),
                MathF.Round(node.Clip.Right),
                MathF.Round(node.Clip.Bottom)
            );
        }
    }
}
