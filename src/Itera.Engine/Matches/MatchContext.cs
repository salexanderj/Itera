namespace Itera.Engine.Matches;

/// <summary>
/// The match a DI scope serves: set once, right after the scope is made, before anything in it is resolved. The
/// match's services read their <see cref="MatchStart"/> from it (the session its mode and address, the save its name).
/// </summary>
public sealed class MatchContext
{
    private MatchStart? _start;

    /// <summary>The match this scope serves. Throws before <see cref="Set"/>.</summary>
    public MatchStart Start =>
        _start ?? throw new InvalidOperationException("The match's scope was used before its start was set.");

    /// <summary>Sets the match; once.</summary>
    public void Set(MatchStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (_start is not null)
        {
            throw new InvalidOperationException("A match's start is set once.");
        }

        _start = start;
    }
}
