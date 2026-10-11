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
/// not meshed yet stays (<see cref="Unbalanced"/> counts them). It keeps the drawn columns themselves and finds the one
/// over a finest column by looking at each level above it, so a column 512 finest columns wide (level 9 over level 0)
/// costs one entry, not a quarter of a million. Main thread; allocates nothing once its tables have grown.
/// </summary>
internal sealed class LevelBalance
{
    private readonly List<ColumnKey> _leaves = [];

    // The columns drawn: they do not overlap, so one at most lies over each finest column.
    private readonly HashSet<ColumnKey> _drawn = [];
    private int _finest;
    private int _coarsest;

    /// <summary>The columns to draw, after <see cref="Balance"/>.</summary>
    public IReadOnlyList<ColumnKey> Leaves => _leaves;

    /// <summary>Columns <see cref="Balance"/> left beside one two or more levels finer: their finer columns were not meshed.</summary>
    public int Unbalanced { get; private set; }

    /// <summary>Starts a pass whose finest drawn level is <paramref name="finestLevel"/>.</summary>
    public void Begin(int finestLevel)
    {
        _leaves.Clear();
        _drawn.Clear();
        _finest = finestLevel;
        _coarsest = finestLevel;
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
        _drawn.Add(column);
        _coarsest = Math.Max(_coarsest, column.Level);
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
                _drawn.Remove(leaf);
                _drawn.Add(new ColumnKey(level, x, z));
                _drawn.Add(new ColumnKey(level, x + 1, z));
                _drawn.Add(new ColumnKey(level, x, z + 1));
                _drawn.Add(new ColumnKey(level, x + 1, z + 1));
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

    /// <summary>The column of <paramref name="level"/> over finest column (x, z) is drawn: one look.</summary>
    private TransitionSides Drawn(int x, int z, int level, TransitionSides side)
    {
        var shift = level - _finest;
        return _drawn.Contains(new ColumnKey(level, x >> shift, z >> shift)) ? side : TransitionSides.None;
    }

    /// <summary>The level of the column drawn over finest column (x, z), or -1 where none is.</summary>
    private int LevelAt(int x, int z)
    {
        for (var level = _finest; level <= _coarsest; level++)
        {
            var shift = level - _finest;
            if (_drawn.Contains(new ColumnKey(level, x >> shift, z >> shift)))
            {
                return level;
            }
        }

        return -1;
    }

    /// <summary>True when a column drawn two or more levels finer than <paramref name="leaf"/> touches it, at a side or a corner.</summary>
    private bool TouchesFiner(ColumnKey leaf)
    {
        var shift = leaf.Level - _finest;
        var size = 1 << shift;
        int x0 = leaf.X << shift,
            z0 = leaf.Z << shift;
        var most = leaf.Level - 2;
        // The ring of finest columns round the leaf's square: the rows past its low and high z, corners included, then
        // the columns past its low and high x.
        return FinerAlongX(x0 - 1, x0 + size, z0 - 1, most)
            || FinerAlongX(x0 - 1, x0 + size, z0 + size, most)
            || FinerAlongZ(z0, z0 + size - 1, x0 - 1, most)
            || FinerAlongZ(z0, z0 + size - 1, x0 + size, most);
    }

    /// <summary>
    /// True when a column of <paramref name="most"/> or finer is drawn over finest columns <paramref name="first"/> to
    /// <paramref name="last"/> along x at <paramref name="z"/>; each column found is stepped past whole.
    /// </summary>
    private bool FinerAlongX(int first, int last, int z, int most)
    {
        var x = first;
        while (x <= last)
        {
            var level = LevelAt(x, z);
            if (level >= 0 && level <= most)
            {
                return true;
            }

            x = level < 0 ? x + 1 : ((x >> (level - _finest)) + 1) << (level - _finest);
        }

        return false;
    }

    /// <summary>As <see cref="FinerAlongX"/>, along z at <paramref name="x"/>.</summary>
    private bool FinerAlongZ(int first, int last, int x, int most)
    {
        var z = first;
        while (z <= last)
        {
            var level = LevelAt(x, z);
            if (level >= 0 && level <= most)
            {
                return true;
            }

            z = level < 0 ? z + 1 : ((z >> (level - _finest)) + 1) << (level - _finest);
        }

        return false;
    }
}
