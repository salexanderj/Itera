using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Networking;
using Itera.Engine.UI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.Tests.UI;

public sealed class DevConsoleTests
{
    private readonly ConsoleLogBuffer _log = new();
    private readonly ConsoleCommands _commands = new(() => SessionRole.Offline, NullLogger<ConsoleCommands>.Instance);
    private readonly List<string> _ran = [];

    public DevConsoleTests()
    {
        _commands.Register("say", "text", "", CommandScope.Local, Say);
        _commands.Register("lines", "", "", CommandScope.Local, Say);
        _commands.Register("linework", "", "", CommandScope.Local, Say);
        _commands.Register(
            "table",
            "",
            "",
            CommandScope.Local,
            context => context.PrintColumns("name", "what it does", 6, 2)
        );
    }

    [Fact]
    public void Typing_edits_the_line_at_the_cursor()
    {
        var console = Create();
        console.Open();

        Type(console, "sy");
        console.KeyDown(Key.Left, false);
        console.KeyUp(Key.Left);
        console.Character('a');
        console.KeyDown(Key.End, false);
        Type(console, " hi");
        console.KeyDown(Key.Home, false);
        console.KeyDown(Key.Delete, false);
        console.KeyUp(Key.Delete);

        Assert.Equal("ay hi", console.Line);
        Assert.Equal(0, console.Cursor);
    }

    [Fact]
    public void Control_characters_are_not_typed()
    {
        var console = Create();
        console.Open();

        console.Character('\t');
        console.Character('\b');
        console.Character('x');

        Assert.Equal("x", console.Line);
    }

    [Fact]
    public void Enter_queues_the_line_and_it_runs_at_the_next_pending_run()
    {
        var console = Create();
        console.Open();
        Type(console, "say one; say two");

        console.KeyDown(Key.Enter, false);

        Assert.Equal("", console.Line);
        Assert.Empty(_ran);
        console.RunPending();
        Assert.Equal(["one", "two"], _ran);
    }

    [Fact]
    public void Up_and_down_step_through_the_history_and_back_to_the_draft()
    {
        var console = Create();
        console.Open();
        Enter(console, "say one");
        Enter(console, "say two");
        Type(console, "draft");

        Press(console, Key.Up);
        Assert.Equal("say two", console.Line);
        Press(console, Key.Up);
        Assert.Equal("say one", console.Line);
        Press(console, Key.Up);
        Assert.Equal("say one", console.Line);
        Press(console, Key.Down);
        Press(console, Key.Down);
        Assert.Equal("draft", console.Line);
    }

    [Fact]
    public void A_held_key_repeats_after_a_delay_and_platform_repeats_are_ignored()
    {
        var console = Create();
        console.Open();
        console.Update(0);
        Type(console, "abcdefghij");

        console.KeyDown(Key.Backspace, false);
        console.KeyDown(Key.Backspace, false);
        Assert.Equal("abcdefghi", console.Line);

        console.Update(DevConsole.RepeatDelaySeconds - 0.01);
        Assert.Equal("abcdefghi", console.Line);
        console.Update(DevConsole.RepeatDelaySeconds + 0.001);
        Assert.Equal("abcdefgh", console.Line);
        console.Update(DevConsole.RepeatDelaySeconds + 2.5 / DevConsole.RepeatsPerSecond);
        Assert.Equal("abcdef", console.Line);

        console.KeyUp(Key.Backspace);
        console.Update(10);
        Assert.Equal("abcdef", console.Line);
    }

    [Fact]
    public void Tab_completes_one_match_and_lists_several()
    {
        var console = Create();
        console.Open();

        Type(console, "sa");
        Press(console, Key.Tab);
        Assert.Equal("say ", console.Line);

        Press(console, Key.Home);
        console.KeyDown(Key.U, true);
        Type(console, "l");
        Press(console, Key.Tab);
        Assert.Equal("line", console.Line);
        Assert.Equal(new ConsoleLine(ConsoleLineKind.Answer, "lines  linework"), Last(console));
    }

    [Fact]
    public void Paste_takes_the_first_line_only()
    {
        var console = Create();
        console.Open();

        console.Paste("say a\nsay b");

        Assert.Equal("say a", console.Line);
    }

    [Fact]
    public void Log_lines_that_came_first_go_in_before_an_answer()
    {
        var console = Create();
        _log.Add(LogLevel.Information, "Console: say hi");
        _log.Add(LogLevel.Warning, "careful");

        console.Print("hi");

        Assert.Equal(3, console.ScrollbackCount);
        Assert.Equal(new ConsoleLine(ConsoleLineKind.Log, "Console: say hi"), console.ScrollbackLine(0));
        Assert.Equal(new ConsoleLine(ConsoleLineKind.Warning, "careful"), console.ScrollbackLine(1));
        Assert.Equal(new ConsoleLine(ConsoleLineKind.Answer, "hi"), console.ScrollbackLine(2));
    }

    [Fact]
    public void An_answer_keeps_its_columns_also_when_copied()
    {
        var console = Create();
        var copy = new RecordingOutput();

        console.Submit("table");
        console.RunPending();
        console.RunNow("table", copy);

        var line = new ConsoleLine(ConsoleLineKind.Answer, "name", "what it does", 6, 2);
        Assert.Equal(line, console.ScrollbackLine(0));
        Assert.Equal(line, Last(console));
        Assert.Equal([("name", "what it does", 6, 2)], copy.Answers);
    }

    [Fact]
    public void The_scrollback_keeps_its_newest_lines()
    {
        var console = Create(scrollback: 100);

        for (var i = 0; i < 250; i++)
        {
            console.Print(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Equal(100, console.ScrollbackCount);
        Assert.Equal("150", console.ScrollbackLine(0).Text);
        Assert.Equal("249", Last(console).Text);
    }

    [Fact]
    public void Scrolling_stays_on_the_lines_shown_as_new_ones_arrive()
    {
        var console = Create();
        console.Open();
        for (var i = 0; i < 30; i++)
        {
            console.Print("line");
        }

        Press(console, Key.PageUp);
        Assert.Equal(10, console.ScrollOffset);
        console.Print("new");
        Assert.Equal(11, console.ScrollOffset);
        console.Scroll(-10);
        Assert.Equal(0, console.ScrollOffset);
    }

    /// <summary>Small turns of the wheel, as a touchpad sends, add up.</summary>
    [Fact]
    public void Small_wheel_turns_add_up()
    {
        var console = Create();
        console.Open();
        for (var i = 0; i < 30; i++)
        {
            console.Print("line");
        }

        // An eighth of a step is three eighths of a line: each one alone moves nothing.
        for (var i = 0; i < 8; i++)
        {
            console.Scroll(0.125f);
        }

        Assert.Equal(3, console.ScrollOffset);
    }

    [Fact]
    public void The_startup_commands_run_once()
    {
        var console = Create(run: "say a; say b");

        console.RunPending();
        console.RunPending();

        Assert.Equal(["a", "b"], _ran);
    }

    [Fact]
    public void A_disabled_console_runs_nothing()
    {
        var console = Create(run: "say a", enabled: false);
        console.Submit("say b");

        console.RunPending();

        Assert.False(console.Enabled);
        Assert.Empty(_ran);
    }

    private DevConsole Create(string? run = null, bool enabled = true, int scrollback = 1000)
    {
        var options = new EngineOptions();
        options.Console.Run = run;
        options.Console.Enabled = enabled;
        options.Console.ScrollbackLines = scrollback;
        return new DevConsole(Options.Create(options), _commands, _log, NullLogger<DevConsole>.Instance);
    }

    private void Say(CommandContext context)
    {
        var text = context.Count > 0 ? context[0] : "";
        _ran.Add(text);
        context.Print(text);
    }

    private static void Type(DevConsole console, string text)
    {
        foreach (var character in text)
        {
            console.Character(character);
        }
    }

    private static void Press(DevConsole console, Key key)
    {
        console.KeyDown(key, false);
        console.KeyUp(key);
    }

    private static void Enter(DevConsole console, string text)
    {
        Type(console, text);
        Press(console, Key.Enter);
    }

    private static ConsoleLine Last(DevConsole console) => console.ScrollbackLine(console.ScrollbackCount - 1);

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<(string Left, string Right, int Column, int Indent)> Answers { get; } = [];

        public void Print(string text) => Answers.Add((text, "", 0, 0));

        public void PrintColumns(string left, string right, int column, int indent) =>
            Answers.Add((left, right, column, indent));

        public void PrintError(string text) { }
    }
}
