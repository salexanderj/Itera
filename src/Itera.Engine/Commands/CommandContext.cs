using System.Globalization;

namespace Itera.Engine.Commands;

/// <summary>A console command as registered: what <see cref="ConsoleCommands"/> shows in help and runs.</summary>
/// <param name="Name">The word that runs it; compared without case.</param>
/// <param name="Usage">Its arguments, for example <c>[on|off]</c>; empty when it takes none.</param>
/// <param name="Help">One line saying what it does.</param>
/// <param name="Scope">Where it may run, by the session role.</param>
/// <param name="Owner">Who registered it: <c>engine</c>, the game, or later a mod's id.</param>
/// <param name="Run">Runs it. Main thread, between fixed steps.</param>
public sealed record ConsoleCommand(
    string Name,
    string Usage,
    string Help,
    CommandScope Scope,
    string Owner,
    Action<CommandContext> Run
);

/// <summary>One run of a command: its arguments and where its answer goes.</summary>
public sealed class CommandContext
{
    private readonly string[] _words;
    private readonly IConsoleOutput _output;

    internal CommandContext(ConsoleCommand command, string[] words, IConsoleOutput output)
    {
        Command = command;
        _words = words;
        _output = output;
    }

    /// <summary>The command being run.</summary>
    public ConsoleCommand Command { get; }

    /// <summary>Number of arguments after the command's name.</summary>
    public int Count => _words.Length - 1;

    /// <summary>Argument <paramref name="index"/>, counted from 0 after the command's name.</summary>
    public string this[int index] => _words[index + 1];

    /// <summary>Adds a line of answer to the console.</summary>
    public void Print(string text) => _output.Print(text);

    /// <summary>
    /// Adds a line of answer in two columns, <paramref name="right"/> from character <paramref name="column"/>; the
    /// console wraps each within its own column, the first's rows after the first starting <paramref name="indent"/>
    /// characters in.
    /// </summary>
    public void PrintColumns(string left, string right, int column, int indent) =>
        _output.PrintColumns(left, right, column, indent);

    /// <summary>
    /// A line of two columns as one line of text: <paramref name="right"/> from character <paramref name="column"/>, or
    /// two spaces after a longer <paramref name="left"/>.
    /// </summary>
    public static string Columns(string left, string right, int column) =>
        left.PadRight(Math.Max(0, column - 2)) + "  " + right;

    /// <summary>Adds a line saying what went wrong to the console.</summary>
    public void PrintError(string text) => _output.PrintError(text);

    /// <summary>Says how the command is used, as an error.</summary>
    public void PrintUsage() =>
        _output.PrintError("usage: " + (Command.Usage.Length == 0 ? Command.Name : Command.Name + " " + Command.Usage));

    /// <summary>True when argument <paramref name="index"/> is exactly <paramref name="word"/>, without case.</summary>
    public bool Is(int index, string word) =>
        index < Count && string.Equals(this[index], word, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads argument <paramref name="index"/> as a number with '.' for the decimal point.</summary>
    public bool TryGetFloat(int index, out float value)
    {
        value = 0;
        return index < Count
            && float.TryParse(this[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && float.IsFinite(value);
    }

    /// <summary>Reads argument <paramref name="index"/> as a whole number.</summary>
    public bool TryGetInt(int index, out int value)
    {
        value = 0;
        return index < Count
            && int.TryParse(this[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Reads argument <paramref name="index"/> as on or off (<c>on</c>, <c>off</c>, <c>1</c>, <c>0</c>, <c>true</c>, <c>false</c>).</summary>
    public bool TryGetOnOff(int index, out bool value)
    {
        value = false;
        if (index >= Count)
        {
            return false;
        }

        switch (this[index].ToUpperInvariant())
        {
            case "ON":
            case "1":
            case "TRUE":
                value = true;
                return true;
            case "OFF":
            case "0":
            case "FALSE":
                return true;
            default:
                return false;
        }
    }

    /// <summary>"on" or "off".</summary>
    public static string OnOff(bool value) => value ? "on" : "off";

    /// <summary>A number as the console prints it: '.' for the decimal point, at most three decimals.</summary>
    public static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
