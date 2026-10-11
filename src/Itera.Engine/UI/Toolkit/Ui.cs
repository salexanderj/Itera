using SkiaSharp;

namespace Itera.Engine.UI.Toolkit;

/// <summary>
/// A UI described in code: between <see cref="Begin"/> and <see cref="End"/> the code declares containers (scopes it
/// closes with <c>using</c>: <see cref="Row"/>, <see cref="Column"/>, <see cref="Stack"/>, <see cref="Grid"/>,
/// <see cref="Scroll"/>) and elements (<see cref="Text(string, in UiText, in UiBox)"/>, <see cref="Button"/>,
/// <see cref="Switch"/>, <see cref="Slider"/>, <see cref="TextBox"/>, <see cref="Area"/>) in the order they show, and
/// <see cref="End"/> lays them out: each element's rectangle in screen pixels from its box (<see cref="UiBox"/>; the
/// style's defaults where the code left a part, <see cref="UiStyle.DefaultBox"/>), its text measured, wrapped, and cut to
/// fit. Ids are the code's: an element that keeps its id keeps its state, and <see cref="Rect"/>, <see cref="TryFind"/>,
/// and <see cref="Dump"/> name it by it; 0 is no id (a text, a space, or a container nothing asks about); <see cref="Id(string)"/>
/// makes one from a name. Each element returns its <see cref="UiState"/> from the last layout. Elements declared outside
/// any container each start at the screen's top left, at most as large as the screen when they fit their content.
/// Declaring and laying out allocate nothing once the pools have grown (the strings are the code's; a text box's typing
/// is made into a string once after each change). Units are pixels of a screen <see cref="ReferenceHeight"/> pixels
/// high, times <see cref="UnitPixels"/> (<see cref="UnitPixelsFor"/>). A declaration that threw is dropped by the next
/// <see cref="Begin"/>. Main thread.
/// </summary>
public sealed partial class Ui
{
    /// <summary>The screen height units are given for: a unit is a pixel on a screen this high at a UI scale of 1.</summary>
    public const float ReferenceHeight = 1080;

    private const int NoNode = -1;

    private readonly IUiTextMeasurer _measurer;
    private UiNode[] _nodes = new UiNode[64];
    private string?[] _texts = new string?[64];
    private TextEntry?[] _entries = new TextEntry?[64];
    private UiLine[] _lines = new UiLine[64];
    private int _count;
    private int _lineCount;
    private int _open = NoNode;
    private bool _building;

    /// <summary>The most elements a layout since the last clearing declared.</summary>
    private int _highWater;

    /// <summary>The ids declared, to their elements' indices (checked for ids declared twice).</summary>
    private readonly Dictionary<int, int> _byId = [];

    /// <summary>Where each id was last laid out: the rectangles the input is matched against while the next is declared.</summary>
    private readonly Dictionary<int, SKRect> _lastRects = [];

    /// <summary>How far each scroll area is scrolled, in pixels, by id.</summary>
    private readonly Dictionary<int, float> _scrolls = [];

    /// <param name="measurer">How wide text is and how tall its lines are.</param>
    /// <param name="style">The look and the sizes the code leaves to it.</param>
    public Ui(IUiTextMeasurer measurer, UiStyle style)
    {
        ArgumentNullException.ThrowIfNull(measurer);
        ArgumentNullException.ThrowIfNull(style);
        _measurer = measurer;
        Style = style;
    }

    /// <summary>The look, and the sizes the code leaves to it; read at each declaration.</summary>
    public UiStyle Style { get; set; }

    /// <summary>Screen pixels per unit in the layout being built or last laid out.</summary>
    public float UnitPixels { get; private set; } = 1;

    /// <summary>The screen's width in pixels.</summary>
    public float ScreenWidth { get; private set; }

    /// <summary>The screen's height in pixels.</summary>
    public float ScreenHeight { get; private set; }

    /// <summary>How many elements were laid out last, containers included; none while a layout is being declared.</summary>
    public int Count => _building ? 0 : _count;

    /// <summary>
    /// Screen pixels a unit on a screen <paramref name="screenHeight"/> pixels high at a UI scale of
    /// <paramref name="scale"/>: sizes grow with the screen's height, so the UI takes the same share of any screen.
    /// </summary>
    public static float UnitPixelsFor(float screenHeight, float scale) => screenHeight / ReferenceHeight * scale;

    /// <summary>
    /// An id from a name, the same in every run (FNV-1a of its characters; never 0), so code and tests name elements
    /// rather than number them: <c>static readonly int Play = Ui.Id("menu.play");</c>.
    /// </summary>
    public static int Id(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var hash = 2166136261u;
        foreach (var character in name)
        {
            hash = (hash ^ character) * 16777619u;
        }

        return hash == 0 ? 1 : (int)hash;
    }

    /// <summary>The id of item <paramref name="index"/> of a list whose id is <paramref name="list"/> (rows, slots); never 0.</summary>
    public static int Id(int list, int index)
    {
        var hash = ((uint)list ^ 2166136261u) * 16777619u;
        hash = (hash ^ (uint)index) * 16777619u;
        return hash == 0 ? 1 : (int)hash;
    }

    /// <summary>
    /// A hash of the last layout: every element's kind, id, rectangle, text, setting, and value. Equal hashes draw the same
    /// picture, so a layer draws again only when it changes.
    /// </summary>
    public int Hash { get; private set; }

    /// <summary>
    /// Starts declaring a UI for a screen of <paramref name="width"/> x <paramref name="height"/> pixels, at
    /// <paramref name="unitPixels"/> pixels a unit (<see cref="UnitPixelsFor"/>).
    /// </summary>
    public void Begin(float width, float height, float unitPixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitPixels);
        // A declaration that threw before its End is dropped.
        ScreenWidth = width;
        ScreenHeight = height;
        UnitPixels = unitPixels;
        _count = 0;
        _lineCount = 0;
        _open = NoNode;
        _building = true;
    }

    /// <summary>Ends the declaration and lays it out. Every container must be closed.</summary>
    public void End()
    {
        if (!_building)
        {
            throw new InvalidOperationException("Ui.End: no Begin.");
        }

        _building = false;
        if (_open != NoNode)
        {
            var open = _nodes[_open];
            _count = 0;
            throw new InvalidOperationException($"Ui.End: the {open.Kind} with id {open.Id} was never closed.");
        }

        IndexIds();
        Layout();
        Hash = ComputeHash();
        _lastRects.Clear();
        foreach (var (id, index) in _byId)
        {
            _lastRects[id] = RectOf(index);
        }

        // No string or typing of an element no longer declared stays reachable.
        Array.Clear(_texts, _count, _highWater - _count);
        Array.Clear(_entries, _count, _highWater - _count);
        _highWater = _count;
    }

    /// <summary>Children one after another from left to right; close it with <c>using</c>.</summary>
    public UiScope Row(int id = 0, in UiBox box = default) => Open(UiKind.Row, id, in box, 0);

    /// <summary>Children one after another from top to bottom; close it with <c>using</c>.</summary>
    public UiScope Column(int id = 0, in UiBox box = default) => Open(UiKind.Column, id, in box, 0);

    /// <summary>Children over each other, each placed by the box's alignment; close it with <c>using</c>.</summary>
    public UiScope Stack(int id = 0, in UiBox box = default) => Open(UiKind.Stack, id, in box, 0);

    /// <summary>
    /// Children in rows of <paramref name="columns"/> equal cells, filled left to right, then down; each row as tall as
    /// its tallest child; children placed in their cells by the box's alignment. Close it with <c>using</c>.
    /// </summary>
    public UiScope Grid(int id, int columns, in UiBox box = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
        return Open(UiKind.Grid, id, in box, columns);
    }

    /// <summary>
    /// A column that shows what fits its own height and scrolls through the rest; give it a height (fixed, grown, or a
    /// share). Close it with <c>using</c>.
    /// </summary>
    public UiScope Scroll(int id, in UiBox box = default) => Open(UiKind.Scroll, id, in box, 0);

    /// <summary>A text with no id.</summary>
    public UiState Text(string text, in UiText setting = default, in UiBox box = default) =>
        Text(0, text, in setting, in box);

    /// <summary>A text, set as <paramref name="setting"/>.</summary>
    public UiState Text(int id, string text, in UiText setting = default, in UiBox box = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var index = Add(UiKind.Text, id, in box);
        _texts[index] = text;
        _nodes[index].TextSetting = setting;
        return StateOf(index);
    }

    /// <summary>A button with <paramref name="label"/>; <see cref="UiState.Clicked"/> when released on itself.</summary>
    public UiState Button(int id, string label, in UiText setting = default, in UiBox box = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        var index = Add(UiKind.Button, id, in box);
        _texts[index] = label;
        _nodes[index].TextSetting = setting;
        return StateOf(index);
    }

    /// <summary>
    /// A switch showing <paramref name="on"/>; <see cref="UiState.Clicked"/> when clicked, and the code flips its value
    /// (the switch shows the value given at its next declaration).
    /// </summary>
    public UiState Switch(int id, bool on, in UiBox box = default)
    {
        var index = Add(UiKind.Switch, id, in box);
        _nodes[index].On = on;
        return StateOf(index);
    }

    /// <summary>
    /// A slider at <paramref name="value"/> between <paramref name="least"/> and <paramref name="most"/>; when the pointer
    /// or a key moves it, <see cref="UiState.Changed"/> and the new <see cref="UiState.Value"/>.
    /// </summary>
    public UiState Slider(int id, float value, float least, float most, in UiBox box = default)
    {
        if (!(most > least))
        {
            throw new ArgumentOutOfRangeException(nameof(most), most, "A slider's most must be above its least.");
        }

        var index = Add(UiKind.Slider, id, in box);
        ref var node = ref _nodes[index];
        node.Least = least;
        node.Most = most;
        node.Value = Math.Clamp(value, least, most);
        return StateOf(index);
    }

    /// <summary>
    /// A box showing <paramref name="entry"/>'s line, or <paramref name="placeholder"/> (dim) while it is empty; clicking it
    /// gives it the typing.
    /// </summary>
    public UiState TextBox(int id, TextEntry entry, string? placeholder = null, in UiBox box = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var index = Add(UiKind.TextBox, id, in box);
        _entries[index] = entry;
        _texts[index] = placeholder;
        return StateOf(index);
    }

    /// <summary>An area the game draws itself (<c>UiLayer.DrawArea</c>), which takes the pointer like a button.</summary>
    public UiState Area(int id, in UiBox box = default)
    {
        var index = Add(UiKind.Area, id, in box);
        return StateOf(index);
    }

    /// <summary>Empty room that takes nothing: a gap of its own size, or, growing, what pushes its neighbours apart.</summary>
    public void Space(in UiBox box) => Add(UiKind.Space, 0, in box);

    /// <summary>
    /// Scrolls scroll area <paramref name="id"/> to <paramref name="pixels"/> from its top (kept within its content at
    /// the next layout).
    /// </summary>
    public void ScrollTo(int id, float pixels) => _scrolls[id] = Math.Max(0, pixels);

    /// <summary>Where element <paramref name="id"/> was last laid out, in screen pixels; empty when it was not.</summary>
    public SKRect Rect(int id) => _lastRects.TryGetValue(id, out var rect) ? rect : SKRect.Empty;

    /// <summary>The element laid out last whose text or label is <paramref name="label"/>, first in order.</summary>
    public bool TryFind(string label, out UiElement element)
    {
        ArgumentNullException.ThrowIfNull(label);
        for (var i = 0; i < Count; i++)
        {
            if (string.Equals(_texts[i], label, StringComparison.Ordinal))
            {
                element = ElementAt(i);
                return true;
            }
        }

        element = default;
        return false;
    }

    /// <summary>Element <paramref name="index"/> of the last layout, in the order declared (containers before their children).</summary>
    public UiElement ElementAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        ref readonly var node = ref _nodes[index];
        return new UiElement(
            index,
            node.Kind,
            node.Id,
            RectOf(index),
            node.Clip,
            _texts[index],
            _entries[index],
            node.TextSetting,
            node.TextPixels,
            node.LineHeight,
            node.FirstLine,
            node.LineCount,
            node.On,
            node.Value,
            node.Least,
            node.Most,
            node.Box.Panel,
            node.Depth,
            node.Floating
        );
    }

    /// <summary>Line <paramref name="index"/> of the last layout's texts (<see cref="UiElement.FirstLine"/>).</summary>
    public UiLine Line(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _lineCount);
        return _lines[index];
    }

    /// <summary>Closes container <paramref name="index"/> (<see cref="UiScope.Dispose"/>).</summary>
    internal void Close(int index)
    {
        if (_building && index < _count && _nodes[index].Closed)
        {
            return;
        }

        if (!_building || _open != index)
        {
            throw new InvalidOperationException("Ui: a container was closed out of order, or outside Begin and End.");
        }

        _nodes[index].Closed = true;
        _open = _nodes[index].Parent;
    }

    private UiScope Open(UiKind kind, int id, in UiBox box, int columns)
    {
        var index = Add(kind, id, in box);
        _nodes[index].Columns = columns;
        _open = index;
        return new UiScope(this, index);
    }

    /// <summary>Appends an element to the container open now, with the style's defaults under its box.</summary>
    private int Add(UiKind kind, int id, in UiBox box)
    {
        if (!_building)
        {
            throw new InvalidOperationException("Ui: elements are declared between Begin and End.");
        }

        if (_count == _nodes.Length)
        {
            Grow();
        }

        var index = _count++;
        _highWater = Math.Max(_highWater, _count);
        var parent = _open;
        var floating = box.Anchor is not null || (parent != NoNode && _nodes[parent].Floating);
        _nodes[index] = new UiNode
        {
            Kind = kind,
            Id = id,
            Parent = parent,
            FirstChild = NoNode,
            LastChild = NoNode,
            Next = NoNode,
            Depth = parent == NoNode ? 0 : _nodes[parent].Depth + 1,
            Box = box.Over(Style.DefaultBox(kind)),
            Floating = floating,
        };
        _texts[index] = null;
        _entries[index] = null;
        if (parent != NoNode)
        {
            ref var container = ref _nodes[parent];
            if (container.LastChild == NoNode)
            {
                container.FirstChild = index;
            }
            else
            {
                _nodes[container.LastChild].Next = index;
            }

            container.LastChild = index;
            container.Children++;
        }

        return index;
    }

    private void Grow()
    {
        var size = _nodes.Length * 2;
        Array.Resize(ref _nodes, size);
        Array.Resize(ref _texts, size);
        Array.Resize(ref _entries, size);
    }

    /// <summary>Element <paramref name="index"/>'s state as declared: its rectangle from the last layout.</summary>
    private UiState StateOf(int index)
    {
        var id = _nodes[index].Id;
        return new UiState(id, Rect(id), Value: _nodes[index].Value);
    }

    /// <summary>Maps the ids declared to their elements; an id declared twice throws.</summary>
    private void IndexIds()
    {
        _byId.Clear();
        for (var i = 0; i < _count; i++)
        {
            var id = _nodes[i].Id;
            if (id != 0 && !_byId.TryAdd(id, i))
            {
                throw new InvalidOperationException(
                    $"Ui: id {id} is declared twice ({_nodes[_byId[id]].Kind} and {_nodes[i].Kind}); each element's id is its own."
                );
            }
        }
    }

    /// <summary>The element's state for <see cref="Dump"/> (none until the input is matched against the layout).</summary>
    private static void DumpState(System.Text.StringBuilder text, int id) { }

    private SKRect RectOf(int index)
    {
        ref readonly var node = ref _nodes[index];
        return new SKRect(node.X, node.Y, node.X + node.W, node.Y + node.H);
    }

    private int ComputeHash()
    {
        var hash = new HashCode();
        hash.Add(_count);
        for (var i = 0; i < _count; i++)
        {
            ref readonly var node = ref _nodes[i];
            hash.Add(node.Kind);
            hash.Add(node.Id);
            hash.Add(node.X);
            hash.Add(node.Y);
            hash.Add(node.W);
            hash.Add(node.H);
            hash.Add(node.TextSetting);
            hash.Add(node.On);
            hash.Add(node.Value);
            hash.Add(node.Least);
            hash.Add(node.Most);
            hash.Add(node.Box.Panel);
            hash.Add(node.Clip.Left);
            hash.Add(node.Clip.Top);
            hash.Add(node.Clip.Right);
            hash.Add(node.Clip.Bottom);
            // The text by its characters: an equal string made again draws the same.
            hash.Add(_texts[i] is { } text ? string.GetHashCode(text, StringComparison.Ordinal) : 0);
            hash.Add(_entries[i] is { } entry ? entry.Line.Version : 0);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A container of a <see cref="Ui"/> being filled; disposing it closes the container (<c>using</c>).</summary>
public readonly ref struct UiScope
{
    private readonly Ui _ui;
    private readonly int _index;

    internal UiScope(Ui ui, int index)
    {
        _ui = ui;
        _index = index;
    }

    /// <summary>Closes the container.</summary>
    public void Dispose() => _ui.Close(_index);
}
