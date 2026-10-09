using Itera.Engine.UI;

namespace Itera.Engine.Tests.UI;

public sealed class ConsoleLayerTests
{
    private readonly List<string> _left = [];
    private readonly List<string> _right = [];

    [Fact]
    public void A_line_of_one_column_wraps_at_spaces_across_the_console()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 30));

        var column = ConsoleLayer.Layout(new ConsoleLine(ConsoleLineKind.Log, text), 40, _left, _right);

        Assert.Equal(0, column);
        Assert.Empty(_right);
        Assert.All(_left, row => Assert.True(row.Length <= 40, row));
        Assert.Equal(text, string.Join(' ', _left));
    }

    [Fact]
    public void Each_column_of_a_line_wraps_within_itself()
    {
        var usage = "preset [on|off] | shape|colour|lights name | min|max 1-100";
        var help = string.Join(' ', Enumerable.Repeat("word", 40));

        var column = ConsoleLayer.Layout(new ConsoleLine(ConsoleLineKind.Answer, usage, help, 22), 80, _left, _right);

        Assert.Equal(22, column);
        Assert.True(_left.Count > 1);
        Assert.All(_left, row => Assert.True(row.Length <= 20, row));
        Assert.Equal(
            usage.Replace(" ", "", StringComparison.Ordinal),
            string.Concat(_left).Replace(" ", "", StringComparison.Ordinal)
        );
        Assert.True(_right.Count > 1);
        Assert.All(_right, row => Assert.True(row.Length <= 80 - 22, row));
        Assert.Equal(help, string.Join(' ', _right));
    }

    [Fact]
    public void A_first_column_wraps_under_its_indent()
    {
        var line = new ConsoleLine(
            ConsoleLineKind.Answer,
            "tp x y z [yaw pitch] | x,y,z,yaw,pitch | markers|centre|north|summit|east|south",
            "Moves the player there.",
            38,
            3
        );

        ConsoleLayer.Layout(line, 120, _left, _right);

        Assert.Equal(
            ["tp x y z [yaw pitch] |", "   x,y,z,yaw,pitch | markers|centre|", "   north|summit|east|south"],
            _left
        );
        Assert.Equal(["Moves the player there."], _right);
    }

    [Fact]
    public void An_indent_is_at_most_half_the_width()
    {
        ConsoleLayer.Wrap(new string('x', 30), 10, 8, _left);

        Assert.Equal([new string('x', 10), "     xxxxx", "     xxxxx", "     xxxxx", "     xxxxx"], _left);
    }

    [Fact]
    public void A_line_whose_second_column_has_too_little_room_is_drawn_as_one()
    {
        var line = new ConsoleLine(ConsoleLineKind.Answer, "quit", "Ends the game, as the menu's Quit does.", 30);

        var column = ConsoleLayer.Layout(line, 30 + ConsoleLayer.MinColumnRoom - 1, _left, _right);

        Assert.Equal(0, column);
        Assert.Empty(_right);
        Assert.StartsWith("quit" + new string(' ', 26) + "Ends", string.Join(' ', _left), StringComparison.Ordinal);
    }

    [Fact]
    public void A_usage_breaks_after_a_bar_when_no_space_is_in_reach()
    {
        ConsoleLayer.Wrap("nav [on|off|dense|standard|small|large|huge|stats]", 20, 0, _left);

        Assert.Equal(["nav [on|off|dense|", "standard|small|", "large|huge|stats]"], _left);
    }

    [Fact]
    public void A_word_longer_than_a_row_is_split()
    {
        ConsoleLayer.Wrap(new string('x', 50), 20, 0, _left);

        Assert.Equal([new string('x', 20), new string('x', 20), new string('x', 10)], _left);
    }

    [Fact]
    public void An_empty_column_is_one_empty_row()
    {
        ConsoleLayer.Wrap("", 0, 0, _left);

        Assert.Equal([""], _left);
    }
}
