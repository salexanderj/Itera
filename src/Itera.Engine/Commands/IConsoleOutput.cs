namespace Itera.Engine.Commands;

/// <summary>Where a command's answer goes: the console's scrollback.</summary>
public interface IConsoleOutput
{
    /// <summary>Adds a line of answer.</summary>
    void Print(string text);

    /// <summary>
    /// Adds a line of answer in two columns: <paramref name="left"/>, and <paramref name="right"/> from character
    /// <paramref name="column"/> (at least 2). The console wraps each within its own column, the first's rows after
    /// the first starting <paramref name="indent"/> characters in; an output that does not wrap gets the line as
    /// <see cref="CommandContext.Columns"/> writes it.
    /// </summary>
    void PrintColumns(string left, string right, int column, int indent) =>
        Print(CommandContext.Columns(left, right, column));

    /// <summary>Adds a line saying what went wrong.</summary>
    void PrintError(string text);
}
