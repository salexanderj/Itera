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
    public void The_startup_script_waits_until_it_is_not_held()
    {
        var console = Create(run: "say a");
        console.HoldStartup = true;

        console.RunPending();
        Assert.Empty(_ran);
        console.HoldStartup = false;
        console.RunPending();

        Assert.Equal(["a"], _ran);
    }

    [Fact]
    public void The_run_file_runs_after_the_run_option_without_its_comments_and_empty_lines()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(file, ["# a comment", "say c", "", "  say d; say e  "]);
            var console = Create(run: "say a; say b", runFile: file);

            console.RunPending();

            Assert.Equal(["a", "b", "c", "d", "e"], _ran);
            Assert.False(console.StartupScriptFailed);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_failed_startup_command_is_marked_and_the_script_goes_on()
    {
        var console = Create(run: "say a; nosuch; say b");

        console.RunPending();

        Assert.Equal(["a", "b"], _ran);
        Assert.True(console.StartupScriptFailed);
    }

    [Fact]
    public void With_exit_on_failure_a_failed_startup_command_ends_the_script()
    {
        var console = Create(run: "say a; fail; say b", exitOnFailure: true);
        _commands.Register("fail", "", "", CommandScope.Local, context => context.PrintError("no"));

        console.RunPending();

        Assert.Equal(["a"], _ran);
        Assert.True(console.StartupScriptFailed);
        Assert.False(console.ScriptsBusy);
    }

    [Fact]
    public void A_typed_line_that_fails_is_not_the_startup_scripts_failure()
    {
        var console = Create(exitOnFailure: true);
        console.Submit("nosuch; say a");

        console.RunPending();

        Assert.Equal(["a"], _ran);
        Assert.False(console.StartupScriptFailed);
    }

    [Fact]
    public void A_hold_keeps_its_script_but_not_the_other()
    {
        var console = Create(run: "hold; say late");
        var release = false;
        _commands.Register("hold", "", "", CommandScope.Local, _ => Assert.True(console.Hold("hold", () => release)));

        console.RunPending();
        console.Submit("say typed");
        console.RunPending();
        Assert.Equal(["typed"], _ran);

        release = true;
        console.RunPending();

        Assert.Equal(["typed", "late"], _ran);
    }

    [Fact]
    public void A_hold_that_names_a_failure_fails_its_command()
    {
        var console = Create(run: "hold; say late", exitOnFailure: true);
        _commands.Register(
            "hold",
            "",
            "",
            CommandScope.Local,
            _ => console.Hold("hold", () => true, failure: () => "it broke")
        );

        console.RunPending();
        console.RunPending();

        Assert.Empty(_ran);
        Assert.True(console.StartupScriptFailed);
        Assert.Equal("it broke", console.ScrollbackLine(console.ScrollbackCount - 1).Text);
    }

    /// <summary>What a command starts (a reset's loading screen) shows in the run only from the next iteration.</summary>
    [Fact]
    public void A_hold_already_true_is_first_checked_at_the_next_run()
    {
        var console = Create(run: "hold; say late");
        _commands.Register("hold", "", "", CommandScope.Local, _ => console.Hold("hold", () => true));

        console.RunPending();
        Assert.Empty(_ran);
        console.RunPending();

        Assert.Equal(["late"], _ran);
    }

    [Fact]
    public void Each_typed_line_is_a_script_of_its_own()
    {
        var console = Create();
        _commands.Register("hold", "", "", CommandScope.Local, _ => console.Hold("hold", () => false));
        console.Submit("hold; say held");
        console.Submit("say free");

        console.RunPending();
        console.RunPending();

        Assert.Equal(["free"], _ran);
        Assert.True(console.ScriptsBusy);
    }

    [Fact]
    public void A_startup_hold_past_its_time_fails_the_script_and_with_exit_on_failure_stops_it()
    {
        var console = Create(run: "hold; say late", exitOnFailure: true);
        var gaveUp = false;
        _commands.Register(
            "hold",
            "",
            "",
            CommandScope.Local,
            _ => console.Hold("hold", () => false, seconds: 5, giveUp: () => gaveUp = true)
        );

        console.RunPending(new ConsoleMoment(0, 0, 10, true));
        console.RunPending(new ConsoleMoment(0, 0, 14, true));
        Assert.False(console.StartupScriptFailed);
        console.RunPending(new ConsoleMoment(0, 0, 15, true));

        Assert.True(console.StartupScriptFailed);
        Assert.True(gaveUp);
        Assert.False(console.ScriptsBusy);
        Assert.Empty(_ran);
    }

    [Fact]
    public void A_hold_that_throws_fails_its_script_without_breaking_the_console()
    {
        var console = Create(run: "hold; say late");
        _commands.Register(
            "hold",
            "",
            "",
            CommandScope.Local,
            _ => console.Hold("hold", () => throw new InvalidOperationException("broken"))
        );

        console.RunPending();
        console.RunPending();

        Assert.True(console.StartupScriptFailed);
        Assert.Empty(_ran);
        Assert.StartsWith(
            "hold failed: broken",
            console.ScrollbackLine(console.ScrollbackCount - 1).Text,
            StringComparison.Ordinal
        );
        Assert.False(console.ScriptsBusy);
    }

    [Fact]
    public void A_hold_given_no_finite_time_lasts_the_default()
    {
        var console = Create(run: "forever; say never");
        _commands.Register(
            "forever",
            "",
            "",
            CommandScope.Local,
            _ => console.Hold("forever", () => false, double.NaN)
        );

        console.RunPending(new ConsoleMoment(0, 0, 0, true));
        console.RunPending(new ConsoleMoment(0, 0, DevConsole.DefaultHoldSeconds - 1, true));
        Assert.True(console.ScriptsBusy);
        console.RunPending(new ConsoleMoment(0, 0, DevConsole.DefaultHoldSeconds, true));

        Assert.False(console.ScriptsBusy);
        Assert.True(console.StartupScriptFailed);
        Assert.Empty(_ran);
    }

    [Fact]
    public void A_script_run_for_the_control_port_holds_and_copies_its_answers()
    {
        var console = Create();
        var release = false;
        _commands.Register("hold", "", "", CommandScope.Local, _ => console.Hold("hold", () => release));
        var copy = new Copy();

        var script = console.RunScript("say a; hold; say b", copy);
        console.RunPending();
        Assert.False(script.Done);
        release = true;
        console.RunPending();

        Assert.True(script.Done);
        Assert.Equal(0, script.Failures);
        Assert.Equal(["a", "b"], copy.Answers);
        Assert.False(console.ScriptsBusy);
    }

    [Fact]
    public void A_stopped_script_takes_back_its_hold_and_drops_the_rest()
    {
        var console = Create();
        var gaveUp = false;
        _commands.Register(
            "hold",
            "",
            "",
            CommandScope.Local,
            _ => console.Hold("hold", () => false, giveUp: () => gaveUp = true)
        );

        var script = console.RunScript("hold; say b", new Copy());
        script.Stop();
        console.RunPending();

        Assert.True(script.Done);
        Assert.True(gaveUp);
        Assert.Empty(_ran);
    }

    /// <summary>A second output, as the control port's reply is.</summary>
    private sealed class Copy : IConsoleOutput
    {
        public List<string> Answers { get; } = [];

        public void Print(string text) => Answers.Add(text);

        public void PrintColumns(string left, string right, int column, int indent) => Answers.Add(left);

        public void PrintError(string text) => Answers.Add("error: " + text);
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

    private DevConsole Create(
        string? run = null,
        bool enabled = true,
        int scrollback = 1000,
        string? runFile = null,
        bool exitOnFailure = false
    )
    {
        var options = new EngineOptions();
        options.Console.Run = run;
        options.Console.RunFile = runFile;
        options.Console.ExitOnFailure = exitOnFailure;
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
