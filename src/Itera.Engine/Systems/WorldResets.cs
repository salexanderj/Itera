namespace Itera.Engine.Systems;

/// <summary>
/// Counts the console's resets (<see cref="MapResetSystem"/>). A game's systems that put something back at a reset keep
/// the count they last saw and act once when it changes, in the same fixed step, since the reset system runs first.
/// Main thread.
/// </summary>
public sealed class WorldResets
{
    /// <summary>Resets so far.</summary>
    public int Count { get; private set; }

    /// <summary>Counts a reset.</summary>
    public void Add() => Count++;

    /// <summary>True when a reset came since <paramref name="seen"/>, which it then brings up to date.</summary>
    public bool Since(ref int seen)
    {
        var reset = seen != Count;
        seen = Count;
        return reset;
    }
}
