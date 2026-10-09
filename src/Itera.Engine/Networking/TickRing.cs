namespace Itera.Engine.Networking;

/// <summary>
/// The last <see cref="Capacity"/> values of something sent every tick, by the sender's tick, whatever order they came
/// in: what a jitter buffer plays back from. Older than the ring holds is dropped. Allocates nothing once made.
/// </summary>
public sealed class TickRing<T>
    where T : struct
{
    public const int Capacity = 16;

    private readonly (uint Tick, T Value, bool Filled)[] _entries = new (uint, T, bool)[Capacity];

    public bool Any { get; private set; }

    public uint Newest { get; private set; }

    public void Store(uint tick, in T value)
    {
        if (Any && tick + Capacity <= Newest)
        {
            return;
        }

        _entries[tick % Capacity] = (tick, value, true);
        if (!Any || tick > Newest)
        {
            Newest = tick;
        }

        Any = true;
    }

    public bool TryGet(uint tick, out T value)
    {
        ref readonly var entry = ref _entries[tick % Capacity];
        if (entry.Filled && entry.Tick == tick)
        {
            value = entry.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The newest value at or before <paramref name="tick"/> that the ring still holds: for something sent only when it
    /// changes, whose value holds until the next.
    /// </summary>
    public bool TryGetLatest(uint tick, out T value) => TryGetLatest(tick, out value, out _);

    /// <summary>The same, with the tick the value is for.</summary>
    public bool TryGetLatest(uint tick, out T value, out uint at)
    {
        var found = -1;
        for (var i = 0; i < Capacity; i++)
        {
            ref readonly var entry = ref _entries[i];
            if (entry.Filled && entry.Tick <= tick && (found < 0 || entry.Tick > _entries[found].Tick))
            {
                found = i;
            }
        }

        value = found >= 0 ? _entries[found].Value : default;
        at = found >= 0 ? _entries[found].Tick : 0;
        return found >= 0;
    }
}

/// <summary>
/// Playing something back a few ticks behind the newest state that arrived (a jitter buffer): start
/// <see cref="DelayTicks"/> behind, advance a tick at a time while at or behind that target, hold when ahead (states
/// came late, so the buffer fills again), and skip ahead when more than <see cref="MaxBehindTicks"/> behind.
/// </summary>
public static class JitterPlayback
{
    public const int DelayTicks = 3;

    public const int MaxBehindTicks = 8;

    /// <summary>
    /// Moves a shown tick on by one, and back to <see cref="DelayTicks"/> behind the newest when it drifted more than
    /// <see cref="MaxBehindTicks"/> from it either way (a stall, or batches lost or skipped): a clock that follows the
    /// host's steps, for what every game shows at the same host tick.
    /// </summary>
    public static void AdvanceClock(ref uint playTick, ref bool playing, uint newest)
    {
        var target = newest >= DelayTicks ? newest - DelayTicks : 0;
        if (!playing)
        {
            playTick = target;
            playing = true;
            return;
        }

        playTick++;
        if (playTick + MaxBehindTicks < newest || playTick > newest + MaxBehindTicks)
        {
            playTick = target;
        }
    }

    public static void Advance(ref uint playTick, ref bool playing, uint newest)
    {
        var target = newest >= DelayTicks ? newest - DelayTicks : 0;
        if (!playing)
        {
            playTick = target;
            playing = true;
        }
        else if (playTick + MaxBehindTicks < newest)
        {
            playTick = target;
        }
        else if (playTick <= target)
        {
            playTick++;
        }
    }
}
