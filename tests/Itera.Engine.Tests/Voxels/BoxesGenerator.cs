using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>A box of level-0 voxels from (X0, Y0, Z0) to (X1, Y1, Z1), inclusive, filled with one material.</summary>
internal readonly record struct VoxelBox(int X0, int Y0, int Z0, int X1, int Y1, int Z1, byte Material)
{
    public bool Contains(int x, int y, int z) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1 && z >= Z0 && z <= Z1;
}

/// <summary>
/// A generator for collision tests: dirt below <see cref="Floor"/> and air above, then the boxes in order, each
/// overwriting what is there (a box of air digs a pit). A box of <see cref="Pattern"/> holds a mix of up to 23
/// materials, one of them <see cref="Leaves"/>, so its bricks store one byte per voxel. Level 0 only matters to these
/// tests; coarser levels are sampled at voxel centres like every generator.
/// </summary>
internal sealed class BoxesGenerator : IChunkGenerator
{
    public const byte Dirt = 2;
    public const byte Stone = 3;
    public const byte Leaves = 6;
    public const byte Mushroom = 32;

    /// <summary>A box material that stands for a mix of materials.</summary>
    public const byte Pattern = 255;

    private readonly VoxelBox[] _boxes;

    public BoxesGenerator(int floor, params VoxelBox[] boxes)
    {
        Floor = floor;
        _boxes = boxes;
    }

    /// <summary>First level-0 y above the dirt.</summary>
    public int Floor { get; }

    public string Id { get; init; } = "test.boxes";

    public int Version => 1;

    /// <summary>The material at level-0 voxel (x, y, z).</summary>
    public byte Material(int x, int y, int z)
    {
        var material = y < Floor ? Dirt : VoxelConstants.Air;
        foreach (var box in _boxes)
        {
            if (box.Contains(x, y, z))
            {
                material = box.Material == Pattern ? PatternMaterial(x, y, z) : box.Material;
            }
        }

        return material;
    }

    public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
    {
        var edge = VoxelConstants.ChunkSize << column.Level;
        int x0 = column.X * edge,
            z0 = column.Z * edge;
        for (var layer = 0; layer < layers.Length; layer++)
        {
            var y0 = column.Chunk(layer, VoxelGrid.Unit).Y * edge;
            var touched = false;
            foreach (var box in _boxes)
            {
                touched |=
                    box.X0 < x0 + edge
                    && box.X1 >= x0
                    && box.Y0 < y0 + edge
                    && box.Y1 >= y0
                    && box.Z0 < z0 + edge
                    && box.Z1 >= z0;
            }

            layers[layer] =
                touched ? ChunkFill.Mixed
                : y0 + edge <= Floor ? ChunkFill.Uniform(Dirt)
                : y0 >= Floor ? ChunkFill.Air
                : ChunkFill.Mixed;
        }
    }

    public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels)
    {
        var level = chunk.Level;
        int ox = chunk.X << VoxelConstants.ChunkShift,
            oy = chunk.Y << VoxelConstants.ChunkShift,
            oz = chunk.Z << VoxelConstants.ChunkShift;
        const int Size = VoxelConstants.ChunkSize;
        for (var z = 0; z < Size; z++)
        {
            for (var y = 0; y < Size; y++)
            {
                var material = VoxelConstants.SampleAt(oy + y, level) < Floor ? Dirt : VoxelConstants.Air;
                voxels.Slice((z * Size + y) * Size, Size).Fill(material);
            }
        }

        // Each box over what is there, in order, only where it meets the chunk.
        foreach (var box in _boxes)
        {
            int x0 = First(box.X0, ox, level),
                x1 = Last(box.X1, ox, level),
                y0 = First(box.Y0, oy, level),
                y1 = Last(box.Y1, oy, level),
                z0 = First(box.Z0, oz, level),
                z1 = Last(box.Z1, oz, level);
            for (var z = z0; z <= z1; z++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    var row = voxels.Slice((z * Size + y) * Size, Size);
                    for (var x = x0; x <= x1; x++)
                    {
                        row[x] =
                            box.Material == Pattern
                                ? PatternMaterial(
                                    VoxelConstants.SampleAt(ox + x, level),
                                    VoxelConstants.SampleAt(oy + y, level),
                                    VoxelConstants.SampleAt(oz + z, level)
                                )
                                : box.Material;
                    }
                }
            }
        }
    }

    /// <summary>The first chunk index, 0 to 127, whose sample point is at or after <paramref name="low"/>.</summary>
    private static int First(int low, int origin, int level)
    {
        var index = 0;
        while (index < VoxelConstants.ChunkSize && VoxelConstants.SampleAt(origin + index, level) < low)
        {
            index++;
        }

        return index;
    }

    /// <summary>The last chunk index, -1 to 127, whose sample point is at or before <paramref name="high"/>.</summary>
    private static int Last(int high, int origin, int level)
    {
        var index = VoxelConstants.ChunkSize - 1;
        while (index >= 0 && VoxelConstants.SampleAt(origin + index, level) > high)
        {
            index--;
        }

        return index;
    }

    /// <summary>Air, leaves, or one of 21 other materials, changing from voxel to voxel.</summary>
    private static byte PatternMaterial(int x, int y, int z)
    {
        var value = (((x + 2 * y + 3 * z) % 23) + 23) % 23;
        return value == 0 ? VoxelConstants.Air
            : value == 1 ? Leaves
            : (byte)(100 + value);
    }
}
