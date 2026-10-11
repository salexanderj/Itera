using Itera.Engine.Voxels;

namespace Itera.Engine.Navigation;

/// <summary>
/// One flow field the navigation keeps: the size of walker it is for, what cells cost that walker, and the goals its
/// routes end in, absolute places (x and z in centimetres; y is not read). Each build maps a goal to the cell under it,
/// or, outside the grid's window, to the window's edge cell nearest it (<see cref="NavGrid.NearestCell"/>), so walkers
/// head that way until the window reaches it. The game makes one per kind of walker and goal; the engine knows nothing
/// of teams.
/// </summary>
public sealed class NavFieldSpec
{
    private readonly VoxelCoord[] _goals;

    public NavFieldSpec(string name, NavSize size, NavCostTable costs, VoxelCoord[] goals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(size);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(goals);
        if (goals.Length == 0)
        {
            throw new ArgumentException("A field needs at least one goal.", nameof(goals));
        }

        Name = name;
        Size = size;
        Costs = costs;
        _goals = goals;
    }

    /// <summary>A name for logs and the debug picture.</summary>
    public string Name { get; }

    /// <summary>The size of walker the field is for.</summary>
    public NavSize Size { get; }

    /// <summary>What entering a cell costs.</summary>
    public NavCostTable Costs { get; }

    /// <summary>The places routes end in, absolute (x and z in centimetres).</summary>
    public ReadOnlySpan<VoxelCoord> Goals => _goals;
}
