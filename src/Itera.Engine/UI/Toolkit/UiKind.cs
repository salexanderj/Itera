namespace Itera.Engine.UI.Toolkit;

/// <summary>What an element of a <see cref="Ui"/> is: a container that places its children, or a single element.</summary>
public enum UiKind
{
    /// <summary>Children one after another from left to right.</summary>
    Row,

    /// <summary>Children one after another from top to bottom.</summary>
    Column,

    /// <summary>Children over each other, each placed by the alignment.</summary>
    Stack,

    /// <summary>Children in rows of equal cells, a given number across.</summary>
    Grid,

    /// <summary>A column that shows part of its children and scrolls through the rest.</summary>
    Scroll,

    /// <summary>A text.</summary>
    Text,

    /// <summary>A button with a label: clicked when released on itself.</summary>
    Button,

    /// <summary>A switch, on or off.</summary>
    Switch,

    /// <summary>A slider over a range.</summary>
    Slider,

    /// <summary>A box that takes typing (<see cref="TextEntry"/>).</summary>
    TextBox,

    /// <summary>An area the game draws itself, which may take the pointer.</summary>
    Area,

    /// <summary>Empty room that takes nothing.</summary>
    Space,
}
