namespace Itera.Engine.Navigation;

/// <summary>
/// What entering a cell costs one kind of walker: a cost per cost class (<see cref="NavGrid.Class"/>) for a walkable
/// cell, <see cref="Break"/> for a cell the walker's size cannot stand in (it would have to break through), and
/// <see cref="Steep"/> for a rise past the size's climb or a drop past its drop. Straight moves cost the table's
/// numbers; diagonal moves cost 1.4 times as much, rounded. A walkable step down within the size's drop also costs
/// <see cref="DropPerMetre"/> for each metre it drops (none by default), never more than the steep cost in all: as every
/// route between two places drops as far as it climbs, less the height between them, it is the going down and back up
/// that costs, so routes go round a hole or a bump where the way round is short. Every cost is at least 1, so a route
/// never loops.
/// </summary>
public sealed class NavCostTable
{
    private readonly int[] _straight = new int[256];
    private readonly int[] _diagonal = new int[256];

    /// <param name="classCosts">Straight cost of each class, by class number; classes past its end cost the last one.</param>
    /// <param name="breakCost">Straight cost of entering a cell the size cannot stand in.</param>
    /// <param name="steepCost">Straight cost of a rise past the size's climb or a drop past its drop.</param>
    /// <param name="dropPerMetre">What a walkable step down costs besides its cell, for each metre it drops.</param>
    public NavCostTable(ReadOnlySpan<int> classCosts, int breakCost, int steepCost, int dropPerMetre = 0)
    {
        if (classCosts.IsEmpty)
        {
            throw new ArgumentException("At least one class cost is needed.", nameof(classCosts));
        }

        for (var i = 0; i < 256; i++)
        {
            var cost = classCosts[Math.Min(i, classCosts.Length - 1)];
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cost, nameof(classCosts));
            _straight[i] = cost;
            _diagonal[i] = Diagonal(cost);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(breakCost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(steepCost);
        ArgumentOutOfRangeException.ThrowIfNegative(dropPerMetre);
        Break = breakCost;
        Steep = steepCost;
        DropPerMetre = dropPerMetre;
        DiagonalBreak = Diagonal(breakCost);
        DiagonalSteep = Diagonal(steepCost);
        var cheapest = int.MaxValue;
        foreach (var cost in classCosts)
        {
            cheapest = Math.Min(cheapest, cost);
        }

        Cheapest = cheapest;
        Largest = Math.Max(Math.Max(DiagonalBreak, DiagonalSteep), _diagonal.Max());
    }

    /// <summary>Straight cost of entering a cell the size cannot stand in.</summary>
    public int Break { get; }

    /// <summary>Straight cost of a rise past the size's climb or a drop past its drop.</summary>
    public int Steep { get; }

    /// <summary>Diagonal cost of entering a cell the size cannot stand in.</summary>
    public int DiagonalBreak { get; }

    /// <summary>Diagonal cost of a rise past the size's climb or a drop past its drop.</summary>
    public int DiagonalSteep { get; }

    /// <summary>What a walkable step down costs besides its cell, for each metre it drops.</summary>
    public int DropPerMetre { get; }

    /// <summary>
    /// What a walkable step into a cell costing <paramref name="cellCost"/> (its class's straight or diagonal cost) costs
    /// with its drop of <paramref name="dropCentimetres"/> (0 or less: none): the cell's cost and
    /// <see cref="DropPerMetre"/> a metre of drop, rounded, at most <paramref name="steepCost"/> (the steep cost of the
    /// same kind of step) unless the cell alone costs more.
    /// </summary>
    public int WithDrop(int cellCost, int dropCentimetres, int steepCost)
    {
        if (dropCentimetres <= 0 || DropPerMetre == 0)
        {
            return cellCost;
        }

        var drop = ((long)dropCentimetres * DropPerMetre + 50) / 100;
        return (int)Math.Max(cellCost, Math.Min(cellCost + drop, steepCost));
    }

    /// <summary>The cheapest class's straight cost.</summary>
    public int Cheapest { get; }

    /// <summary>The largest cost of any one move.</summary>
    public int Largest { get; }

    /// <summary>Straight cost of entering a walkable cell of class <paramref name="costClass"/>.</summary>
    public int Straight(byte costClass) => _straight[costClass];

    /// <summary>Diagonal cost of entering a walkable cell of class <paramref name="costClass"/>.</summary>
    public int DiagonalCost(byte costClass) => _diagonal[costClass];

    private static int Diagonal(int cost) => (cost * 14 + 5) / 10;
}
