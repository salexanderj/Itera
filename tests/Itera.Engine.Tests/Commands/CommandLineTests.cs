using Itera.Engine.Commands;

namespace Itera.Engine.Tests.Commands;

public sealed class CommandLineTests
{
    [Fact]
    public void Spaces_separate_words_and_semicolons_separate_commands()
    {
        var commands = CommandLine.Parse("  view 1 ;blend   0.3;; stats ");

        Assert.Equal(3, commands.Count);
        Assert.Equal(["view", "1"], commands[0]);
        Assert.Equal(["blend", "0.3"], commands[1]);
        Assert.Equal(["stats"], commands[2]);
    }

    [Fact]
    public void Quotes_keep_spaces_and_semicolons_in_one_word()
    {
        var commands = CommandLine.Parse("say \"a; b\" c\"d e\"");

        Assert.Single(commands);
        Assert.Equal(["say", "a; b", "cd e"], commands[0]);
    }

    [Fact]
    public void An_empty_pair_of_quotes_is_an_empty_word()
    {
        Assert.Equal(["name", ""], CommandLine.Parse("name \"\"")[0]);
    }

    [Fact]
    public void An_unclosed_quote_runs_to_the_end()
    {
        Assert.Equal(["say", "a; b"], CommandLine.Parse("say \"a; b")[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";;")]
    public void A_line_without_words_has_no_commands(string line)
    {
        Assert.Empty(CommandLine.Parse(line));
    }
}
