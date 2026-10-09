using Itera.Engine.UI;
using Silk.NET.Input;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="TextLine"/>: characters at the cursor, a pasted first line, the editing keys (by word with Ctrl), the
/// length limit, and a version that rises only on a change.
/// </summary>
public sealed class TextLineTests
{
    [Fact]
    public void It_inserts_at_the_cursor_and_edits_with_the_keys()
    {
        var line = new TextLine(40);
        foreach (var character in "join host:1")
        {
            line.Insert(character);
        }

        line.Edit(Key.Left, control: true, out var repeats);
        Assert.True(repeats);
        Assert.Equal(5, line.Cursor);
        line.Edit(Key.Backspace, control: false, out _);
        Assert.Equal("joinhost:1", line.Text);
        line.Edit(Key.Home, control: false, out repeats);
        Assert.False(repeats);
        line.Edit(Key.Delete, control: false, out _);
        Assert.Equal("oinhost:1", line.Text);
        line.Edit(Key.End, control: false, out _);
        Assert.Equal(line.Length, line.Cursor);
        Assert.False(line.Edit(Key.Enter, control: false, out _));

        line.Edit(Key.U, control: true, out _);
        Assert.Equal("", line.Text);
    }

    [Fact]
    public void A_paste_takes_its_first_line_as_far_as_the_line_has_room()
    {
        var line = new TextLine(8);
        line.Paste("127.0.0.1:47600\nsecond");

        Assert.Equal("127.0.0.", line.Text);
        Assert.False(line.Insert('x'));
        Assert.False(line.Insert('\t'));
    }

    [Fact]
    public void Its_version_rises_only_when_something_changes()
    {
        var line = new TextLine(10);
        var version = line.Version;
        line.Edit(Key.Backspace, control: false, out _);
        line.MoveCursor(3);
        Assert.Equal(version, line.Version);

        line.Insert('a');
        Assert.NotEqual(version, line.Version);
    }
}
