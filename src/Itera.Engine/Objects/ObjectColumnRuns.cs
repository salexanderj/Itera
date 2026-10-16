using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Objects;

/// <summary>
/// A kind's voxel columns as runs, for one set of passable materials: for each of its columns (index
/// <c>x + Size.X * z</c>), lowest first, the runs of its solid voxels that are not passable (<see cref="Blocking"/>) and
/// of all its solid voxels (<see cref="Solid"/>), each from its lowest voxel to its highest. Made from its parts' bricks,
/// a whole brick at a time where it is one material, once a run (<see cref="VoxelObjectKind.ColumnRuns"/>), and read on
/// any thread: the mover's and navigation's readings of the objects take their columns from them
/// (<c>Physics.ObjectColumnReader</c>).
/// </summary>
internal sealed class ObjectColumnRuns
{
    private const int Own = ObjectPart.OwnVoxels;
    private const int Brick = VoxelConstants.BrickSize;

    private readonly int[] _blockingStarts;
    private readonly VoxelRun[] _blocking;
    private readonly int[] _solidStarts;
    private readonly VoxelRun[] _solid;

    private ObjectColumnRuns(
        in MaterialSet passable,
        int[] blockingStarts,
        VoxelRun[] blocking,
        int[] solidStarts,
        VoxelRun[] solid
    )
    {
        Passable = passable;
        _blockingStarts = blockingStarts;
        _blocking = blocking;
        _solidStarts = solidStarts;
        _solid = solid;
    }

    /// <summary>The materials passed through, which <see cref="Blocking"/> leaves out.</summary>
    public MaterialSet Passable { get; }

    /// <summary>The runs of column <paramref name="column"/>'s solid voxels that are not passable, lowest first.</summary>
    public ReadOnlySpan<VoxelRun> Blocking(int column) =>
        _blocking.AsSpan(_blockingStarts[column], _blockingStarts[column + 1] - _blockingStarts[column]);

    /// <summary>The runs of column <paramref name="column"/>'s solid voxels, passable ones too, lowest first.</summary>
    public ReadOnlySpan<VoxelRun> Solid(int column) =>
        _solid.AsSpan(_solidStarts[column], _solidStarts[column + 1] - _solidStarts[column]);

    /// <summary>
    /// Reads <paramref name="parts"/>' own voxels (not their borders) into the runs of a kind of <paramref name="size"/>
    /// voxels, with <paramref name="passable"/> passed through, one stack of parts over a 112-voxel square at a time.
    /// </summary>
    internal static ObjectColumnRuns Build(
        VoxelCoord size,
        ObjectPart[] parts,
        in MaterialSet kindMaterials,
        in MaterialSet passable
    )
    {
        // Each column's voxels as bits, one stack of parts at a time; blocking ones apart only where some are passable.
        var split = kindMaterials.Overlaps(passable);
        var words = (size.Y + 63) >> 6;
        var solidBits = new ulong[Own * Own * words];
        var blockingBits = split ? new ulong[Own * Own * words] : solidBits;
        var solid = new List<(int Column, VoxelRun Run)>();
        var blocking = split ? new List<(int Column, VoxelRun Run)>() : solid;
        var stacks = new Dictionary<(int X, int Z), List<ObjectPart>>();
        foreach (var part in parts)
        {
            if (!stacks.TryGetValue((part.Offset.X, part.Offset.Z), out var stack))
            {
                stack = [];
                stacks.Add((part.Offset.X, part.Offset.Z), stack);
            }

            stack.Add(part);
        }

        foreach (var stack in stacks.Values)
        {
            Array.Clear(solidBits);
            if (split)
            {
                Array.Clear(blockingBits);
            }

            foreach (var part in stack)
            {
                AddPart(part, words, solidBits, blockingBits, in passable);
            }

            var first = stack[0];
            for (var z = 0; z < first.OwnSize.Z; z++)
            {
                for (var x = 0; x < first.OwnSize.X; x++)
                {
                    var column = first.Offset.X + x + size.X * (first.Offset.Z + z);
                    var at = (x + Own * z) * words;
                    AddRuns(solidBits.AsSpan(at, words), size.Y, column, solid);
                    if (split)
                    {
                        AddRuns(blockingBits.AsSpan(at, words), size.Y, column, blocking);
                    }
                }
            }
        }

        var columns = size.X * size.Z;
        var (solidStarts, solidRuns) = Gather(solid, columns);
        var (blockingStarts, blockingRuns) = split ? Gather(blocking, columns) : (solidStarts, solidRuns);
        return new ObjectColumnRuns(in passable, blockingStarts, blockingRuns, solidStarts, solidRuns);
    }

    /// <summary>Sets the bits of a part's own solid voxels in its stack's columns (index <c>x + 112 * z</c> from its corner).</summary>
    private static void AddPart(
        ObjectPart part,
        int words,
        ulong[] solidBits,
        ulong[] blockingBits,
        in MaterialSet passable
    )
    {
        const int border = ObjectPart.BorderVoxels;
        var level = part.Levels[0];
        var own = part.OwnSize;
        for (var b = 0; b < level.Bricks.Length; b++)
        {
            var packed = level.Bricks[b];
            // The brick's corner in the part's own voxels; only the part's own voxels count (its border is another's).
            int bx = ((packed & 15) << 3) - border,
                by = (((packed >> 4) & 15) << 3) - border,
                bz = (((packed >> 8) & 15) << 3) - border;
            int x0 = Math.Max(bx, 0),
                x1 = Math.Min(bx + Brick, own.X) - 1,
                y0 = Math.Max(by, 0),
                y1 = Math.Min(by + Brick, own.Y) - 1,
                z0 = Math.Max(bz, 0),
                z1 = Math.Min(bz + Brick, own.Z) - 1;
            if (x0 > x1 || y0 > y1 || z0 > z1)
            {
                continue;
            }

            // The kind's voxels y0 to y1 lie in one word: a brick's 8 start on a multiple of 8.
            var word = (part.Offset.Y + y0) >> 6;
            var payload = level.PayloadStart[b];
            if (level.Uniform[b])
            {
                var material = level.Payload[payload];
                if (material == VoxelConstants.Air)
                {
                    continue;
                }

                var mask = (ulong.MaxValue >> (63 - (y1 - y0))) << ((part.Offset.Y + y0) & 63);
                var blocks = !passable.Contains(material);
                for (var z = z0; z <= z1; z++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        var at = (x + Own * z) * words + word;
                        solidBits[at] |= mask;
                        if (blocks)
                        {
                            blockingBits[at] |= mask;
                        }
                    }
                }

                continue;
            }

            for (var z = z0; z <= z1; z++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    var at = (x + Own * z) * words + word;
                    for (var y = y0; y <= y1; y++)
                    {
                        var material = level.Payload[payload + (x - bx) + Brick * ((y - by) + Brick * (z - bz))];
                        if (material == VoxelConstants.Air)
                        {
                            continue;
                        }

                        var bit = 1UL << ((part.Offset.Y + y) & 63);
                        solidBits[at] |= bit;
                        if (!passable.Contains(material))
                        {
                            blockingBits[at] |= bit;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Adds the runs of set bits of one column's words, lowest first, under <paramref name="height"/>.</summary>
    private static void AddRuns(ReadOnlySpan<ulong> bits, int height, int column, List<(int Column, VoxelRun Run)> runs)
    {
        var y = 0;
        while (y < height)
        {
            var low = Next(bits, y, height, set: true);
            if (low >= height)
            {
                return;
            }

            var end = Next(bits, low, height, set: false);
            runs.Add((column, new VoxelRun(low, end - 1)));
            y = end;
        }
    }

    /// <summary>The first bit from <paramref name="from"/> that is set (or clear), or <paramref name="height"/>.</summary>
    private static int Next(ReadOnlySpan<ulong> bits, int from, int height, bool set)
    {
        var word = from >> 6;
        var current = (set ? bits[word] : ~bits[word]) & (ulong.MaxValue << (from & 63));
        while (current == 0)
        {
            if (++word >= bits.Length)
            {
                return height;
            }

            current = set ? bits[word] : ~bits[word];
        }

        return Math.Min(height, (word << 6) + BitOperations.TrailingZeroCount(current));
    }

    /// <summary>The runs in column order, with each column's start among them (and the end after the last).</summary>
    private static (int[] Starts, VoxelRun[] Runs) Gather(List<(int Column, VoxelRun Run)> found, int columns)
    {
        var starts = new int[columns + 1];
        foreach (var (column, _) in found)
        {
            starts[column + 1]++;
        }

        for (var i = 0; i < columns; i++)
        {
            starts[i + 1] += starts[i];
        }

        // Each column's runs came lowest first, its stack's in turn.
        var runs = new VoxelRun[found.Count];
        var next = starts[..^1];
        foreach (var (column, run) in found)
        {
            runs[next[column]++] = run;
        }

        return (starts, runs);
    }
}

/// <summary>A run of one column of a kind's voxels, from its lowest to its highest.</summary>
internal readonly record struct VoxelRun(int Low, int High);
