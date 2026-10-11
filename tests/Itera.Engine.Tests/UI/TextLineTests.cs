using System.Text;
using Itera.Engine.UI;
using Silk.NET.Input;

namespace Itera.Engine.Tests.UI;

/// <summary>
/// <see cref="TextLine"/>: characters at the cursor, a pasted first line, the editing keys (by word with Ctrl), the
/// length limit, a version that rises only on a change, and characters past U+FFFF kept whole.
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

    [Fact]
    public void A_character_past_the_basic_plane_is_inserted_moved_over_and_deleted_whole()
    {
        var line = new TextLine(40);
        line.Insert(new Rune('a'));
        line.Insert(new Rune(0x1F600));
        line.Insert(new Rune('b'));
        Assert.Equal("a\U0001F600b", line.Text);
        Assert.Equal(4, line.Cursor);

        line.Edit(Key.Left, control: false, out _);
        line.Edit(Key.Left, control: false, out _);
        Assert.Equal(1, line.Cursor);
        line.Edit(Key.Right, control: false, out _);
        Assert.Equal(3, line.Cursor);
        line.Edit(Key.Backspace, control: false, out _);
        Assert.Equal("ab", line.Text);

        line.Set("\U0001F600\U0001F601", cursor: 0);
        line.Edit(Key.Delete, control: false, out _);
        Assert.Equal("\U0001F601", line.Text);

        // A place inside a pair moves to the pair's start.
        line.MoveCursor(1);
        Assert.Equal(0, line.Cursor);
        Assert.False(line.Insert('\uD83D'));
    }

    [Fact]
    public void A_pair_goes_in_only_where_both_halves_fit_and_is_never_cut()
    {
        var line = new TextLine(3);
        line.Insert(new Rune('a'));
        line.Insert(new Rune('b'));
        Assert.False(line.Insert(new Rune(0x1F600)));
        Assert.True(line.Insert(new Rune('c')));
        Assert.Equal("abc", line.Text);

        line.Set("ab\U0001F600");
        Assert.Equal("ab", line.Text);

        line.Set("");
        line.Paste("x\U0001F600yz");
        Assert.Equal("x\U0001F600", line.Text);

        line.Set("");
        line.Paste("a\tb\uD800c");
        Assert.Equal("ab\uFFFD", line.Text);
    }

    [Fact]
    public void A_cursor_set_inside_a_pair_and_the_jumps_keep_off_its_middle()
    {
        var line = new TextLine(40);
        line.Set("a\U0001F600 b\U0001F601", cursor: 2);
        Assert.Equal(1, line.Cursor);

        line.Edit(Key.End, control: false, out _);
        Assert.Equal(line.Length, line.Cursor);
        line.Edit(Key.Left, control: true, out _);
        Assert.Equal(4, line.Cursor);
        line.Edit(Key.Home, control: false, out _);
        line.Edit(Key.Right, control: true, out _);
        Assert.Equal(3, line.Cursor);
    }
}
