namespace Itera.Engine.Settings;

/// <summary>
/// The configuration keys given on the command line (<c>--Engine:Audio:Volume=0.5</c>, <c>/Engine:Audio:Volume 0.5</c>,
/// and <c>Engine:Audio:Volume=0.5</c> all give <c>Engine:Audio:Volume</c>, as .NET's command-line configuration reads
/// them), without case: a player's setting saved in <c>settings.json</c> does not
/// replace one of them for the run, and the run does not write it back (<see cref="PlayerSettings"/>).
/// </summary>
public sealed class CommandLineKeys
{
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="args">The command line's arguments, as the host builder reads them.</param>
    public CommandLineKeys(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string[] all = [.. args];
        for (var i = 0; i < all.Length; i++)
        {
            var arg = all[i];
            var prefixed = arg.StartsWith("--", StringComparison.Ordinal) || arg.StartsWith('/');
            var key =
                arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..]
                : arg.StartsWith('/') ? arg[1..]
                : arg;
            var equals = key.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                _keys.Add(key[..equals]);
            }
            else if (prefixed)
            {
                // The next argument is its value, whatever it holds.
                _keys.Add(key);
                i++;
            }
        }
    }

    /// <summary>None: a run with no arguments, and tests.</summary>
    public static CommandLineKeys None { get; } = new([]);

    /// <summary>True when the command line gave <paramref name="key"/> (<c>Engine:Audio:Volume</c>).</summary>
    public bool Contains(string key) => _keys.Contains(key);
}
