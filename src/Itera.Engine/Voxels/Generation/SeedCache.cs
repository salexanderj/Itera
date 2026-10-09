namespace Itera.Engine.Voxels.Generation;

/// <summary>
/// Holds something built from the world's seed, such as a layout of regions or a table of values, for a generator that
/// worker threads call at once. It builds the value on first use and again when another seed is asked for, never twice
/// at the same time; a call that finds the value already built reads it without a lock. Only the last seed is kept.
/// <para>
/// A built value must be a function of the seed alone and must not change after it is built, so every thread sees the
/// same one.
/// </para>
/// </summary>
/// <typeparam name="T">The kind of value built.</typeparam>
public sealed class SeedCache<T>
    where T : class
{
    private readonly Func<ulong, T> _build;
    private readonly Lock _lock = new();
    private Entry? _entry;

    /// <summary>Makes a cache that builds its value with <paramref name="build"/>.</summary>
    public SeedCache(Func<ulong, T> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        _build = build;
    }

    /// <summary>The value for <paramref name="seed"/>.</summary>
    public T Get(ulong seed)
    {
        var entry = Volatile.Read(ref _entry);
        if (entry is not null && entry.Seed == seed)
        {
            return entry.Value;
        }

        lock (_lock)
        {
            entry = _entry;
            if (entry is null || entry.Seed != seed)
            {
                entry = new Entry(seed, _build(seed));
                Volatile.Write(ref _entry, entry);
            }

            return entry.Value;
        }
    }

    private sealed class Entry(ulong seed, T value)
    {
        public ulong Seed { get; } = seed;

        public T Value { get; } = value;
    }
}
