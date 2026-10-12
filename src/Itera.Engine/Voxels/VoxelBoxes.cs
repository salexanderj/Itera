using System.Numerics;

namespace Itera.Engine.Voxels;

/// <summary>A box of level-0 voxels from (X0, Y0, Z0) to (X1, Y1, Z1), inclusive.</summary>
internal readonly record struct SolidBox(int X0, int Y0, int Z0, int X1, int Y1, int Z1)
{
    public int SizeX => X1 - X0 + 1;

    public int SizeY => Y1 - Y0 + 1;

    public int SizeZ => Z1 - Z0 + 1;

    public long Voxels => (long)SizeX * SizeY * SizeZ;
}

/// <summary>
/// Turns the blocking voxels of a region into boxes that do not overlap and cover them exactly, for Bepu shapes. A part
/// of the tree that is all one blocking material is one box. A mixed brick becomes runs along x in each row, runs equal
/// in x merged upwards in y, then rectangles equal in x and y merged along z, so a flat surface or a wall gives a few
/// boxes per brick. Boxes are not merged across bricks. Main thread; reuses its arrays, so building allocates only when
/// a region holds more boxes than any before it.
/// </summary>
internal sealed class VoxelBoxes
{
    // A brick layer holds at most 4 runs per row (alternate voxels) in 8 rows.
    private const int MaxRectangles = 32;

    private readonly Rectangle[] _layer = new Rectangle[MaxRectangles];
    private readonly Rectangle[] _open = new Rectangle[MaxRectangles];
    private readonly Rectangle[] _next = new Rectangle[MaxRectangles];
    private readonly int[] _openZ = new int[MaxRectangles];
    private readonly int[] _nextZ = new int[MaxRectangles];
    private SolidBox[] _boxes = new SolidBox[1024];

    /// <summary>Boxes found by the last build.</summary>
    public int Count { get; private set; }

    /// <summary>The boxes found by the last build.</summary>
    public ReadOnlySpan<SolidBox> Boxes => _boxes.AsSpan(0, Count);

    /// <summary>
    /// Finds the boxes of the blocking voxels from (x0, y0, z0) to (x1, y1, z1), inclusive, that are not in
    /// <paramref name="passable"/>. Columns that are not resident give none.
    /// </summary>
    public void Build(VoxelWorld world, int x0, int y0, int z0, int x1, int y1, int z1, in MaterialSet passable) =>
        Build(world, 0, x0, y0, z0, x1, y1, z1, in passable);

    /// <summary>
    /// <see cref="Build(VoxelWorld, int, int, int, int, int, int, in MaterialSet)"/> in the voxels of
    /// <paramref name="level"/>: the corners and the boxes are in that level's voxels.
    /// </summary>
    public void Build(
        VoxelWorld world,
        int level,
        int x0,
        int y0,
        int z0,
        int x1,
        int y1,
        int z1,
        in MaterialSet passable
    )
    {
        Count = 0;
        var visitor = new Visitor(this);
        world.WalkBlocking(level, x0, y0, z0, x1, y1, z1, in passable, ref visitor);
    }

    /// <summary>
    /// Finds the boxes of a piece's blocking voxels that are not in <paramref name="passable"/>, in the piece's own
    /// voxels (0 to its size less one).
    /// </summary>
    public void Build(VoxelMemory memory, in VoxelPiece piece, in MaterialSet passable)
    {
        Count = 0;
        var visitor = new Visitor(this);
        VoxelWorld.WalkTree(
            memory,
            piece.Tree,
            piece.Size.X - 1,
            piece.Size.Y - 1,
            piece.Size.Z - 1,
            in passable,
            ref visitor
        );
    }

    /// <summary>
    /// Finds the boxes of a voxel object kind's blocking voxels that are not in <paramref name="excluded"/>, in the
    /// kind's own voxels (0 to its size less one): each part's own voxels, brick by brick (a part's bricks lie on the
    /// kind's 8-voxel lattice).
    /// </summary>
    public void Build(Objects.VoxelObjectKind kind, in MaterialSet excluded)
    {
        Count = 0;
        const int border = Objects.ObjectPart.BorderVoxels;
        const int brick = VoxelConstants.BrickSize;
        Span<ulong> mask = stackalloc ulong[VoxelConstants.BrickMaskWords];
        foreach (var part in kind.Parts)
        {
            var level = part.Levels[0];
            var own = part.Offset;
            var end = new VoxelCoord(own.X + part.OwnSize.X, own.Y + part.OwnSize.Y, own.Z + part.OwnSize.Z);
            for (var b = 0; b < level.Bricks.Length; b++)
            {
                var packed = level.Bricks[b];
                int bx = ((packed & 15) << 3) - border + own.X,
                    by = (((packed >> 4) & 15) << 3) - border + own.Y,
                    bz = (((packed >> 8) & 15) << 3) - border + own.Z;
                var payload = level.PayloadStart[b];
                var uniform = level.Uniform[b];
                if (
                    uniform
                    && bx >= own.X
                    && by >= own.Y
                    && bz >= own.Z
                    && bx + brick <= end.X
                    && by + brick <= end.Y
                    && bz + brick <= end.Z
                )
                {
                    var material = level.Payload[payload];
                    if (material != VoxelConstants.Air && !excluded.Contains(material))
                    {
                        Add(bx, by, bz, bx + brick - 1, by + brick - 1, bz + brick - 1);
                    }

                    continue;
                }

                mask.Clear();
                var any = false;
                for (var j = 0; j < brick * brick * brick; j++)
                {
                    int x = bx + (j & 7),
                        y = by + ((j >> 3) & 7),
                        z = bz + (j >> 6);
                    if (x < own.X || y < own.Y || z < own.Z || x >= end.X || y >= end.Y || z >= end.Z)
                    {
                        continue;
                    }

                    var material = uniform ? level.Payload[payload] : level.Payload[payload + j];
                    if (material != VoxelConstants.Air && !excluded.Contains(material))
                    {
                        mask[j >> 6] |= 1UL << (j & 63);
                        any = true;
                    }
                }

                if (any)
                {
                    AddBrick(bx, by, bz, mask);
                }
            }
        }
    }

    private void Add(int x0, int y0, int z0, int x1, int y1, int z1)
    {
        if (Count == _boxes.Length)
        {
            Array.Resize(ref _boxes, _boxes.Length * 2);
        }

        _boxes[Count++] = new SolidBox(x0, y0, z0, x1, y1, z1);
    }

    /// <summary>Adds the boxes of a brick whose lowest voxel is (x, y, z); mask word z holds layer z, bit x + 8y.</summary>
    private void AddBrick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask)
    {
        var open = 0;
        for (var layer = 0; layer <= VoxelConstants.BrickMaskWords; layer++)
        {
            var rectangles = layer < VoxelConstants.BrickMaskWords ? Rectangles(mask[layer]) : 0;

            // A rectangle equal to one open from the layers below extends it; the others start new boxes.
            var next = 0;
            for (var i = 0; i < rectangles; i++)
            {
                var rectangle = _layer[i];
                var start = layer;
                for (var j = 0; j < open; j++)
                {
                    if (_open[j] == rectangle)
                    {
                        start = _openZ[j];
                        _openZ[j] = -1;
                        break;
                    }
                }

                _next[next] = rectangle;
                _nextZ[next++] = start;
            }

            // Open boxes that did not continue end in the layer below.
            for (var j = 0; j < open; j++)
            {
                if (_openZ[j] >= 0)
                {
                    var box = _open[j];
                    Add(x + box.X0, y + box.Y0, z + _openZ[j], x + box.X1, y + box.Y1, z + layer - 1);
                }
            }

            _next.AsSpan(0, next).CopyTo(_open);
            _nextZ.AsSpan(0, next).CopyTo(_openZ);
            open = next;
        }
    }

    /// <summary>Writes the rectangles of one brick layer (bit x + 8y) into the layer array and returns how many.</summary>
    private int Rectangles(ulong word)
    {
        var count = 0;
        for (var row = 0; row < VoxelConstants.BrickSize; row++)
        {
            var bits = (int)((word >> (8 * row)) & 0xFF);
            // Each run of set bits in the row continues the rectangle that ended in the row below with the same run, or
            // starts one. Runs in a row do not overlap, so no rectangle is continued twice.
            while (bits != 0)
            {
                var from = BitOperations.TrailingZeroCount(bits);
                var to = from;
                while (to < 7 && ((bits >> (to + 1)) & 1) != 0)
                {
                    to++;
                }

                bits &= ~(((1 << (to - from + 1)) - 1) << from);
                var continued = false;
                for (var i = 0; i < count; i++)
                {
                    if (_layer[i].X0 == from && _layer[i].X1 == to && _layer[i].Y1 == row - 1)
                    {
                        _layer[i] = _layer[i] with { Y1 = row };
                        continued = true;
                        break;
                    }
                }

                if (!continued)
                {
                    _layer[count++] = new Rectangle(from, row, to, row);
                }
            }
        }

        return count;
    }

    private readonly record struct Rectangle(int X0, int Y0, int X1, int Y1);

    private readonly struct Visitor : IBlockingVisitor
    {
        private readonly VoxelBoxes _boxes;

        public Visitor(VoxelBoxes boxes)
        {
            _boxes = boxes;
        }

        public void Box(int x0, int y0, int z0, int x1, int y1, int z1) => _boxes.Add(x0, y0, z0, x1, y1, z1);

        public void Brick(int x, int y, int z, scoped ReadOnlySpan<ulong> mask) => _boxes.AddBrick(x, y, z, mask);

        public bool Finished => false;
    }
}
