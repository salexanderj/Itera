using System.Text;
using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Itera.Engine.UI;

/// <summary>What a line in the console's scrollback is, which picks its colour.</summary>
public enum ConsoleLineKind
{
    /// <summary>A log line of Information (a command's echo among them).</summary>
    Log = 0,

    /// <summary>A log line of Warning.</summary>
    Warning = 1,

    /// <summary>A log line of Error or Critical.</summary>
    Error = 2,

    /// <summary>A command's answer.</summary>
    Answer = 3,

    /// <summary>What went wrong with a command.</summary>
    Failure = 4,
}

/// <summary>One line of the console's scrollback.</summary>
/// <param name="Kind">What the line is, which picks its colour.</param>
/// <param name="Text">The line, or its first column when <paramref name="Column"/> is above 0.</param>
/// <param name="Right">The second column's text; empty for a line of one column.</param>
/// <param name="Column">The character the second column starts at; 0 for a line of one column.</param>
/// <param name="Indent">Characters the first column's rows after the first start in by when it wraps.</param>
public readonly record struct ConsoleLine(
    ConsoleLineKind Kind,
    string Text,
    string Right = "",
    int Column = 0,
    int Indent = 0
);

/// <summary>Where the run is, which the console's waits (<c>wait</c>) read: given each <see cref="DevConsole.RunPending(in ConsoleMoment)"/>.</summary>
/// <param name="Steps">Fixed steps run since the run began, over every match.</param>
/// <param name="Frames">Frames built since the run began.</param>
/// <param name="Seconds">The game clock, in seconds.</param>
/// <param name="Loaded">True while a match is played and its loading screen has gone.</param>
public readonly record struct ConsoleMoment(ulong Steps, ulong Frames, double Seconds, bool Loaded);

/// <summary>
/// The in-game console's state: open or closed, the line being typed and its cursor, the history of lines run, the
/// scrollback (log lines of Information and above from <see cref="ConsoleLogBuffer"/>, and commands' answers), and the
/// lines waiting to run. <see cref="InputService"/> sends it keys while it is open (it is the
/// <see cref="IKeyboardFocus"/>); <see cref="ConsoleLayer"/> draws it. Enter queues the line, and
/// <see cref="RunPending(in ConsoleMoment)"/> runs it through <see cref="ConsoleCommands"/> at a fixed point of the main
/// loop, between fixed steps. Editing: left and right, Home and End, Backspace and Delete (held keys repeat), Up and Down
/// through the history, Tab to complete a command's name, Page Up and Page Down or the wheel to scroll, Ctrl+V to paste.
/// Main thread.
/// <para>
/// Commands run as scripts (<see cref="ConsoleScript"/>): each line typed is one, the startup commands
/// (<c>Engine:Console:Run</c>, then the lines of <c>Engine:Console:RunFile</c>) another, and a line the control port
/// sends as a script another (<see cref="RunScript"/>). A command can hold its script (<see cref="Hold"/>: <c>wait</c>,
/// and <c>screenshot</c> until its PNG is written), and the commands after it run once the hold ends, in a later
/// <see cref="RunPending(in ConsoleMoment)"/>; a hold that runs past its time fails and drops the rest of its script.
/// </para>
/// </summary>
public sealed partial class DevConsole : IKeyboardFocus, IConsoleOutput
{
    /// <summary>Seconds a hold lasts at most unless its command gives another (<c>wait ... TIMEOUT</c>).</summary>
    public const double DefaultHoldSeconds = 300;

    /// <summary>Longest a command may hold its script, in seconds.</summary>
    public const double MostHoldSeconds = 3600;

    /// <summary>Seconds a held key waits before it repeats.</summary>
    public const double RepeatDelaySeconds = 0.4;

    /// <summary>Repeats per second of a held key.</summary>
    public const double RepeatsPerSecond = 30;

    /// <summary>Lines of history kept.</summary>
    public const int HistoryLength = 100;

    /// <summary>Longest line that can be typed.</summary>
    public const int MaxLineLength = 1000;

    private readonly ConsoleCommands _commands;
    private readonly ConsoleLogBuffer _log;
    private readonly ILogger<DevConsole> _logger;
    private readonly ConsoleLine[] _scrollback;
    private readonly List<string> _history = [];
    private readonly Queue<string> _pending = new();
    private readonly TextLine _line = new(MaxLineLength);
    private readonly Action<ConsoleLogLine> _takeLogLine;
    private readonly string? _startupRun;
    private readonly string? _startupFile;
    private readonly bool _exitOnFailure;
    private readonly List<ConsoleScript> _scripts = [];
    private bool _startupWaiting;
    private ConsoleScript? _startupScript;
    private ConsoleScript? _running;
    private ConsoleMoment _moment;
    private int _scrollbackStart;
    private int _scrollbackCount;
    private int _historyIndex = -1;
    private string _draft = "";
    private int _scroll;

    /// <summary>The wheel's turns, three lines a step, with parts of a line kept.</summary>
    private WheelSteps _wheel;
    private Key _repeatKey = Key.Unknown;
    private bool _repeatControl;
    private double _repeatAt;
    private double _now;

    public DevConsole(
        IOptions<EngineOptions> options,
        ConsoleCommands commands,
        ConsoleLogBuffer log,
        ILogger<DevConsole> logger
    )
    {
        _logger = logger;
        var settings = options.Value.Console;
        Enabled = settings.Enabled;
        _startupRun = string.IsNullOrWhiteSpace(settings.Run) ? null : settings.Run;
        _startupFile = string.IsNullOrWhiteSpace(settings.RunFile) ? null : settings.RunFile;
        _startupWaiting = _startupRun is not null || _startupFile is not null;
        _exitOnFailure = settings.ExitOnFailure;
        _commands = commands;
        _log = log;
        _scrollback = new ConsoleLine[settings.ScrollbackLines];
        _takeLogLine = TakeLogLine;
    }

    public bool Enabled { get; }

    public bool IsOpen { get; private set; }

    /// <summary>Rises whenever anything drawn changes.</summary>
    public int Version { get; private set; }

    /// <summary>The line being typed.</summary>
    public string Line => _line.Text;

    /// <summary>Where the cursor is in <see cref="Line"/>, from 0 (before the first character) to its length.</summary>
    public int Cursor => _line.Cursor;

    /// <summary>Lines in the scrollback.</summary>
    public int ScrollbackCount => _scrollbackCount;

    /// <summary>How many lines up from the newest the view is scrolled.</summary>
    public int ScrollOffset => _scroll;

    /// <summary>When a key was last typed, in <see cref="Update"/>'s seconds, so the cursor shows while typing.</summary>
    public double LastTyped { get; private set; }

    /// <summary>Line <paramref name="index"/> of the scrollback, 0 the oldest.</summary>
    public ConsoleLine ScrollbackLine(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _scrollbackCount);
        return _scrollback[(_scrollbackStart + index) % _scrollback.Length];
    }

    public void Open()
    {
        if (!IsOpen)
        {
            IsOpen = true;
            _scroll = 0;
            LastTyped = _now;
            Changed();
        }
    }

    public void Close()
    {
        if (IsOpen)
        {
            IsOpen = false;
            _repeatKey = Key.Unknown;
            Changed();
        }
    }

    /// <summary>A key went down; false always, as the console repeats held keys itself (<see cref="Update"/>).</summary>
    public bool KeyDown(Key key, bool control)
    {
        if (key == _repeatKey)
        {
            // A repeat from the platform; the console repeats held keys itself.
            return false;
        }

        if (Apply(key, control))
        {
            _repeatKey = key;
            _repeatControl = control;
            _repeatAt = _now + RepeatDelaySeconds;
        }

        return false;
    }

    public void KeyUp(Key key)
    {
        if (key == _repeatKey)
        {
            _repeatKey = Key.Unknown;
        }
    }

    public void Character(Rune character)
    {
        if (_line.Insert(character))
        {
            Typed();
        }
    }

    public void Paste(string text)
    {
        var version = _line.Version;
        _line.Paste(text);
        if (_line.Version != version)
        {
            Typed();
        }
    }

    public void Scroll(float steps)
    {
        ScrollBy(_wheel.Take(steps * 3));
    }

    public void ReleaseKeys()
    {
        _repeatKey = Key.Unknown;
    }

    /// <summary>Repeats a held key when it is due. Once per frame, with the frame's clock in seconds.</summary>
    public void Update(double now)
    {
        _now = now;
        if (_repeatKey == Key.Unknown || !IsOpen || now < _repeatAt)
        {
            return;
        }

        // A long frame repeats a few times, not a burst of every repeat it missed.
        var repeats = 0;
        while (now >= _repeatAt && repeats < 4)
        {
            Apply(_repeatKey, _repeatControl);
            _repeatAt += 1 / RepeatsPerSecond;
            repeats++;
        }

        if (now >= _repeatAt)
        {
            _repeatAt = now + 1 / RepeatsPerSecond;
        }
    }

    /// <summary>
    /// True while the startup commands (<c>Engine:Console:Run</c>) wait: until the first match's world and systems
    /// exist, since most of them are the match's.
    /// </summary>
    public bool HoldStartup { get; set; }

    /// <summary>Where the run was at the last <see cref="RunPending(in ConsoleMoment)"/>, which holds read.</summary>
    public ConsoleMoment Now => _moment;

    /// <summary>
    /// True once a command of the startup script failed (unknown, refused, threw, or printed a failure) or a hold of it
    /// ran out of time. With <c>Engine:Console:ExitOnFailure</c> the script stops there and the loop ends the run.
    /// </summary>
    public bool StartupScriptFailed { get; private set; }

    /// <summary>True while a script has commands left to run or is held.</summary>
    public bool ScriptsBusy => _scripts.Count > 0 || (_startupScript is { Done: false });

    /// <summary>
    /// Moves new log lines into the scrollback, starts the startup script (<c>Engine:Console:Run</c>, then
    /// <c>Engine:Console:RunFile</c>) the first time it is not held (<see cref="HoldStartup"/>), makes each line typed
    /// since the last call a script of its own, and runs each script's commands until one holds it or none are left.
    /// The startup script goes on across matches once started. Main loop, between fixed steps.
    /// </summary>
    public void RunPending(in ConsoleMoment now)
    {
        _moment = now;
        DrainLog();
        if (Enabled)
        {
            if (_startupWaiting && !HoldStartup)
            {
                _startupWaiting = false;
                _startupScript = StartupScript();
            }

            while (_pending.TryDequeue(out var line))
            {
                var typed = new ConsoleScript(startup: false, copy: null);
                typed.Add(line);
                _scripts.Add(typed);
            }

            if (_startupScript is { } startup)
            {
                Advance(startup);
            }
        }
        else
        {
            _pending.Clear();
        }

        // The control port's scripts run also when the console is disabled, as its other lines do.
        for (var i = 0; i < _scripts.Count; i++)
        {
            Advance(_scripts[i]);
        }

        _scripts.RemoveAll(static script => script.Done);
    }

    /// <summary><see cref="RunPending(in ConsoleMoment)"/> at the moment it last had (tests).</summary>
    public void RunPending() => RunPending(in _moment);

    /// <summary>
    /// Starts <paramref name="line"/> as a script of its own (the control port's <c>run</c> with <c>script</c>): its
    /// commands run now, until one holds it, and the rest at the next runs of the console; each answer goes to the
    /// scrollback and to <paramref name="copy"/>. Runs also when the console is disabled. Main loop, between fixed steps.
    /// </summary>
    public ConsoleScript RunScript(string line, IConsoleOutput copy)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(copy);
        var script = new ConsoleScript(startup: false, copy);
        script.Add(line);
        Advance(script);
        if (!script.Done)
        {
            _scripts.Add(script);
        }

        return script;
    }

    /// <summary>
    /// Holds the script whose command runs now until <paramref name="done"/> is true, first checked at the next
    /// <see cref="RunPending(in ConsoleMoment)"/> (what a command starts, such as a reset's loading screen, shows in the
    /// run's state only from the next loop iteration); then <paramref name="failure"/>, when given, may name what went
    /// wrong, which counts as the command's failure. Past <paramref name="seconds"/> of the game clock the hold fails and
    /// the rest of the script is dropped. False when no script runs the command (the control port's plain <c>run</c>),
    /// which then goes on at once.
    /// </summary>
    /// <param name="what">The hold's name in its failure, for example "wait loaded".</param>
    /// <param name="done">True once the hold may end.</param>
    /// <param name="seconds">How long it may last, at most <see cref="MostHoldSeconds"/>.</param>
    /// <param name="failure">Once done: what went wrong, or null.</param>
    /// <param name="giveUp">Called when the hold ends without its condition, so its command can take back what it asked for.</param>
    public bool Hold(
        string what,
        Func<bool> done,
        double seconds = DefaultHoldSeconds,
        Func<string?>? failure = null,
        Action? giveUp = null
    )
    {
        ArgumentNullException.ThrowIfNull(what);
        ArgumentNullException.ThrowIfNull(done);
        if (_running is not { } script)
        {
            return false;
        }

        script.Until = done;
        script.Failure = failure;
        script.GiveUp = giveUp;
        script.FreshHold = true;
        script.Holding = what;
        script.HoldSeconds = double.IsFinite(seconds) ? Math.Clamp(seconds, 0, MostHoldSeconds) : DefaultHoldSeconds;
        script.Deadline = _moment.Seconds + script.HoldSeconds;
        return true;
    }

    /// <summary>The startup commands as a script: <c>Engine:Console:Run</c>'s, then the lines of its file.</summary>
    private ConsoleScript StartupScript()
    {
        var script = new ConsoleScript(startup: true, copy: null);
        if (_startupRun is { } run)
        {
            script.Add(run);
        }

        if (_startupFile is { } file)
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    var text = line.Trim();
                    if (text.Length > 0 && text[0] != '#')
                    {
                        script.Add(text);
                    }
                }
            }
            catch (Exception ex)
            {
                // Any reason it cannot be read (gone, a folder, no access, a bad path) fails the script, not the loop.
                Report(script, $"Console:RunFile {file} could not be read: {ex.Message}");
                Failed(script, "reading " + file);
            }
        }

        return script;
    }

    /// <summary>Runs <paramref name="script"/>'s commands until one holds it or none are left.</summary>
    private void Advance(ConsoleScript script)
    {
        while (true)
        {
            if (script.Until is { } until)
            {
                if (script.FreshHold)
                {
                    script.FreshHold = false;
                    return;
                }

                if (!Ended(script, until))
                {
                    return;
                }
            }

            if (!script.Commands.TryDequeue(out var words))
            {
                return;
            }

            var output = new CountedOutput(this, script.Copy);
            _running = script;
            bool ran;
            try
            {
                ran = _commands.Run(words, output);
            }
            finally
            {
                _running = null;
            }

            if (!ran || output.Failures > 0)
            {
                script.Failures++;
                if (Failed(script, string.Join(' ', words)))
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Checks <paramref name="script"/>'s hold: true when it ended and the script goes on; false while it holds, or when
    /// it failed past its time or threw, which stops the script (a failure).
    /// </summary>
    private bool Ended(ConsoleScript script, Func<bool> until)
    {
        var held = script.Holding;
        bool done;
        try
        {
            done = until();
        }
        catch (Exception ex)
        {
            Report(script, $"{held} failed: {ex.Message}; the commands after it were dropped.");
            Stop(script, held);
            return false;
        }

        if (!done)
        {
            if (_moment.Seconds >= script.Deadline)
            {
                // Taken back first, so the reason it gives (a withdrawn screenshot's) is the one reported.
                script.TakeBack();
                var reason = FailureOf(script);
                var why = reason is null ? "" : " (" + reason + ")";
                Report(
                    script,
                    $"{held}: not done after {CommandContext.Number(script.HoldSeconds)} s{why}; the commands after it were dropped."
                );
                Stop(script, held);
            }

            return false;
        }

        var failure = FailureOf(script);
        script.EndHold();
        if (failure is null)
        {
            return true;
        }

        Report(script, failure);
        return !Failed(script, held);
    }

    /// <summary>What the hold's command says went wrong, or null; a throw is what went wrong.</summary>
    private static string? FailureOf(ConsoleScript script)
    {
        try
        {
            return script.Failure?.Invoke();
        }
        catch (Exception ex)
        {
            return script.Holding + " failed: " + ex.Message;
        }
    }

    /// <summary>A failure of the script itself (not of a command): to the scrollback and the script's copy, counted.</summary>
    private void Report(ConsoleScript script, string text)
    {
        script.Failures++;
        PrintError(text);
        script.Copy?.PrintError(text);
    }

    /// <summary>Stops <paramref name="script"/> after a hold failed, and counts it as a failure of the startup script.</summary>
    private void Stop(ConsoleScript script, string what)
    {
        StopQuietly(script, what);
        Failed(script, what);
    }

    /// <summary>Stops <paramref name="script"/>; a hold's take-back that throws is reported, never left to end the loop.</summary>
    private void StopQuietly(ConsoleScript script, string what)
    {
        try
        {
            script.Stop();
        }
        catch (Exception ex)
        {
            PrintError($"{what}: taking it back failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A command or hold of <paramref name="script"/> failed. The startup script's failures are marked and logged, and
    /// with <c>Engine:Console:ExitOnFailure</c> the script stops; returns true when it stopped.
    /// </summary>
    private bool Failed(ConsoleScript script, string what)
    {
        if (!script.Startup)
        {
            return false;
        }

        StartupScriptFailed = true;
        LogStartupFailed(what, _exitOnFailure);
        if (!_exitOnFailure)
        {
            return false;
        }

        StopQuietly(script, what);
        return true;
    }

    /// <summary>
    /// Runs <paramref name="line"/> now, as if typed, with each answer both in the scrollback and given to
    /// <paramref name="copy"/> (the control port's reply). Runs also when the console is disabled. Its commands run at
    /// once, outside any script, so none of them holds (<see cref="Hold"/>): the port has waits of its own. Main loop,
    /// between fixed steps.
    /// </summary>
    public void RunNow(string line, IConsoleOutput copy)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(copy);
        _commands.Execute(line, new CopiedOutput(this, copy));
    }

    /// <summary>Queues <paramref name="line"/> to run at the next <see cref="RunPending(in ConsoleMoment)"/>, as if typed.</summary>
    public void Submit(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _pending.Enqueue(line);
    }

    /// <summary>Empties the scrollback.</summary>
    public void Clear()
    {
        DrainLog();
        Array.Clear(_scrollback);
        _scrollbackStart = 0;
        _scrollbackCount = 0;
        _scroll = 0;
        Changed();
    }

    public void Print(string text)
    {
        LogAnswer(text);
        Add(ConsoleLineKind.Answer, text);
    }

    public void PrintColumns(string left, string right, int column, int indent)
    {
        var text = CommandContext.Columns(left, right, column);
        LogAnswer(text);
        DrainLog();
        Store(new ConsoleLine(ConsoleLineKind.Answer, left, right, Math.Max(2, column), Math.Max(0, indent)));
    }

    public void PrintError(string text)
    {
        LogFailure(text);
        Add(ConsoleLineKind.Failure, text);
    }

    private void Add(ConsoleLineKind kind, string text)
    {
        // Log lines that came first (the command's echo) go in first.
        DrainLog();
        Append(kind, text);
    }

    private void DrainLog() => _log.Drain(_takeLogLine);

    private void TakeLogLine(ConsoleLogLine line)
    {
        var kind = line.Level switch
        {
            >= LogLevel.Error => ConsoleLineKind.Error,
            LogLevel.Warning => ConsoleLineKind.Warning,
            _ => ConsoleLineKind.Log,
        };
        Append(kind, line.Text);
    }

    private void Append(ConsoleLineKind kind, string text)
    {
        foreach (var part in text.Split('\n'))
        {
            Store(new ConsoleLine(kind, part.TrimEnd('\r')));
        }
    }

    private void Store(in ConsoleLine line)
    {
        var index = (_scrollbackStart + _scrollbackCount) % _scrollback.Length;
        _scrollback[index] = line;
        if (_scrollbackCount < _scrollback.Length)
        {
            _scrollbackCount++;
        }
        else
        {
            _scrollbackStart = (_scrollbackStart + 1) % _scrollback.Length;
        }

        // A view scrolled up stays on the lines it shows.
        if (_scroll > 0)
        {
            _scroll = Math.Min(_scroll + 1, Math.Max(0, _scrollbackCount - 1));
        }

        Changed();
    }

    /// <summary>Does what <paramref name="key"/> does; true when holding it should repeat that.</summary>
    private bool Apply(Key key, bool control)
    {
        switch (key)
        {
            case Key.Enter:
            case Key.KeypadEnter:
                Enter();
                return false;
            case Key.Up:
                History(+1);
                return true;
            case Key.Down:
                History(-1);
                return true;
            case Key.PageUp:
                ScrollBy(10);
                return true;
            case Key.PageDown:
                ScrollBy(-10);
                return true;
            case Key.Tab:
                Complete();
                return false;
            default:
                var version = _line.Version;
                var edited = _line.Edit(key, control, out var repeats);
                if (_line.Version != version)
                {
                    Typed();
                }

                return edited && repeats;
        }
    }

    private void Enter()
    {
        var line = _line.Text;
        SetLine("");
        _historyIndex = -1;
        _scroll = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (_history.Count == 0 || _history[^1] != line)
        {
            _history.Add(line);
            if (_history.Count > HistoryLength)
            {
                _history.RemoveAt(0);
            }
        }

        Submit(line);
    }

    private void History(int step)
    {
        if (_history.Count == 0)
        {
            return;
        }

        if (_historyIndex < 0)
        {
            if (step < 0)
            {
                return;
            }

            _draft = _line.Text;
        }

        var index = Math.Clamp(_historyIndex + step, -1, _history.Count - 1);
        if (index == _historyIndex)
        {
            return;
        }

        _historyIndex = index;
        SetLine(index < 0 ? _draft : _history[_history.Count - 1 - index]);
    }

    private void Complete()
    {
        var text = _line.Text;
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        if (space >= 0 && space < _line.Cursor)
        {
            return;
        }

        var prefix = space < 0 ? text : text[..space];
        var matches = _commands.Complete(prefix).ToList();
        if (matches.Count == 0)
        {
            return;
        }

        if (matches.Count == 1)
        {
            var rest = space < 0 ? " " : text[space..];
            SetLine(matches[0] + rest, matches[0].Length + 1);
            return;
        }

        var common = matches[0];
        foreach (var match in matches)
        {
            var length = 0;
            while (
                length < common.Length
                && length < match.Length
                && char.ToUpperInvariant(common[length]) == char.ToUpperInvariant(match[length])
            )
            {
                length++;
            }

            common = common[..length];
        }

        Append(ConsoleLineKind.Answer, string.Join("  ", matches));
        if (common.Length > prefix.Length)
        {
            SetLine(common + (space < 0 ? "" : text[space..]), common.Length);
        }
    }

    private void SetLine(string text, int cursor = -1)
    {
        _line.Set(text, cursor);
        Typed();
    }

    private void ScrollBy(int lines)
    {
        var scroll = Math.Clamp(_scroll + lines, 0, Math.Max(0, _scrollbackCount - 1));
        if (scroll != _scroll)
        {
            _scroll = scroll;
            Changed();
        }
    }

    private void Typed()
    {
        LastTyped = _now;
        Changed();
    }

    private void Changed() => Version++;

    [LoggerMessage(Level = LogLevel.Debug, Message = "Console answer: {Text}")]
    private partial void LogAnswer(string text);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Console failure: {Text}")]
    private partial void LogFailure(string text);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Console: the startup script's '{What}' failed (the script stops there: {Stops})"
    )]
    private partial void LogStartupFailed(string what, bool stops);

    /// <summary>Answers written to the scrollback and to a script's copy, with the failures counted.</summary>
    private sealed class CountedOutput(DevConsole console, IConsoleOutput? copy) : IConsoleOutput
    {
        public int Failures { get; private set; }

        public void Print(string text)
        {
            console.Print(text);
            copy?.Print(text);
        }

        public void PrintColumns(string left, string right, int column, int indent)
        {
            console.PrintColumns(left, right, column, indent);
            copy?.PrintColumns(left, right, column, indent);
        }

        public void PrintError(string text)
        {
            Failures++;
            console.PrintError(text);
            copy?.PrintError(text);
        }
    }

    /// <summary>Answers written to the scrollback and to a second output.</summary>
    private sealed class CopiedOutput(DevConsole console, IConsoleOutput copy) : IConsoleOutput
    {
        public void Print(string text)
        {
            console.Print(text);
            copy.Print(text);
        }

        public void PrintColumns(string left, string right, int column, int indent)
        {
            console.PrintColumns(left, right, column, indent);
            copy.PrintColumns(left, right, column, indent);
        }

        public void PrintError(string text)
        {
            console.PrintError(text);
            copy.PrintError(text);
        }
    }
}
