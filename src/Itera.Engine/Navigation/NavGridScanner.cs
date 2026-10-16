using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Navigation;

/// <summary>
/// Reads navigation cells from the level-0 voxels where they are loaded, by the rule the game's builder follows at each
/// cell's centre: below a limit of <see cref="LowCentimetres"/> over the ground there (<see cref="INavGridSource.GroundAt"/>;
/// the cell's floor where the game gives none), the surface over the highest blocking voxel is the new floor (a dig
/// lowers it, a low step raises it): where its density puts the ground (<see cref="VoxelWorld.SurfaceOver"/>, as the
/// mesher draws it and the mover stands on it; on voxels without densities, the voxel's top); solid whose surface rises
/// past that limit blocks the cell (stone placed on a path, or 1 dm
/// cubes stacked one at a time: judged from the floor each cube raised, a stack would be a floor to walk over); otherwise the headroom runs up to the lowest
/// blocking voxel over the floor (a tunnel dug through a wall opens with its height). Only the voxel column at the
/// cell's centre is read, as the builder samples its centre. Foliage does not block, as for the character mover. The
/// voxel objects a world places count as the voxels do where given (<see cref="Objects"/>: the mover's, read in the same
/// cells and combined by the highest top and the lowest bottom), and <see cref="StampObjects"/> stamps them, as
/// generated, onto cells a source filled. Main thread (but <see cref="Stamp"/> and <see cref="StampObjects"/>);
/// allocates nothing once the objects' chunks it reads are made (<c>ObjectColumns</c>, which allocates until its cache is
/// full).
/// </summary>
public sealed class NavGridScanner
{
    /// <summary>How far below a cell's floor a dig is looked for: 4 m.</summary>
    public const int DepthCentimetres = 400;

    private readonly VoxelWorld _world;
    private readonly MaterialSet _passable;
    private readonly INavGridSource? _source;

    /// <param name="world">The voxels.</param>
    /// <param name="passability">The materials walkers pass through.</param>
    /// <param name="lowCentimetres">The highest a solid may rise over the ground and still be stood on.</param>
    /// <param name="source">The game's ground and soil (<see cref="INavGridSource.GroundAt"/>,
    /// <see cref="INavGridSource.IsGround"/>); null to judge from each cell's floor and dig for every removal.</param>
    public NavGridScanner(
        VoxelWorld world,
        IVoxelPassability passability,
        int lowCentimetres,
        INavGridSource? source = null
    )
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(passability);
        _world = world;
        _passable = MaterialSet.Passable(passability);
        LowCentimetres = lowCentimetres;
        _source = source;
    }

    /// <summary>The highest a solid may rise over the floor and still be stood on.</summary>
    public int LowCentimetres { get; }

    /// <summary>
    /// The voxel objects the world places or a game places during play, as the mover reads them, in the mover's cells
    /// (its <c>VoxelCharacterMover.Objects</c>, or one of navigation's own in the same cells), or null for none: a read
    /// waits until they are known round the cell, and counts them as voxels: in cells finer than the world's voxels, any
    /// object's voxel in the cell's square counts, so a thin wall anywhere in it blocks it; in the voxels' own, the
    /// column at its centre, as the voxels'.
    /// </summary>
    internal ObjectColumns? Objects { get; init; }

    /// <summary>
    /// True when the objects that may stand in the level-0 voxel columns from (<paramref name="x0"/>,
    /// <paramref name="z0"/>) to (<paramref name="x1"/>, <paramref name="z1"/>) are known (their areas filled); always
    /// without objects.
    /// </summary>
    public bool ObjectsKnown(int x0, int z0, int x1, int z1)
    {
        if (Objects is null)
        {
            return true;
        }

        // The voxels' columns in the objects' cells.
        var per = _world.Grid.BaseCentimetres / Objects.CellCentimetres;
        return Objects.Known(x0 * per, z0 * per, (x1 + 1) * per - 1, (z1 + 1) * per - 1);
    }

    /// <summary>
    /// The objects' cells a navigation cell about (<paramref name="x"/>, <paramref name="z"/>), absolute centimetres,
    /// reads: in cells finer than the world's voxels, those whose squares come nearer its centre than half a cell's width
    /// (every column of its square); else the voxels' own footprint, <paramref name="voxels"/>.
    /// </summary>
    private CircleFootprint ObjectFootprint(int x, int z, in CircleFootprint voxels)
    {
        var cell = Objects!.CellCentimetres;
        return cell < _world.Grid.BaseCentimetres
            ? new(x / (double)cell, z / (double)cell, NavGrid.CellCentimetres / 2.0 / cell)
            : voxels;
    }

    /// <summary>
    /// True when <paramref name="edit"/> clears only materials walkers pass through (a blast stripping leaves and
    /// flowers): it changes no floor or headroom, so its cells need neither reading again nor stamping (stamped from its
    /// shape, a clear of some materials counts as a dig of its whole shape). Any thread; allocates nothing.
    /// </summary>
    public bool ClearsOnlyPassable(in VoxelEdit edit)
    {
        if (edit.Operation != VoxelEditOperation.ClearMaterials)
        {
            return false;
        }

        for (var material = 1; material < VoxelEdit.MaterialSetSize; material++)
        {
            if (edit.ClearsMaterial((byte)material) && !_passable.Contains((byte)material))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the voxel column at the centre of <paramref name="cell"/> is loaded at level 0, and the objects that may
    /// stand in it are known.
    /// </summary>
    public bool IsResident(NavGrid grid, int cell)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var voxels = _world.Grid;
        var x = voxels.ToVoxel(grid.CentreX(grid.Column(cell)));
        var z = voxels.ToVoxel(grid.CentreZ(grid.Row(cell)));
        return _world.ColumnsResident(x, z, x, z) && ObjectsKnown(x, z, x, z);
    }

    /// <summary>
    /// Reads cell <paramref name="cell"/> again from the voxels, judged from the ground there (or its present floor where
    /// the game gives no ground); its class stays. Returns true when its floor or headroom changed. The cell's column must
    /// be resident (<see cref="IsResident"/>).
    /// </summary>
    public bool Rescan(NavGrid grid, int cell)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var x = grid.CentreX(grid.Column(cell));
        var z = grid.CentreZ(grid.Row(cell));
        // Heights here are centimetres; the world is read in its level-0 voxels: the one under the cell's centre.
        var voxels = _world.Grid;
        var footprint = new CircleFootprint(voxels.ToVoxel(x) + 0.5, voxels.ToVoxel(z) + 0.5, 0.5);
        var worldFloor = voxels.ToCentimetres(voxels.MinVoxelY);
        var objectFootprint = Objects is null ? default : ObjectFootprint(x, z, in footprint);
        var reference = grid.Floor[cell];
        var ground = Ground(x, z, reference);
        var limit = ground + LowCentimetres;
        var lowest = Math.Max(Math.Min(reference, ground) - DepthCentimetres, worldFloor);
        var top = HighestTop(in footprint, in objectFootprint, lowest, limit, out var fromWorld, out var fromObjects);
        if (top == int.MinValue && lowest > worldFloor)
        {
            // Dug deeper than the search reaches (one 5 m removal): down to the world's floor.
            top = HighestTop(in footprint, in objectFootprint, worldFloor, lowest - 1, out fromWorld, out fromObjects);
        }

        var surface = top == int.MinValue ? int.MinValue : SurfaceOver(x, top, z, fromWorld, fromObjects);
        int floor;
        byte clear;
        if (surface > limit)
        {
            // Solid reaches past the limit over the ground: too high to step onto, however it was built.
            floor = Math.Min(reference, ground);
            clear = 0;
        }
        else
        {
            // Nothing left under the cell (dug through the world's floor): it stands at the world's floor. The headroom
            // is looked for from the voxel over the highest blocking one, which the surface may lie under.
            var above = top == int.MinValue ? worldFloor : top;
            var standing = top == int.MinValue ? worldFloor : surface;
            var worldBottom = _world.LowestBottom(
                in footprint,
                voxels.ToVoxel(above),
                voxels.ToVoxel(above + NavGrid.MostClear - 1),
                in _passable
            );
            var bottom = worldBottom == int.MaxValue ? int.MaxValue : voxels.ToCentimetres(worldBottom);
            if (Objects is not null)
            {
                var objectCell = Objects.CellCentimetres;
                var objectBottom = Objects.LowestBottom(
                    in objectFootprint,
                    FloorDivide(above, objectCell),
                    FloorDivide(above + NavGrid.MostClear - 1, objectCell)
                );
                if (objectBottom != int.MaxValue)
                {
                    bottom = Math.Min(bottom, objectBottom * objectCell);
                }
            }

            var room = bottom == int.MaxValue ? NavGrid.MostClear : bottom - standing;
            floor = standing;
            clear = (byte)(Math.Clamp(room, 0, NavGrid.MostClear) / NavGrid.ClearUnit);
        }

        if (grid.Floor[cell] == floor && grid.Clear[cell] == clear)
        {
            return false;
        }

        grid.Floor[cell] = floor;
        grid.Clear[cell] = clear;
        return true;
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to the cells under it without reading voxels, from its shape alone, for edits whose
    /// voxels are not loaded (a save's edits at start): a dig (clear or lift, or a material walkers pass through set) that
    /// reaches a cell's floor lowers it to the dig's bottom, and one that reaches its headroom's top raises that; a clear
    /// of some materials digs so only when the set holds ground (<see cref="INavGridSource.IsGround"/>: soil; stamping a pit of its whole box
    /// whatever it cleared would be wrong) and changes nothing otherwise; stone set or filled from the floor up raises the floor when low enough over
    /// the ground to stand on and blocks the cell otherwise, and stone over the floor lowers the headroom; a fill of a
    /// material walkers pass through changes nothing. A piece put back counts as its bounding sphere filled; painting
    /// changes nothing. Only the cells of the grid's window are changed (an edit outside it changes none), and of those
    /// only the columns <paramref name="firstColumn"/> to <paramref name="lastColumn"/> and rows
    /// <paramref name="firstRow"/> to <paramref name="lastRow"/> when given (the strips a move of the window brings in).
    /// Reports each changed cell to <paramref name="changed"/>. Any thread that owns the grid (the source's ground is
    /// read on any thread); allocates nothing.
    /// </summary>
    public void Stamp(
        NavGrid grid,
        in VoxelEdit edit,
        Action<int>? changed = null,
        int firstColumn = 0,
        int firstRow = 0,
        int lastColumn = int.MaxValue,
        int lastRow = int.MaxValue
    )
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (!Effect(in edit, out var removes))
        {
            return;
        }

        edit.GetBounds(out var min, out var end);
        var columns = (
            First: Math.Max(firstColumn, NavGrid.FloorDivide(min.X - grid.OriginX, NavGrid.CellCentimetres)),
            Last: Math.Min(
                Math.Min(lastColumn, grid.Width - 1),
                NavGrid.FloorDivide(end.X - 1 - grid.OriginX, NavGrid.CellCentimetres)
            )
        );
        var rows = (
            First: Math.Max(firstRow, NavGrid.FloorDivide(min.Z - grid.OriginZ, NavGrid.CellCentimetres)),
            Last: Math.Min(
                Math.Min(lastRow, grid.Depth - 1),
                NavGrid.FloorDivide(end.Z - 1 - grid.OriginZ, NavGrid.CellCentimetres)
            )
        );
        for (var row = Math.Max(0, rows.First); row <= rows.Last; row++)
        {
            var z = grid.CentreZ(row);
            for (var column = Math.Max(0, columns.First); column <= columns.Last; column++)
            {
                var x = grid.CentreX(column);
                if (!Span(in edit, x, z, out var y0, out var y1))
                {
                    continue;
                }

                var cell = grid.Index(column, row);
                if (
                    removes
                        ? Remove(grid, cell, y0, y1)
                        : Add(grid, cell, y0, y1, Ground(x, z, grid.Floor[cell]), LowCentimetres)
                )
                {
                    changed?.Invoke(cell);
                }
            }
        }
    }

    /// <summary>
    /// True when <paramref name="edit"/>'s shape covers the voxel column at the centre of a cell of
    /// <paramref name="grid"/>: a read uses only that column, so an edit that covers none (most far thin channels, 2 to 3
    /// cm wide) can never change a cell, and needs neither reading nor stamping.
    /// Any thread; allocates nothing.
    /// </summary>
    public static bool CoversAnyCentre(NavGrid grid, in VoxelEdit edit)
    {
        ArgumentNullException.ThrowIfNull(grid);
        edit.GetBounds(out var min, out var end);
        if (min.X >= end.X || min.Z >= end.Z)
        {
            return false;
        }

        // The columns and rows whose centres lie within the edit's bounds.
        var firstColumn = Math.Max(
            0,
            CeilingDivide(min.X - grid.OriginX - NavGrid.CellCentimetres / 2, NavGrid.CellCentimetres)
        );
        var lastColumn = Math.Min(
            grid.Width - 1,
            FloorDivide(end.X - 1 - grid.OriginX - NavGrid.CellCentimetres / 2, NavGrid.CellCentimetres)
        );
        var firstRow = Math.Max(
            0,
            CeilingDivide(min.Z - grid.OriginZ - NavGrid.CellCentimetres / 2, NavGrid.CellCentimetres)
        );
        var lastRow = Math.Min(
            grid.Depth - 1,
            FloorDivide(end.Z - 1 - grid.OriginZ - NavGrid.CellCentimetres / 2, NavGrid.CellCentimetres)
        );
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                if (Span(in edit, grid.CentreX(column), grid.CentreZ(row), out _, out _))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// What a stamp of <paramref name="edit"/> does: false for nothing, else a dig (<paramref name="removes"/>) or solid
    /// added.
    /// </summary>
    private bool Effect(in VoxelEdit edit, out bool removes)
    {
        removes = false;
        switch (edit.Operation)
        {
            case VoxelEditOperation.Paint:
            case VoxelEditOperation.Reset:
            case VoxelEditOperation.RemovePart:
            case VoxelEditOperation.ClearCopies:
            case VoxelEditOperation.PlaceObject:
            case VoxelEditOperation.RemoveObject:
                return false;
            case VoxelEditOperation.Clear:
            case VoxelEditOperation.Lift:
                removes = true;
                return true;
            case VoxelEditOperation.ClearMaterials:
                removes = true;
                return ClearsGround(in edit);
            default:
                // A piece put back is a fill of air: its voxels are the piece's.
                if (edit.Shape == VoxelEditShape.Piece)
                {
                    return true;
                }

                if (_passable.Contains(edit.Material))
                {
                    // Set, it replaces what stood there with what walkers pass through; filled, it changes nothing.
                    removes = edit.Operation == VoxelEditOperation.Set;
                    return removes;
                }

                return true;
        }
    }

    /// <summary>True when a clear of some materials removes a material that is ground and that walkers do not pass through.</summary>
    private bool ClearsGround(in VoxelEdit edit)
    {
        for (var material = 1; material < VoxelEdit.MaterialSetSize; material++)
        {
            var value = (byte)material;
            if (edit.ClearsMaterial(value) && !_passable.Contains(value) && (_source?.IsGround(value) ?? true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The top, in centimetres, of the highest blocking voxel of the world or an object between the centimetres
    /// <paramref name="y0"/> and <paramref name="y1"/> under <paramref name="footprint"/> (in the world's level-0
    /// voxels) and <paramref name="objectFootprint"/> (in the objects' cells), or <see cref="int.MinValue"/> when there
    /// is none; <paramref name="fromWorld"/> and <paramref name="fromObjects"/> say whose top it is (both where they
    /// meet).
    /// </summary>
    private int HighestTop(
        in CircleFootprint footprint,
        in CircleFootprint objectFootprint,
        int y0,
        int y1,
        out bool fromWorld,
        out bool fromObjects
    )
    {
        var voxels = _world.Grid;
        var worldTop = _world.HighestTop(in footprint, voxels.ToVoxel(y0), voxels.ToVoxel(y1), in _passable);
        var worldCentimetres = worldTop == int.MinValue ? int.MinValue : voxels.ToCentimetres(worldTop);
        var objectCentimetres = int.MinValue;
        if (Objects is not null)
        {
            var objectCell = Objects.CellCentimetres;
            var objectTop = Objects.HighestTop(
                in objectFootprint,
                FloorDivide(y0, objectCell),
                FloorDivide(y1, objectCell)
            );
            objectCentimetres = objectTop == int.MinValue ? int.MinValue : objectTop * objectCell;
        }

        var top = Math.Max(worldCentimetres, objectCentimetres);
        fromWorld = top != int.MinValue && worldCentimetres == top;
        fromObjects = top != int.MinValue && objectCentimetres == top;
        return top;
    }

    /// <summary>
    /// The surface over a run of blocking voxels whose top is <paramref name="top"/> centimetres in the column at
    /// (<paramref name="x"/>, <paramref name="z"/>), in centimetres, rounded: the world's where its top is it, where the
    /// density crosses one half (<see cref="VoxelWorld.SurfaceOver"/>), from half a voxel under the top to half a voxel
    /// over it; an object's where its top is (the top of its highest voxel in the cell's square); the higher where both
    /// are.
    /// </summary>
    private int SurfaceOver(int x, int top, int z, bool fromWorld, bool fromObjects)
    {
        var voxels = _world.Grid;
        var surface = int.MinValue;
        if (fromWorld)
        {
            var vt = voxels.ToVoxel(top);
            var world = _world.SurfaceOver(voxels.ToVoxel(x), vt, voxels.ToVoxel(z), in _passable);
            surface = (int)Math.Round((double.IsNaN(world) ? vt : world) * voxels.BaseCentimetres);
        }

        return fromObjects ? Math.Max(surface, top) : surface;
    }

    /// <summary>
    /// Stamps the voxel objects the world places, as generated, onto the cells from columns
    /// <paramref name="firstColumn"/> to <paramref name="lastColumn"/> and rows <paramref name="firstRow"/> to
    /// <paramref name="lastRow"/> (inclusive), after a source filled them (<see cref="INavGridSource.Fill"/>, which knows
    /// only the ground): in the voxel column at each cell's centre, each run of an object's blocking voxels, as a read
    /// finds it, is solid added from its bottom to its surface, judged from the ground there as a stamped fill is: it
    /// blocks the cell where it rises more than <see cref="LowCentimetres"/> over the ground (a trunk), raises the floor
    /// where lower (a flat stone), and lowers the headroom where it hangs over the floor (a branch). The navigation
    /// thread; allocates only as its lists grow.
    /// </summary>
    internal void StampObjects(
        NavGrid grid,
        NavObjects objects,
        int firstColumn,
        int firstRow,
        int lastColumn,
        int lastRow
    )
    {
        var voxels = _world.Grid;
        var cell = objects.CellCentimetres;
        const int half = NavGrid.CellCentimetres / 2;
        var found = objects.Near(
            grid.CentreX(firstColumn),
            grid.CentreX(lastColumn),
            grid.CentreZ(firstRow),
            grid.CentreZ(lastRow)
        );
        foreach (var placement in found)
        {
            // The kind's reach, and half a voxel: a cell reads the voxel column holding its centre, whose own centre lies
            // up to half a voxel from it.
            var reach = objects.Kinds[placement.Kind].ReachCentimetres + cell / 2 + 1;
            var anchor = placement.Anchor;
            // The cells whose centres lie within its reach.
            var c0 = Math.Max(
                firstColumn,
                CeilingDivide(anchor.X - reach - grid.OriginX - half, NavGrid.CellCentimetres)
            );
            var c1 = Math.Min(lastColumn, FloorDivide(anchor.X + reach - grid.OriginX - half, NavGrid.CellCentimetres));
            var r0 = Math.Max(firstRow, CeilingDivide(anchor.Z - reach - grid.OriginZ - half, NavGrid.CellCentimetres));
            var r1 = Math.Min(lastRow, FloorDivide(anchor.Z + reach - grid.OriginZ - half, NavGrid.CellCentimetres));
            for (var row = r0; row <= r1; row++)
            {
                var z = grid.CentreZ(row);
                for (var column = c0; column <= c1; column++)
                {
                    var x = grid.CentreX(column);
                    var spans = objects.Spans(in placement, voxels.ToVoxel(x), voxels.ToVoxel(z));
                    if (spans.Count == 0)
                    {
                        continue;
                    }

                    var index = grid.Index(column, row);
                    var ground = Ground(x, z, grid.Floor[index]);
                    foreach (var span in spans)
                    {
                        // As a read judges it: a run whose bottom lies within the step over the ground stands on the
                        // floor (its bottom a whole voxel, the floor any centimetre), so it raises the floor or blocks.
                        var bottom = span.Lo * cell;
                        if (bottom <= ground + LowCentimetres)
                        {
                            bottom = Math.Min(bottom, grid.Floor[index]);
                        }

                        Add(grid, index, bottom, (int)Math.Round(span.Surface * cell), ground, LowCentimetres);
                    }
                }
            }
        }
    }

    /// <summary>The game's ground at column (x, z), or <paramref name="floor"/> where it gives none.</summary>
    private int Ground(int x, int z, int floor)
    {
        var ground = _source?.GroundAt(x, z) ?? int.MinValue;
        return ground == int.MinValue ? floor : ground;
    }

    /// <summary>The level-0 ys [y0, y1) the edit covers at column (x, z); false where it covers none.</summary>
    private static bool Span(in VoxelEdit edit, int x, int z, out int y0, out int y1)
    {
        if (edit.Shape == VoxelEditShape.Box)
        {
            y0 = edit.Origin.Y;
            y1 = edit.Origin.Y + edit.Extent.Y;
            return x >= edit.Origin.X
                && x < edit.Origin.X + edit.Extent.X
                && z >= edit.Origin.Z
                && z < edit.Origin.Z + edit.Extent.Z;
        }

        if (edit.Shape == VoxelEditShape.Capsule)
        {
            return CapsuleSpan(in edit, x, z, out y0, out y1);
        }

        long dx = x - edit.Origin.X;
        long dz = z - edit.Origin.Z;
        long r = edit.Radius;
        var room = r * r - dx * dx - dz * dz;
        if (room < 0)
        {
            y0 = y1 = 0;
            return false;
        }

        var half = (int)Math.Sqrt(room);
        y0 = edit.Origin.Y - half;
        y1 = edit.Origin.Y + half + 1;
        return true;
    }

    /// <summary>
    /// The level-0 ys [y0, y1) a capsule covers at column (x, z), by its exact test down its bounds (a capsule is convex,
    /// so the ys it covers there are one run); false where it covers none.
    /// </summary>
    private static bool CapsuleSpan(in VoxelEdit edit, int x, int z, out int y0, out int y1)
    {
        edit.GetBounds(out var min, out var end);
        y0 = y1 = 0;
        var found = false;
        for (var y = min.Y; y < end.Y; y++)
        {
            if (!edit.Contains(x, y, z))
            {
                if (found)
                {
                    break;
                }

                continue;
            }

            if (!found)
            {
                y0 = y;
                found = true;
            }

            y1 = y + 1;
        }

        return found;
    }

    private static bool Remove(NavGrid grid, int cell, int y0, int y1)
    {
        int floor = grid.Floor[cell];
        var blocked = grid.IsBlocked(cell);
        var ceiling = floor + grid.ClearCentimetres(cell);
        if (y0 < floor && y1 >= floor)
        {
            // Dug into the ground under the floor: the floor drops to the dig's bottom.
            var drop = floor - y0;
            floor = y0;
            ceiling += drop;
        }

        if (blocked)
        {
            // What stood on the floor counts as dug away only where one dig reaches from the floor to 2.55 m over it;
            // smaller digs through a wall are found when the voxels are read again.
            if (y0 > floor || y1 < floor + NavGrid.MostClear / 2)
            {
                return Write(grid, cell, floor, 0, false);
            }

            ceiling = y1;
        }
        else if (y0 <= ceiling && y1 > ceiling)
        {
            ceiling = y1;
        }

        return Write(grid, cell, floor, ceiling - floor, true);
    }

    private static bool Add(NavGrid grid, int cell, int y0, int y1, int ground, int lowCentimetres)
    {
        if (grid.IsBlocked(cell))
        {
            return false;
        }

        int floor = grid.Floor[cell];
        var ceiling = floor + grid.ClearCentimetres(cell);
        if (y1 <= floor || y0 >= ceiling)
        {
            return false;
        }

        if (y0 <= floor + 1)
        {
            // Judged from the ground, as a read is: stacked stamps block as one would.
            if (y1 - ground > lowCentimetres)
            {
                return Write(grid, cell, floor, 0, false);
            }

            return Write(grid, cell, y1, ceiling - y1, true);
        }

        return Write(grid, cell, floor, y0 - floor, y0 - floor >= NavGrid.ClearUnit);
    }

    private static int FloorDivide(int value, int divisor) =>
        value >= 0 ? value / divisor : -((-value + divisor - 1) / divisor);

    private static int CeilingDivide(int value, int divisor) => -FloorDivide(-value, divisor);

    private static bool Write(NavGrid grid, int cell, int floor, int room, bool open)
    {
        var clear = open ? (byte)Math.Max(1, Math.Clamp(room, 0, NavGrid.MostClear) / NavGrid.ClearUnit) : (byte)0;
        if (grid.Floor[cell] == floor && grid.Clear[cell] == clear)
        {
            return false;
        }

        grid.Floor[cell] = floor;
        grid.Clear[cell] = clear;
        return true;
    }
}
