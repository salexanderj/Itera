using Itera.Engine.Meshing;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// <see cref="LevelBalance"/>: a drawn column touching one two levels finer, at a side or a corner, is split into its
/// four finer columns, a split that leaves a new column beside one two levels finer splits it in turn, a column whose
/// finer columns are not meshed stays and is counted, a column's sides and corners past which one a level coarser is
/// drawn are named, and a pass allocates nothing once its tables have grown.
/// </summary>
public sealed class LevelBalanceTests
{
    private static readonly Func<ColumnKey, bool> s_always = static _ => true;
    private static readonly Func<ColumnKey, bool> s_never = static _ => false;

    [Fact]
    public void A_column_beside_one_two_levels_finer_is_split()
    {
        var balance = new LevelBalance();
        balance.Begin(0);
        // Level 3 over finest columns x 0 to 7; beside it, x 8 to 15 split down to level 1 at its near corner.
        balance.Add(new ColumnKey(3, 0, 0));
        balance.Add(new ColumnKey(2, 3, 0));
        balance.Add(new ColumnKey(2, 2, 1));
        balance.Add(new ColumnKey(2, 3, 1));
        balance.Add(new ColumnKey(1, 4, 0));
        balance.Add(new ColumnKey(1, 5, 0));
        balance.Add(new ColumnKey(1, 4, 1));
        balance.Add(new ColumnKey(1, 5, 1));

        balance.Balance(s_always);

        Assert.DoesNotContain(new ColumnKey(3, 0, 0), balance.Leaves);
        Assert.Contains(new ColumnKey(2, 0, 0), balance.Leaves);
        Assert.Contains(new ColumnKey(2, 1, 0), balance.Leaves);
        Assert.Contains(new ColumnKey(2, 0, 1), balance.Leaves);
        Assert.Contains(new ColumnKey(2, 1, 1), balance.Leaves);
        Assert.Equal(11, balance.Leaves.Count);
        Assert.Equal(0, balance.Unbalanced);
    }

    [Fact]
    public void A_column_touching_one_two_levels_finer_at_a_corner_is_split_and_its_child_there_again()
    {
        var balance = new LevelBalance();
        balance.Begin(0);
        // Level 3 over x and z 0 to 7, and one level-0 column at (8, 8), touching it at the corner only.
        balance.Add(new ColumnKey(3, 0, 0));
        balance.Add(new ColumnKey(0, 8, 8));

        balance.Balance(s_always);

        // Split to level 2; its quarter at the corner (x and z 4 to 7) touches the level-0 column and splits to level 1.
        Assert.DoesNotContain(new ColumnKey(3, 0, 0), balance.Leaves);
        Assert.DoesNotContain(new ColumnKey(2, 1, 1), balance.Leaves);
        Assert.Contains(new ColumnKey(2, 0, 0), balance.Leaves);
        Assert.Contains(new ColumnKey(1, 3, 3), balance.Leaves);
        Assert.Equal(1 + 3 + 4, balance.Leaves.Count);
        Assert.Equal(0, balance.Unbalanced);
    }

    [Fact]
    public void Columns_a_level_apart_stay_and_one_whose_finer_columns_are_not_meshed_is_counted()
    {
        var balance = new LevelBalance();
        balance.Begin(0);
        balance.Add(new ColumnKey(2, 0, 0));
        balance.Add(new ColumnKey(1, 2, 0));
        balance.Add(new ColumnKey(1, 2, 1));

        balance.Balance(s_always);

        Assert.Equal(3, balance.Leaves.Count);
        Assert.Equal(0, balance.Unbalanced);

        balance.Begin(0);
        balance.Add(new ColumnKey(3, 0, 0));
        balance.Add(new ColumnKey(1, 4, 0));

        balance.Balance(s_never);

        Assert.Equal([new ColumnKey(3, 0, 0), new ColumnKey(1, 4, 0)], balance.Leaves);
        Assert.Equal(1, balance.Unbalanced);
    }

    [Fact]
    public void The_sides_past_which_a_column_one_level_coarser_is_drawn_are_named()
    {
        var balance = new LevelBalance();
        balance.Begin(0);
        // Level 2 over finest x and z 0 to 3; level 1 over x 4 to 7, z 0 to 3, but for x 6 to 7, z 0 to 1 at level 0.
        balance.Add(new ColumnKey(2, 0, 0));
        balance.Add(new ColumnKey(1, 2, 0));
        balance.Add(new ColumnKey(1, 2, 1));
        balance.Add(new ColumnKey(1, 3, 1));
        balance.Add(new ColumnKey(0, 6, 0));
        balance.Add(new ColumnKey(0, 7, 0));
        balance.Add(new ColumnKey(0, 6, 1));
        balance.Add(new ColumnKey(0, 7, 1));

        balance.Balance(s_always);

        Assert.Equal(8, balance.Leaves.Count);
        Assert.Equal(TransitionSides.LowX | TransitionSides.LowXHighZ, balance.SidesOf(new ColumnKey(1, 2, 0)));
        Assert.Equal(TransitionSides.LowX | TransitionSides.LowXLowZ, balance.SidesOf(new ColumnKey(1, 2, 1)));
        Assert.Equal(TransitionSides.LowX | TransitionSides.LowXHighZ, balance.SidesOf(new ColumnKey(0, 6, 0)));
        Assert.Equal(TransitionSides.HighZ | TransitionSides.LowXHighZ, balance.SidesOf(new ColumnKey(0, 7, 1)));
        Assert.Equal(
            TransitionSides.LowX
                | TransitionSides.HighZ
                | TransitionSides.LowXLowZ
                | TransitionSides.LowXHighZ
                | TransitionSides.HighXHighZ,
            balance.SidesOf(new ColumnKey(0, 6, 1))
        );
        // Finer neighbours and none at all are not named.
        Assert.Equal(TransitionSides.None, balance.SidesOf(new ColumnKey(2, 0, 0)));
        Assert.Equal(TransitionSides.None, balance.SidesOf(new ColumnKey(1, 3, 1)));
    }

    [Fact]
    public void A_pass_allocates_nothing_once_its_tables_have_grown()
    {
        var balance = new LevelBalance();
        for (var pass = 0; pass < 2; pass++)
        {
            var start = SystemAllocation.StartMeasurement();
            balance.Begin(0);
            balance.Add(new ColumnKey(3, 0, 0));
            balance.Add(new ColumnKey(0, 8, 8));
            balance.Add(new ColumnKey(3, 2, 0));
            balance.Balance(s_always);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            if (pass == 1)
            {
                Assert.Equal(0, allocated);
            }
        }
    }
}
