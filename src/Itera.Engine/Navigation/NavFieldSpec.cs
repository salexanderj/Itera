namespace Itera.Engine.Navigation;

/// <summary>
/// One flow field the navigation keeps: the size of walker it is for, what cells cost that walker, and the goal cells
/// its routes end in. The game makes one per kind of walker and goal; the engine knows nothing of teams.
/// </summary>
public sealed class NavFieldSpec
{
    private readonly int[] _goalCells;

    public NavFieldSpec(string name, NavSize size, NavCostTable costs, int[] goalCells)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(size);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(goalCells);
        if (goalCells.Length == 0)
        {
            throw new ArgumentException("A field needs at least one goal cell.", nameof(goalCells));
        }

        Name = name;
        Size = size;
        Costs = costs;
        _goalCells = goalCells;
    }

    /// <summary>A name for logs and the debug picture.</summary>
    public string Name { get; }

    /// <summary>The size of walker the field is for.</summary>
    public NavSize Size { get; }

    /// <summary>What entering a cell costs.</summary>
    public NavCostTable Costs { get; }

    /// <summary>The cells routes end in.</summary>
    public ReadOnlySpan<int> GoalCells => _goalCells;
}
