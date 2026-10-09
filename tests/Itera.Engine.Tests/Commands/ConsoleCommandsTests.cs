using Itera.Engine.Commands;
using Itera.Engine.Networking;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Commands;

public sealed class ConsoleCommandsTests
{
    private readonly RecordingOutput _output = new();
    private SessionRole _role = SessionRole.Offline;
    private readonly ConsoleCommands _commands;

    public ConsoleCommandsTests()
    {
        _commands = new ConsoleCommands(() => _role, NullLogger<ConsoleCommands>.Instance);
    }

    [Fact]
    public void A_command_gets_its_arguments_and_answers()
    {
        _commands.Register("add", "a b", "Adds.", CommandScope.Local, Add);

        _commands.Execute("ADD 2 3.5", _output);

        Assert.Equal(["5.5"], _output.Answers);
        Assert.Empty(_output.Failures);
    }

    [Fact]
    public void An_output_that_does_not_wrap_gets_two_columns_as_one_line()
    {
        _commands.Register(
            "table",
            "",
            "",
            CommandScope.Local,
            context =>
            {
                context.PrintColumns("a", "first", 6, 0);
                context.PrintColumns("longer name", "second", 6, 2);
            }
        );

        _commands.Execute("table", _output);

        Assert.Equal(["a     first", "longer name  second"], _output.Answers);
    }

    [Fact]
    public void Names_are_unique_and_one_word()
    {
        _commands.Register("add", "", "", CommandScope.Local, Add);

        Assert.Throws<InvalidOperationException>(() => _commands.Register("Add", "", "", CommandScope.Local, Add));
        Assert.Throws<ArgumentException>(() => _commands.Register("two words", "", "", CommandScope.Local, Add));
        Assert.Throws<ArgumentException>(() => _commands.Register("a;b", "", "", CommandScope.Local, Add));
    }

    [Fact]
    public void A_matchs_commands_go_when_it_ends_and_the_engines_stay()
    {
        _commands.Register("help", "", "", CommandScope.Local, Add);
        _commands.BeginMatch();
        _commands.Register("spawn", "", "", CommandScope.Match, Add);
        // An owner of its own, as an application's systems give: still the match's to remove.
        _commands.Register("tp", "", "", CommandScope.Match, Add, "sample");

        Assert.Equal(["help", "spawn", "tp"], _commands.All.Select(c => c.Name));
        Assert.Equal(ConsoleCommands.MatchOwner, _commands.All.First(c => c.Name == "spawn").Owner);
        Assert.Equal("sample", _commands.All.First(c => c.Name == "tp").Owner);

        _commands.EndMatch();
        Assert.Equal(["help"], _commands.All.Select(c => c.Name));

        // The next match registers its own again, and a command registered with no match is the engine's.
        _commands.BeginMatch();
        _commands.Register("tp", "", "", CommandScope.Match, Add);
        _commands.EndMatch();
        _commands.Register("leave", "", "", CommandScope.Local, Add);
        Assert.Equal(["help", "leave"], _commands.All.Select(c => c.Name));
    }

    [Fact]
    public void An_unknown_command_is_reported()
    {
        Assert.False(_commands.Run(["nosuch"], _output));

        Assert.Equal(["nosuch: no such command; 'help' lists them."], _output.Failures);
    }

    [Theory]
    [InlineData(SessionRole.Offline, CommandScope.Local, true)]
    [InlineData(SessionRole.Offline, CommandScope.Match, true)]
    [InlineData(SessionRole.Offline, CommandScope.Offline, true)]
    [InlineData(SessionRole.Host, CommandScope.Local, true)]
    [InlineData(SessionRole.Host, CommandScope.Match, true)]
    [InlineData(SessionRole.Host, CommandScope.Offline, false)]
    [InlineData(SessionRole.Client, CommandScope.Local, true)]
    [InlineData(SessionRole.Client, CommandScope.Match, false)]
    [InlineData(SessionRole.Client, CommandScope.Offline, false)]
    public void The_scope_decides_where_a_command_runs(SessionRole role, CommandScope scope, bool runs)
    {
        var ran = false;
        _commands.Register("go", "", "", scope, _ => ran = true);
        _role = role;

        Assert.Equal(runs, _commands.Run(["go"], _output));

        Assert.Equal(runs, ran);
        Assert.Equal(runs ? 0 : 1, _output.Failures.Count);
    }

    [Fact]
    public void A_command_that_throws_is_reported_and_the_next_one_still_runs()
    {
        _commands.Register("boom", "", "", CommandScope.Local, _ => throw new InvalidOperationException("bang"));
        _commands.Register("add", "", "", CommandScope.Local, Add);

        _commands.Execute("boom; add 1 1", _output);

        Assert.Equal(["boom failed: bang"], _output.Failures);
        Assert.Equal(["2"], _output.Answers);
    }

    [Fact]
    public void An_owner_s_commands_go_together()
    {
        _commands.Register("a", "", "", CommandScope.Local, Add, owner: "mod");
        _commands.Register("b", "", "", CommandScope.Local, Add, owner: "mod");
        _commands.Register("c", "", "", CommandScope.Local, Add);

        Assert.Equal(2, _commands.UnregisterOwner("mod"));

        Assert.Equal(["c"], _commands.All.Select(c => c.Name));
    }

    [Fact]
    public void Names_complete_from_a_prefix_in_order()
    {
        _commands.Register("linework", "", "", CommandScope.Local, Add);
        _commands.Register("lines", "", "", CommandScope.Local, Add);
        _commands.Register("view", "", "", CommandScope.Local, Add);

        Assert.Equal(["lines", "linework"], _commands.Complete("LIN"));
    }

    [Fact]
    public void Arguments_read_as_numbers_and_switches()
    {
        CommandContext? seen = null;
        _commands.Register("probe", "", "", CommandScope.Local, context => seen = context);

        _commands.Run(["probe", "on", "OFF", "1,5", "-2", "x"], _output);

        Assert.NotNull(seen);
        Assert.True(seen.TryGetOnOff(0, out var on) && on);
        Assert.True(seen.TryGetOnOff(1, out var off) && !off);
        Assert.False(seen.TryGetFloat(2, out _));
        Assert.True(seen.TryGetInt(3, out var whole) && whole == -2);
        Assert.False(seen.TryGetOnOff(4, out _));
        Assert.False(seen.TryGetFloat(5, out _));
        Assert.True(seen.Is(4, "X"));
    }

    private static void Add(CommandContext context)
    {
        if (context.TryGetFloat(0, out var a) && context.TryGetFloat(1, out var b))
        {
            context.Print(CommandContext.Number(a + b));
        }
    }

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Answers { get; } = [];

        public List<string> Failures { get; } = [];

        public void Print(string text) => Answers.Add(text);

        public void PrintError(string text) => Failures.Add(text);
    }
}
