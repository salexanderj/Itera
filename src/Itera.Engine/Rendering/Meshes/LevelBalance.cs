using Itera.Engine.Meshing;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// Keeps the columns a pass draws within one level of their neighbours: the levels' seams are stitched between a level
/// and the next coarser one only (transition cells), and the reaches alone let a
/// level-3 column stand beside level-1 ones (67 to 85 m) and a level-2 column beside level-0 ones (34 to 42 m). The
/// pass adds the columns it would draw (<see cref="Add"/>); <see cref="Balance"/> then splits each column that touches
/// one two or more levels finer, along a side or at a corner, into its four columns of the level below, where those
/// are meshed, until none is left; a split can make its coarser neighbours split in turn. A column whose finer ones are
/// not meshed yet stays (<see cref="Unbalanced"/> counts them). Main thread; allocates nothing once its tables have
/// grown.
/// </summary>
internal sealed class LevelBalance
{
    private readonly List<ColumnKey> _leaves = [];

    // The level drawn over each column of the finest level, by its x and z.
    private readonly Dictionary<long, int> _levels = [];
    private int _finest;

    /// <summary>The columns to draw, after <see cref="Balance"/>.</summary>
    public IReadOnlyList<ColumnKey> Leaves => _leaves;

    /// <summary>Columns <see cref="Balance"/> left beside one two or more levels finer: their finer columns were not meshed.</summary>
    public int Unbalanced { get; private set; }

    /// <summary>Starts a pass whose finest drawn level is <paramref name="finestLevel"/>.</summary>
    public void Begin(int finestLevel)
    {
        _leaves.Clear();
        _levels.Clear();
        _finest = finestLevel;
        Unbalanced = 0;
    }

    /// <summary>Adds a column the pass would draw; columns added must not overlap.</summary>
    public void Add(ColumnKey column)
    {
        if (column.Level < _finest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(column),
                column,
                "A column finer than the pass's finest level."
            );
        }

        _leaves.Add(column);
        Mark(column, column.Level);
    }

    /// <summary>
    /// Splits columns until no two that touch differ by more than a level, where <paramref name="canSplit"/> says a
    /// column's four finer ones are meshed.
    /// </summary>
    public void Balance(Func<ColumnKey, bool> canSplit)
    {
        ArgumentNullException.ThrowIfNull(canSplit);
        var changed = true;
        while (changed)
        {
            changed = false;
            // Leaves added by a split are looked at in the same sweep, after the others.
            for (var i = 0; i < _leaves.Count; i++)
            {
                var leaf = _leaves[i];
                if (leaf.Level < _finest + 2 || !TouchesFiner(leaf) || !canSplit(leaf))
                {
                    continue;
                }

                var level = leaf.Level - 1;
                int x = leaf.X << 1,
                    z = leaf.Z << 1;
                _leaves[i] = new ColumnKey(level, x, z);
                _leaves.Add(new ColumnKey(level, x + 1, z));
                _leaves.Add(new ColumnKey(level, x, z + 1));
                _leaves.Add(new ColumnKey(level, x + 1, z + 1));
                Mark(leaf, level);
                changed = true;
            }
        }

        foreach (var leaf in _leaves)
        {
            Unbalanced += leaf.Level >= _finest + 2 && TouchesFiner(leaf) ? 1 : 0;
        }
    }

    /// <summary>
    /// The sides and corners of <paramref name="leaf"/>, a column of <see cref="Leaves"/>, past which a column one level
    /// coarser is drawn: where its blocks are meshed in the transition mode. After <see cref="Balance"/>, a column
    /// beside a side is aligned to twice the leaf's size, so one finest column just past each side and corner tells.
    /// </summary>
    public TransitionSides SidesOf(ColumnKey leaf)
    {
        var shift = leaf.Level - _finest;
        var size = 1 << shift;
        int x0 = leaf.X << shift,
            z0 = leaf.Z << shift;
        var coarser = leaf.Level + 1;
        var sides = TransitionSides.None;
        sides |= Drawn(x0 - 1, z0, coarser, TransitionSides.LowX);
        sides |= Drawn(x0 + size, z0, coarser, TransitionSides.HighX);
        sides |= Drawn(x0, z0 - 1, coarser, TransitionSides.LowZ);
        sides |= Drawn(x0, z0 + size, coarser, TransitionSides.HighZ);
        sides |= Drawn(x0 - 1, z0 - 1, coarser, TransitionSides.LowXLowZ);
        sides |= Drawn(x0 + size, z0 - 1, coarser, TransitionSides.HighXLowZ);
        sides |= Drawn(x0 - 1, z0 + size, coarser, TransitionSides.LowXHighZ);
        sides |= Drawn(x0 + size, z0 + size, coarser, TransitionSides.HighXHighZ);
        return sides;
    }

    private TransitionSides Drawn(int x, int z, int level, TransitionSides side) =>
        _levels.TryGetValue(Pack(x, z), out var drawn) && drawn == level ? side : TransitionSides.None;

    /// <summary>True when a column drawn two or more levels finer than <paramref name="leaf"/> touches it, at a side or a corner.</summary>
    private bool TouchesFiner(ColumnKey leaf)
    {
        var shift = leaf.Level - _finest;
        var size = 1 << shift;
        int x0 = leaf.X << shift,
            z0 = leaf.Z << shift;
        var most = leaf.Level - 2;
        // The ring of finest columns round the leaf's square.
        for (var i = -1; i <= size; i++)
        {
            if (
                Finer(x0 + i, z0 - 1, most)
                || Finer(x0 + i, z0 + size, most)
                || (i >= 0 && i < size && (Finer(x0 - 1, z0 + i, most) || Finer(x0 + size, z0 + i, most)))
            )
            {
                return true;
            }
        }

        return false;
    }

    private bool Finer(int x, int z, int most) => _levels.TryGetValue(Pack(x, z), out var level) && level <= most;

    /// <summary>Records <paramref name="level"/> as drawn over every finest column of <paramref name="column"/>'s square.</summary>
    private void Mark(ColumnKey column, int level)
    {
        var shift = column.Level - _finest;
        var size = 1 << shift;
        int x0 = column.X << shift,
            z0 = column.Z << shift;
        for (var z = 0; z < size; z++)
        {
            for (var x = 0; x < size; x++)
            {
                _levels[Pack(x0 + x, z0 + z)] = level;
            }
        }
    }

    private static long Pack(int x, int z) => ((long)x << 32) | (uint)z;
}
