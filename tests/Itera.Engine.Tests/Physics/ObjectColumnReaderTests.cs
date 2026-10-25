using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// One object's columns as the mover and navigation read them (<see cref="ObjectColumnReader"/>), from its kind's columns'
/// runs (<see cref="ObjectColumnRuns"/>): the runs against every voxel of the kind's shape, across its parts; the reader's
/// spans against a cell-by-cell reference, for cells as wide as the object's voxels, wider, and narrower, at four
/// headings, an anchor off the cells, a box reaching below its anchor, and passable voxels; a damaged object read through
/// its damage alike; and the runs made once for each set of passable materials.
/// </summary>
public sealed class ObjectColumnReaderTests
{
    private const byte Stone = 20;
    private const byte Metal = 21;
    private const byte Leaves = 22;
    private static readonly int[] s_headings = [0, 700, 1024, 2900];
    private readonly VoxelObjectKinds _kinds = new();
    private readonly VoxelObjectShape _wideShape = WideShape();
    private readonly VoxelObjectShape _oddShape = OddShape();
    private readonly VoxelObjectKindId _wide;
    private readonly VoxelObjectKindId _odd;
    private readonly MaterialSet _leavesPass = MaterialSet.Passable(new LeavesPass());
    private readonly MaterialSet _nothingPasses = MaterialSet.Passable(new NothingPassable());

    public ObjectColumnReaderTests()
    {
        _wide = _kinds.Register("wide", _wideShape);
        _odd = _kinds.Register("odd", _oddShape);
    }

    [Fact]
    public void Runs_are_each_columns_solid_voxels_across_the_kinds_parts()
    {
        Assert.True(_kinds[_wide].PartCount >= 4);
        foreach (var (id, shape) in new[] { (_wide, _wideShape), (_odd, _oddShape) })
        {
            var kind = _kinds[id];
            foreach (var passable in new[] { _leavesPass, _nothingPasses })
            {
                var runs = kind.ColumnRuns(in passable);
                for (var z = 0; z < kind.Size.Z; z++)
                {
                    for (var x = 0; x < kind.Size.X; x++)
                    {
                        var column = x + kind.Size.X * z;
                        Assert.Equal(
                            ReferenceRuns(shape, x, z, m => m != VoxelConstants.Air && !passable.Contains(m)),
                            runs.Blocking(column).ToArray()
                        );
                        Assert.Equal(
                            ReferenceRuns(shape, x, z, m => m != VoxelConstants.Air),
                            runs.Solid(column).ToArray()
                        );
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(4, 13, -37, 29)]
    [InlineData(10, 13, -37, 29)]
    [InlineData(3, -250, 101, 77)]
    public void A_wide_objects_spans_are_its_cells_whose_centres_land_on_its_blocking_voxels(
        int cell,
        int x,
        int y,
        int z
    ) => AssertSpans(_wide, cell, new VoxelCoord(x, y, z), damage: null);

    [Theory]
    [InlineData(7, 0, 0, 0)]
    [InlineData(10, 33, -5, -71)]
    [InlineData(2, 33, 11, -71)]
    public void An_odd_objects_spans_reaching_below_its_anchor_are_read_alike(int cell, int x, int y, int z) =>
        AssertSpans(_odd, cell, new VoxelCoord(x, y, z), damage: null);

    [Fact]
    public void A_damaged_objects_spans_are_read_through_its_damage()
    {
        // The cell widths are covered by the undamaged rows; the damage takes the same path in each.
        const int cell = 3;
        var kind = _kinds[_wide];
        // A crater of 1 m round its middle, and its leaves in a box painted to stone (blocking now).
        var dug = new DugVoxels(kind, centre: (60, 60, 8), radius: 25, paintFrom: (20, 10, 0), paintTo: (100, 110, 16));
        AssertSpans(_wide, cell, new VoxelCoord(13, -37, 29), dug);
    }

    [Fact]
    public void Runs_are_made_once_for_each_set_of_passable_materials()
    {
        var kind = _kinds[_odd];
        var leaves = kind.ColumnRuns(in _leavesPass);

        Assert.Same(leaves, kind.ColumnRuns(MaterialSet.Passable(new LeavesPass())));
        Assert.NotSame(leaves, kind.ColumnRuns(in _nothingPasses));
        Assert.Same(leaves, kind.ColumnRuns(in _leavesPass));
    }

    /// <summary>Every column round the object at each heading, against the cell-by-cell reference.</summary>
    private void AssertSpans(VoxelObjectKindId id, int cell, VoxelCoord anchor, DugVoxels? damage)
    {
        var kind = _kinds[id];
        var reader = new ObjectColumnReader(_leavesPass, cell);
        IObjectVoxelSource voxels = (IObjectVoxelSource?)damage ?? kind;
        var spans = new List<ObjectSpan>();
        var expected = new List<ObjectSpan>();
        var read = 0;
        foreach (var heading in s_headings)
        {
            var placement = new ObjectPlacement(new ObjectId(1), id, anchor, heading);
            var x0 = (int)DeterministicMath.FloorDivide((long)anchor.X - kind.ReachCentimetres, cell) - 1;
            var x1 = (int)DeterministicMath.FloorDivide((long)anchor.X + kind.ReachCentimetres, cell) + 1;
            var z0 = (int)DeterministicMath.FloorDivide((long)anchor.Z - kind.ReachCentimetres, cell) - 1;
            var z1 = (int)DeterministicMath.FloorDivide((long)anchor.Z + kind.ReachCentimetres, cell) + 1;
            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    spans.Clear();
                    reader.AddColumn(in placement, kind, voxels, damage is not null, x, z, spans);
                    Reference(in placement, kind, voxels, cell, x, z, expected);
                    Assert.Equal(expected, spans);
                    read += spans.Count;
                }
            }
        }

        // Many columns hold several runs.
        Assert.True(read > 1000, $"{read} spans read");
    }

    /// <summary>
    /// The cells of column (x, z) whose centres, turned into the object, land on a voxel of <paramref name="voxels"/> that
    /// is solid and not leaves, read one by one over the kind's box, each run with its surface and material.
    /// </summary>
    private static void Reference(
        in ObjectPlacement placement,
        VoxelObjectKind kind,
        IObjectVoxelSource voxels,
        int cell,
        int x,
        int z,
        List<ObjectSpan> spans
    )
    {
        spans.Clear();
        var anchor = placement.Anchor;
        var unit = kind.UnitCentimetres;
        var (ox, oz) = ObjectTurns.ToObject(
            placement.Heading,
            x * (long)cell + cell / 2 - anchor.X,
            z * (long)cell + cell / 2 - anchor.Z
        );
        var vx = (int)DeterministicMath.FloorDivide(ox + kind.AnchorCentimetres.X, unit);
        var vz = (int)DeterministicMath.FloorDivide(oz + kind.AnchorCentimetres.Z, unit);
        if (vx < 0 || vz < 0 || vx >= kind.Size.X || vz >= kind.Size.Z)
        {
            return;
        }

        int VoxelY(long centimetres) =>
            (int)DeterministicMath.FloorDivide(centimetres - anchor.Y + kind.AnchorCentimetres.Y, unit);
        bool Blocks(int vy)
        {
            voxels.Voxel(vx, vy, vz, out var material, out _);
            return material is not VoxelConstants.Air and not Leaves;
        }

        var y0 = (int)DeterministicMath.FloorDivide((long)anchor.Y + kind.BottomCentimetres, cell);
        var y1 = (int)DeterministicMath.FloorDivide((long)anchor.Y + kind.TopCentimetres - 1, cell);
        var start = int.MinValue;
        for (var y = y0; y <= y1 + 1; y++)
        {
            var solid = y <= y1 && Blocks(VoxelY((long)y * cell + cell / 2));
            if (solid && start == int.MinValue)
            {
                start = y;
            }
            else if (!solid && start != int.MinValue)
            {
                // Its surface: over the highest blocking voxel under the top cell's top, where the density crosses one
                // half on the way to the voxel above (that voxel's top where the voxel above blocks), from the top cell's
                // bottom to half a cell over its top.
                var hi = y - 1;
                var surface = (float)(hi + 1);
                voxels.Voxel(vx, VoxelY((long)hi * cell + cell / 2), vz, out var material, out _);
                for (var vy = VoxelY((long)(hi + 1) * cell - 1); vy >= VoxelY((long)hi * cell); vy--)
                {
                    if (Blocks(vy))
                    {
                        voxels.Voxel(vx, vy, vz, out material, out var low);
                        double over = vy + 1;
                        if (!Blocks(vy + 1))
                        {
                            voxels.Voxel(vx, vy + 1, vz, out var above, out var high);
                            high = above == VoxelConstants.Air ? high : (byte)0;
                            over = vy + 0.5 + (low - 127.5) / (low - (double)high);
                        }

                        var top = (over * unit - kind.AnchorCentimetres.Y + anchor.Y) / cell;
                        surface = (float)Math.Clamp(top, hi, hi + 1.5);
                        break;
                    }
                }

                spans.Add(new ObjectSpan(start, hi, surface, material));
                start = int.MinValue;
            }
        }
    }

    private static VoxelRun[] ReferenceRuns(VoxelObjectShape shape, int x, int z, Func<byte, bool> counts)
    {
        var runs = new List<VoxelRun>();
        var start = -1;
        for (var y = 0; y <= shape.Size.Y; y++)
        {
            var solid = y < shape.Size.Y && counts(shape.MaterialAt(x, y, z));
            if (solid && start < 0)
            {
                start = y;
            }
            else if (!solid && start >= 0)
            {
                runs.Add(new VoxelRun(start, y - 1));
                start = -1;
            }
        }

        return [.. runs];
    }

    /// <summary>
    /// 4 cm voxels, 4.8 m by 4.7 m by 64 cm, its anchor at its floor's middle, in four parts: a stone floor, a metal wall
    /// along one side, stone shelves a voxel thick, a cloud of leaves, a hollow, and scattered single voxels.
    /// </summary>
    private static VoxelObjectShape WideShape()
    {
        var shape = new VoxelObjectShape(4, new VoxelCoord(120, 118, 16), new VoxelCoord(240, 0, 32));
        shape.FillBox(0, 0, 0, 480, 12, 64, Stone);
        shape.FillBox(0, 0, 0, 480, 472, 8, Metal);
        for (var k = 1; k <= 5; k++)
        {
            shape.FillBox(0, k * 80, 0, 480, k * 80 + 4, 64, Stone);
        }

        shape.FillEllipsoid(240, 250, 36, 150, 120, 26, Leaves);
        shape.FillBox(300, 20, 10, 440, 300, 60, Metal);
        for (var y = 10; y < 70; y++)
        {
            for (var z = 5; z < 13; z++)
            {
                for (var x = 80; x < 105; x++)
                {
                    shape.Clear(x, y, z);
                }
            }
        }

        var seed = 12345u;
        for (var i = 0; i < 4000; i++)
        {
            seed = seed * 1664525 + 1013904223;
            int x = (int)(seed >> 8) % 120,
                y = (int)(seed >> 3) % 118,
                z = (int)(seed >> 20) % 16;
            shape.Set(
                x,
                y,
                z,
                i % 3 == 0 ? Leaves
                    : i % 3 == 1 ? Metal
                    : VoxelConstants.Air,
                i % 3 == 2 ? (byte)0 : (byte)255
            );
        }

        return shape;
    }

    /// <summary>7 cm voxels, its box starting 70 cm below its anchor: a stone egg with leaves through it and a gap.</summary>
    private static VoxelObjectShape OddShape()
    {
        var shape = new VoxelObjectShape(7, new VoxelCoord(30, 40, 20), new VoxelCoord(105, 70, 70));
        shape.FillEllipsoid(105, 140, 70, 100, 130, 65, Stone);
        for (var y = 0; y < 40; y++)
        {
            for (var z = 0; z < 20; z++)
            {
                for (var x = 0; x < 30; x++)
                {
                    if (y is >= 10 and < 20 && (x + z) % 5 == 0 && shape.MaterialAt(x, y, z) != VoxelConstants.Air)
                    {
                        shape.Set(x, y, z, Leaves, 255);
                    }
                    else if (y == 25 && x < 15)
                    {
                        shape.Clear(x, y, z);
                    }
                }
            }
        }

        return shape;
    }

    /// <summary>A kind's voxels with a sphere taken away and its leaves in a box painted to stone, as damage leaves them.</summary>
    private sealed class DugVoxels(
        VoxelObjectKind kind,
        (int X, int Y, int Z) centre,
        int radius,
        (int X, int Y, int Z) paintFrom,
        (int X, int Y, int Z) paintTo
    ) : IObjectVoxelSource
    {
        public void Voxel(int x, int y, int z, out byte material, out byte density)
        {
            kind.Voxel(x, y, z, out material, out density);
            if (material == VoxelConstants.Air)
            {
                return;
            }

            int dx = x - centre.X,
                dy = y - centre.Y,
                dz = z - centre.Z;
            if (dx * dx + dy * dy + dz * dz <= radius * radius)
            {
                material = VoxelConstants.Air;
                density = 0;
            }
            else if (
                material == Leaves
                && x >= paintFrom.X
                && x < paintTo.X
                && y >= paintFrom.Y
                && y < paintTo.Y
                && z >= paintFrom.Z
                && z < paintTo.Z
            )
            {
                material = Stone;
            }
        }
    }

    private sealed class LeavesPass : IVoxelPassability
    {
        public bool IsPassable(byte material) => material == Leaves;
    }
}
