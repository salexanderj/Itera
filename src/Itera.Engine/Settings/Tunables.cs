using System.Globalization;

namespace Itera.Engine.Settings;

/// <summary>Who a tunable's value belongs to (<see cref="Tunables"/>).</summary>
public enum TuningScope
{
    /// <summary>This game's alone (a camera's feel, a sound's level): set anywhere, sent nowhere.</summary>
    Local = 0,

    /// <summary>
    /// The match's (a speed, a recoil, a part's limit): what a client predicts must move by the host's numbers, so only a
    /// host or an offline game sets it, and a host sends its value to its clients.
    /// </summary>
    Match = 1,
}

/// <summary>
/// A named number a game reads while it plays and a developer changes live (<c>tune NAME VALUE</c>): its value, its
/// default, its range, and its scope. Read <see cref="Value"/> anywhere; it allocates nothing.
/// </summary>
public sealed class Tunable
{
    internal Tunable(string name, float value, float least, float most, string help, TuningScope scope)
    {
        Name = name;
        Default = value;
        Value = value;
        Least = least;
        Most = most;
        Help = help;
        Scope = scope;
    }

    public string Name { get; }

    public float Value { get; internal set; }

    public float Default { get; }

    public float Least { get; }

    public float Most { get; }

    public string Help { get; }

    public TuningScope Scope { get; }

    /// <summary>Raised each time the value changes, so a host knows which to send.</summary>
    internal long Version { get; set; }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Name} = {Value:G6} ({Least:G6} to {Most:G6}, {(Scope == TuningScope.Match ? "match" : "local")}{(Value != Default ? $", default {Default:G6}" : "")}): {Help}"
        );
}

/// <summary>
/// The run's tunables (<see cref="Tunable"/>): the numbers a game tunes while it plays (movement, recoil, sway, part
/// limits, an enemy's), registered once at the run's start (a game's <c>IRunStartup</c>) and sealed by the first match,
/// each a name of 1 to 64 letters, digits, '.', '-', or '_', a default within a range, a line of help, and a scope. The
/// console's <c>tune</c> lists and sets them. A host sends its match tunables to its clients when they change and as each
/// joins, by name, so a client moves by the host's numbers (<c>SessionService</c>). Every game's match tunables go back to
/// their defaults when its match ends, a host's and an offline game's too. Main thread.
/// </summary>
public sealed class Tunables
{
    /// <summary>The most tunables a run registers.</summary>
    public const int MostTunables = 1024;

    /// <summary>The longest name a tunable may have.</summary>
    public const int MostNameLength = 64;

    private readonly List<Tunable> _all = [];
    private readonly Dictionary<string, Tunable> _byName = new(StringComparer.Ordinal);
    private long _version;
    private bool _sealed;

    /// <summary>The tunables, in the order of their registration.</summary>
    public IReadOnlyList<Tunable> All => _all;

    /// <summary>Raised each time any value changes.</summary>
    internal long Version => _version;

    /// <summary>
    /// Registers a tunable of <paramref name="value"/>, from <paramref name="least"/> to <paramref name="most"/>. Throws
    /// <see cref="ArgumentException"/> for a name not allowed or taken, or a range that does not hold the value, and
    /// <see cref="InvalidOperationException"/> once the first match has sealed them or past <see cref="MostTunables"/>.
    /// </summary>
    public Tunable Register(
        string name,
        float value,
        float least,
        float most,
        string help,
        TuningScope scope = TuningScope.Match
    )
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(help);
        if (_sealed)
        {
            throw new InvalidOperationException($"The tunables are sealed by the first match; {name} is too late.");
        }

        if (NameProblem(name) is { } problem)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        if (!float.IsFinite(least) || !float.IsFinite(most) || least > value || value > most || !float.IsFinite(value))
        {
            throw new ArgumentException(
                $"The tunable {name}'s range {least} to {most} does not hold {value}.",
                nameof(value)
            );
        }

        if (_all.Count == MostTunables)
        {
            throw new InvalidOperationException($"At most {MostTunables} tunables; {name} is one too many.");
        }

        var tunable = new Tunable(name, value, least, most, help, scope);
        if (!_byName.TryAdd(name, tunable))
        {
            throw new ArgumentException($"The tunable {name} is registered already.", nameof(name));
        }

        _all.Add(tunable);
        return tunable;
    }

    /// <summary>The tunable named <paramref name="name"/>, or null.</summary>
    public Tunable? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// Sets <paramref name="tunable"/> to <paramref name="value"/>. False, the value unchanged, for a number that is not
    /// finite or out of its range.
    /// </summary>
    public bool TrySet(Tunable tunable, float value)
    {
        ArgumentNullException.ThrowIfNull(tunable);
        if (!float.IsFinite(value) || value < tunable.Least || value > tunable.Most)
        {
            return false;
        }

        if (value != tunable.Value)
        {
            tunable.Value = value;
            tunable.Version = ++_version;
        }

        return true;
    }

    /// <summary>Every tunable of <paramref name="scope"/> back to its default.</summary>
    public void Reset(TuningScope scope)
    {
        foreach (var tunable in _all)
        {
            if (tunable.Scope == scope)
            {
                TrySet(tunable, tunable.Default);
            }
        }
    }

    /// <summary>What is wrong with a name, or null when nothing is.</summary>
    internal static string? NameProblem(string name)
    {
        if (name.Length is 0 or > MostNameLength)
        {
            return $"A tunable's name has {name.Length} characters; 1 to {MostNameLength} fit.";
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return $"The tunable name {name} holds a character other than a letter, digit, '.', '-', or '_'.";
            }
        }

        return null;
    }

    /// <summary>No more tunables: the first match seals them.</summary>
    internal void Seal() => _sealed = true;
}
