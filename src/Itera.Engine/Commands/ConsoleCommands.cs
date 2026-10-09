using System.Text;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Commands;

/// <summary>
/// The console's commands: the engine's, the game's, and later mods'. Each has a name, a usage line, one line of help,
/// a scope (<see cref="CommandScope"/>), which this checks against the session role before running it, and an owner, so
/// every command one owner registered can go at once. Lines run through <see cref="Execute"/>; each command run is
/// logged at Information ("Console: ..."), which is also its echo in the console's scrollback. A command that throws
/// is reported and logged, and the game goes on. Main thread only.
/// </summary>
public sealed partial class ConsoleCommands
{
    private readonly SortedDictionary<string, ConsoleCommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The owner of the commands a match's layers and systems register (<see cref="BeginMatch"/>).</summary>
    public const string MatchOwner = "match";

    private const string EngineOwner = "engine";

    private readonly Func<SessionRole> _role;
    private readonly ILogger<ConsoleCommands> _logger;
    private readonly List<string> _matchCommands = [];
    private string _defaultOwner = EngineOwner;
    private bool _inMatch;

    public ConsoleCommands(MatchControl matches, ILogger<ConsoleCommands> logger)
        : this(() => matches.Role, logger) { }

    /// <param name="role">The session role now; commands' scopes are checked against it.</param>
    /// <param name="logger">Where runs and failures are logged.</param>
    internal ConsoleCommands(Func<SessionRole> role, ILogger<ConsoleCommands> logger)
    {
        _role = role;
        _logger = logger;
    }

    /// <summary>Every command, by name.</summary>
    public IEnumerable<ConsoleCommand> All => _commands.Values;

    /// <summary>
    /// Adds a command, owned by <paramref name="owner"/>, or when it is null by the match while one starts or plays
    /// (<see cref="BeginMatch"/>) and by the engine otherwise. A command added while a match starts or plays, whatever
    /// its owner, goes when the match ends (<see cref="EndMatch"/>): it may hold the match's services. Throws when another
    /// command has the name.
    /// </summary>
    public void Register(
        string name,
        string usage,
        string help,
        CommandScope scope,
        Action<CommandContext> run,
        string? owner = null
    )
    {
        owner ??= _defaultOwner;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(run);
        if (name.Any(char.IsWhiteSpace) || name.Contains(';', StringComparison.Ordinal) || name.Contains('"'))
        {
            throw new ArgumentException($"A command's name is one word; '{name}' is not.", nameof(name));
        }

        if (!_commands.TryAdd(name, new ConsoleCommand(name, usage, help, scope, owner, run)))
        {
            throw new InvalidOperationException($"A console command named '{name}' already exists.");
        }

        if (_inMatch)
        {
            _matchCommands.Add(name);
        }
    }

    /// <summary>
    /// A match starts: commands registered from now (its HUD layers' and systems') are the match's, and go at
    /// <see cref="EndMatch"/>; those without an owner are owned by <see cref="MatchOwner"/>.
    /// </summary>
    public void BeginMatch()
    {
        _inMatch = true;
        _defaultOwner = MatchOwner;
    }

    /// <summary>The match ended: removes every command registered since <see cref="BeginMatch"/>.</summary>
    public void EndMatch()
    {
        _inMatch = false;
        _defaultOwner = EngineOwner;
        foreach (var name in _matchCommands)
        {
            _commands.Remove(name);
        }

        _matchCommands.Clear();
    }

    /// <summary>Removes a command; false when there is none of that name.</summary>
    public bool Unregister(string name) => _commands.Remove(name);

    /// <summary>Removes every command <paramref name="owner"/> registered and returns how many there were.</summary>
    public int UnregisterOwner(string owner)
    {
        var names = _commands.Values.Where(c => c.Owner == owner).Select(c => c.Name).ToList();
        foreach (var name in names)
        {
            _commands.Remove(name);
        }

        return names.Count;
    }

    /// <summary>Finds a command by name, without case.</summary>
    public bool TryGet(string name, out ConsoleCommand command) => _commands.TryGetValue(name, out command!);

    /// <summary>The names that start with <paramref name="prefix"/>, without case, in order.</summary>
    public IEnumerable<string> Complete(string prefix) =>
        _commands.Keys.Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why <paramref name="command"/> may not run in this game now, or null when it may.</summary>
    public string? Refusal(ConsoleCommand command)
    {
        var role = _role();
        return command.Scope switch
        {
            CommandScope.Match when role == SessionRole.Client =>
                $"{command.Name}: only a host or an offline game can use it.",
            CommandScope.Offline when role != SessionRole.Offline =>
                $"{command.Name}: works only in an offline game (in a session it would change what every peer agrees on).",
            _ => null,
        };
    }

    /// <summary>Runs every command in <paramref name="line"/> in turn (<see cref="CommandLine"/>).</summary>
    public void Execute(string line, IConsoleOutput output)
    {
        foreach (var words in CommandLine.Parse(line))
        {
            Run(words, output);
        }
    }

    /// <summary>Runs one command given as its words. Returns false when it was unknown, refused, or threw.</summary>
    public bool Run(string[] words, IConsoleOutput output)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(output);
        if (words.Length == 0)
        {
            return false;
        }

        var text = Join(words);
        LogRun(text);
        if (!_commands.TryGetValue(words[0], out var command))
        {
            output.PrintError($"{words[0]}: no such command; 'help' lists them.");
            return false;
        }

        if (Refusal(command) is { } refusal)
        {
            output.PrintError(refusal);
            return false;
        }

        try
        {
            command.Run(new CommandContext(command, words, output));
            return true;
        }
        catch (Exception exception)
        {
            output.PrintError($"{command.Name} failed: {exception.Message}");
            LogFailed(exception, text);
            return false;
        }
    }

    private static string Join(string[] words)
    {
        var text = new StringBuilder();
        foreach (var word in words)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            var quote = word.Length == 0 || word.Any(c => char.IsWhiteSpace(c) || c == ';');
            text.Append(quote ? "\"" + word + "\"" : word);
        }

        return text.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Console: {Command}")]
    private partial void LogRun(string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Console: {Command} failed")]
    private partial void LogFailed(Exception exception, string command);
}
