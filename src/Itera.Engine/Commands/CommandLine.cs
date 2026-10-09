using System.Text;

namespace Itera.Engine.Commands;

/// <summary>
/// Splits a console line into commands and their words. ';' separates commands, spaces separate words, and double quotes
/// keep spaces and ';' inside one word (<c>say "a; b"</c>). A quote that is not closed runs to the end of the line.
/// Empty commands are dropped.
/// </summary>
public static class CommandLine
{
    /// <summary>The commands in <paramref name="line"/>, each as its words; the first word is the command's name.</summary>
    public static List<string[]> Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var commands = new List<string[]>();
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var quoted = false;
        foreach (var c in line)
        {
            if (quoted)
            {
                if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    word.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
                inWord = true;
            }
            else if (c == ';')
            {
                EndWord();
                EndCommand();
            }
            else if (char.IsWhiteSpace(c))
            {
                EndWord();
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }

        EndWord();
        EndCommand();
        return commands;

        void EndWord()
        {
            if (inWord)
            {
                words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
        }

        void EndCommand()
        {
            if (words.Count > 0)
            {
                commands.Add([.. words]);
                words.Clear();
            }
        }
    }
}
