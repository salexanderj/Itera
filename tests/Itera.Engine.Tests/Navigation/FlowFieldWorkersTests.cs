using Itera.Engine.Navigation;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Navigation;

/// <summary>
/// <see cref="FlowFieldWorkers"/>: a set of fields built at once comes out as one builder alone builds each, the set
/// stops between fields once asked, and a quarter of the processors build, never more than there are fields.
/// </summary>
public sealed class FlowFieldWorkersTests
{
    private const int Size = 96;

    private static readonly NavSize s_small = new("small", 200, 30, 45, 64);
    private static readonly NavSize s_large = new("large", 300, 45, 45, 64);
    private static readonly NavCostTable s_breaking = new([200, 10], 400, 3000);
    private static readonly NavCostTable s_avoiding = new([200, 10], 20_000, 3000);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public void Fields_built_at_once_match_those_one_builder_builds_alone(int helpers)
    {
        var grid = Grid();
        NavFieldSpec[] specs =
        [
            new("a", s_small, s_breaking, Places(grid, [grid.Index(90, 90)])),
            new("b", s_large, s_avoiding, Places(grid, [grid.Index(5, 80), grid.Index(6, 80)])),
            new("c", s_small, s_avoiding, Places(grid, [grid.Index(48, 2)])),
            new("d", s_large, s_breaking, Places(grid, [grid.Index(70, 30)])),
        ];
        var walkable = Walkable(grid, specs);
        var alone = new FlowField[specs.Length];
        var builder = new FlowFieldBuilder();
        for (var i = 0; i < specs.Length; i++)
        {
            alone[i] = new FlowField(grid);
            builder.Build(grid, specs[i], walkable[i], alone[i]);
        }

        var fields = specs.Select(_ => new FlowField(grid)).ToArray();
        var milliseconds = new double[specs.Length];
        using (var workers = new FlowFieldWorkers(helpers, () => false))
        {
            // Twice, so the second set reuses the helpers and their builders.
            workers.Build(grid, specs, walkable, fields, milliseconds);
            workers.Build(grid, specs, walkable, fields, milliseconds);
        }

        for (var i = 0; i < specs.Length; i++)
        {
            Assert.Equal(alone[i].Directions, fields[i].Directions);
        }

        Assert.All(milliseconds, ms => Assert.True(ms > 0));
    }

    [Fact]
    public void A_set_asked_to_stop_starts_no_field()
    {
        var grid = Grid();
        NavFieldSpec[] specs = [new("a", s_small, s_breaking, Places(grid, [grid.Index(90, 90)]))];
        var fields = new[] { new FlowField(grid) };
        using var workers = new FlowFieldWorkers(1, () => true);

        workers.Build(grid, specs, Walkable(grid, specs), fields, new double[1]);

        // As made: no field was built into it.
        Assert.Equal(new FlowField(grid).Directions, fields[0].Directions);
    }

    [Theory]
    [InlineData(4, 16, 3)]
    [InlineData(4, 8, 1)]
    [InlineData(4, 64, 3)]
    [InlineData(1, 16, 0)]
    [InlineData(4, 2, 0)]
    [InlineData(0, 16, 0)]
    public void A_quarter_of_the_processors_build_never_more_than_the_fields(int fields, int processors, int helpers) =>
        Assert.Equal(helpers, FlowFieldWorkers.HelpersFor(fields, processors));

    /// <summary>Open road with walls across it, gaps in them, and a raised block, so each field routes its own way.</summary>
    private static NavGrid Grid()
    {
        var grid = new NavGrid(0, 0, Size, Size);
        grid.Clear.AsSpan().Fill(byte.MaxValue);
        grid.Class.AsSpan().Fill(1);
        for (var x = 0; x < Size; x++)
        {
            if (x is not (20 or 21 or 70))
            {
                grid.Clear[grid.Index(x, 40)] = 0;
            }

            if (x is not (50 or 51))
            {
                grid.Clear[grid.Index(x, 60)] = 0;
            }
        }

        for (var z = 10; z < 30; z++)
        {
            for (var x = 30; x < 45; x++)
            {
                grid.Floor[grid.Index(x, z)] = 120;
            }
        }

        return grid;
    }

    private static byte[][] Walkable(NavGrid grid, NavFieldSpec[] specs)
    {
        var walkable = new byte[specs.Length][];
        for (var i = 0; i < specs.Length; i++)
        {
            walkable[i] = new byte[grid.Count];
            specs[i].Size.Derive(grid, walkable[i], 0, grid.Depth - 1);
        }

        return walkable;
    }

    private static VoxelCoord[] Places(NavGrid grid, int[] cells) =>
        [.. cells.Select(cell => new VoxelCoord(grid.CentreX(grid.Column(cell)), 0, grid.CentreZ(grid.Row(cell))))];
}
