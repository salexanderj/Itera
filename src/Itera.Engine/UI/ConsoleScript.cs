using Itera.Engine.Commands;

namespace Itera.Engine.UI;

/// <summary>
/// Console commands run in order by <see cref="DevConsole"/>: a typed line, the startup commands, or a line the control
/// port sent as a script. A command may hold it (<see cref="DevConsole.Hold"/>) until a condition holds; the commands
/// after it wait. Main thread.
/// </summary>
public sealed class ConsoleScript
{
    internal ConsoleScript(bool startup, IConsoleOutput? copy)
    {
        Startup = startup;
        Copy = copy;
    }

    /// <summary>True once every command has run and nothing holds it.</summary>
    public bool Done => Until is null && Commands.Count == 0;

    /// <summary>Its commands and holds that failed so far.</summary>
    public int Failures { get; internal set; }

    /// <summary>True for the startup commands, whose failures count (<see cref="DevConsole.StartupScriptFailed"/>).</summary>
    internal bool Startup { get; }

    /// <summary>Where its answers go besides the scrollback (the control port's reply), or null.</summary>
    internal IConsoleOutput? Copy { get; }

    internal Queue<string[]> Commands { get; } = new();

    /// <summary>What holds it; null while nothing does.</summary>
    internal Func<bool>? Until { get; set; }

    /// <summary>Once <see cref="Until"/> holds: what went wrong, or null.</summary>
    internal Func<string?>? Failure { get; set; }

    /// <summary>Called when the hold ends without its condition (out of time, or <see cref="Stop"/>).</summary>
    internal Action? GiveUp { get; set; }

    /// <summary>True until the hold's first check, which waits for the next run of the console.</summary>
    internal bool FreshHold { get; set; }

    internal string Holding { get; set; } = "";

    internal double HoldSeconds { get; set; }

    /// <summary>The game clock's seconds at which the hold fails.</summary>
    internal double Deadline { get; set; }

    /// <summary>Drops the hold, taking back what its command asked for, and every command left.</summary>
    public void Stop()
    {
        Commands.Clear();
        TakeBack();
        EndHold();
    }

    /// <summary>Adds a line's commands (<see cref="CommandLine"/>).</summary>
    internal void Add(string line)
    {
        foreach (var words in CommandLine.Parse(line))
        {
            Commands.Enqueue(words);
        }
    }

    /// <summary>Calls <see cref="GiveUp"/> once, if set.</summary>
    internal void TakeBack()
    {
        var giveUp = GiveUp;
        GiveUp = null;
        giveUp?.Invoke();
    }

    internal void EndHold()
    {
        Until = null;
        Failure = null;
        GiveUp = null;
        FreshHold = false;
    }
}
