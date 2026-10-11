using System.Globalization;
using System.Text;

namespace Itera.Engine.UI.Toolkit;

/// <summary>The last layout as text: one line an element, for tests, the console's <c>ui</c>, and agents.</summary>
public sealed partial class Ui
{
    /// <summary>
    /// The last layout, one line an element, indented by depth: its kind, its id (<c>#12</c>), its text in quotes (a text
    /// box's typing, else its placeholder), its rectangle as x,y and width x height in whole pixels, and what else it
    /// shows (a switch's on or off, a slider's value, a text's lines when more than one, <c>cut</c> where text was cut,
    /// <c>panel</c>, <c>float</c>, and the element's state). Allocates: for tools and tests, not each frame.
    /// </summary>
    public string Dump()
    {
        var text = new StringBuilder();
        for (var i = 0; i < Count; i++)
        {
            DumpLine(text, i);
        }

        return text.ToString();
    }

    private void DumpLine(StringBuilder text, int index)
    {
        ref readonly var node = ref _nodes[index];
        var invariant = CultureInfo.InvariantCulture;
        text.Append(' ', 2 * node.Depth).Append(node.Kind.ToString());
        if (node.Id != 0)
        {
            text.Append(invariant, $" #{node.Id}");
        }

        if (HasText(node.Kind))
        {
            text.Append(" \"").Append(Shown(index)).Append('"');
        }

        text.Append(invariant, $" {node.X:0},{node.Y:0} {node.W:0}x{node.H:0}");
        if (node.Kind == UiKind.Switch)
        {
            text.Append(node.On ? " on" : " off");
        }

        if (node.Kind == UiKind.Slider)
        {
            text.Append(invariant, $" value={node.Value:0.###}");
        }

        if (node.LineCount > 1)
        {
            text.Append(invariant, $" lines={node.LineCount}");
        }

        for (var line = node.FirstLine; line < node.FirstLine + node.LineCount; line++)
        {
            if (_lines[line].Ellipsis)
            {
                text.Append(" cut");
                break;
            }
        }

        if (node.Box.Panel)
        {
            text.Append(" panel");
        }

        if (node.Box.Anchor is not null)
        {
            text.Append(" float");
        }

        DumpState(text, index);
        text.Append('\n');
    }
}
