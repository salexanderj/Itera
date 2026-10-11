namespace Itera.Engine.Voxels;

public sealed partial class VoxelWorld
{
    /// <summary>Voxels along one edge of a block: a 32-voxel cell of a chunk's tree.</summary>
    internal const int BlockEdge = 1 << VoxelConstants.CellShift;

    /// <summary>Voxels around a block that its samples include on every side.</summary>
    internal const int BlockApron = 2;

    /// <summary>Samples along one edge of a block's sample box: the block and its apron.</summary>
    internal const int BlockSampleEdge = BlockEdge + 2 * BlockApron;

    /// <summary>
    /// Samples <see cref="TryReadBlock(int, int, int, int, Span{byte})"/> fills: <c>36^3</c>, index <c>x + 36 * (y + 36 * z)</c>. Sample (sx, sy, sz) is
    /// the level-k voxel (32 bx + sx - 2, 32 by + sy - 2, 32 bz + sz - 2) of block (bx, by, bz).
    /// </summary>
    internal const int BlockSamples = BlockSampleEdge * BlockSampleEdge * BlockSampleEdge;

    /// <summary>Voxels <see cref="TryReadOwnBlock"/> fills: <c>32^3</c>, index <c>x + 32 * (y + 32 * z)</c>.</summary>
    internal const int OwnBlockVoxels = BlockEdge * BlockEdge * BlockEdge;

    /// <summary>Bricks along one axis of a sample box: the block's 4, and one more on each side for the apron.</summary>
    private const int BlockBricksPerAxis = BlockEdge / VoxelConstants.BrickSize + 2;

    /// <summary>
    /// Copies the materials of block (<paramref name="bx"/>, <paramref name="by"/>, <paramref name="bz"/>) of
    /// <paramref name="level"/>, with a 2-voxel apron, into <paramref name="samples"/> (<see cref="BlockSamples"/>
    /// bytes, index <c>x + 36 * (y + 36 * z)</c>). A block is a 32^3 cell of a level-k chunk: block b covers the level-k
    /// voxels [32 b, 32 b + 32) on each axis. Returns false, writing nothing, when the column that holds the block is
    /// not resident or the block lies outside the world.
    /// A sample whose voxel lies in a resident column of the level and inside the world's height takes that voxel's
    /// material. Any other sample (its column is not resident, or it lies past the map, below the world, or above it)
    /// takes the material of the voxel at its coordinates clamped into the block's own 32^3 box, so an unloaded
    /// neighbour shows no surface against the block. Reads brick by brick and allocates nothing. Main thread.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="samples"/> is not <see cref="BlockSamples"/> long.</exception>
    internal bool TryReadBlock(int level, int bx, int by, int bz, Span<byte> samples) =>
        TryReadBlock(level, bx, by, bz, samples, default);

    /// <summary>
    /// <see cref="TryReadBlock(int, int, int, int, Span{byte})"/> that also copies each sample's density
    /// (<see cref="VoxelDensity"/>) into <paramref name="densities"/>, laid out as the samples, unless it is empty. A
    /// sample that takes a clamped voxel's material takes its density too.
    /// </summary>
    /// <exception cref="ArgumentException">A span is not <see cref="BlockSamples"/> long.</exception>
    internal bool TryReadBlock(int level, int bx, int by, int bz, Span<byte> samples, Span<byte> densities)
    {
        if (samples.Length != BlockSamples)
        {
            throw new ArgumentException($"The samples must hold {BlockSamples} bytes.", nameof(samples));
        }

        if (!densities.IsEmpty && densities.Length != BlockSamples)
        {
            throw new ArgumentException($"The densities must hold {BlockSamples} bytes.", nameof(densities));
        }

        var withDensities = !densities.IsEmpty;

        if (!TryGetOwnBlockColumn(level, bx, by, bz))
        {
            return false;
        }

        var memory = Memory;

        // The block's bricks along each axis: where they lie in the chunks, in the samples, and how much of each the
        // sample box takes. The block's own bricks are the middle four; the first and last add the apron's two voxels.
        Span<BlockBrickSpan> spans = stackalloc BlockBrickSpan[3 * BlockBricksPerAxis];
        var xs = spans[..BlockBricksPerAxis];
        var ys = spans.Slice(BlockBricksPerAxis, BlockBricksPerAxis);
        var zs = spans.Slice(2 * BlockBricksPerAxis, BlockBricksPerAxis);
        var firstChunkX = MakeBrickSpans(bx, xs, out var chunksX);
        var firstChunkY = MakeBrickSpans(by, ys, out var chunksY);
        var firstChunkZ = MakeBrickSpans(bz, zs, out var chunksZ);

        // The chunks that hold the sample box, 1 or 2 along each axis, and which of them can be read.
        Span<ChildState> chunks = stackalloc ChildState[8];
        var readable = 0;
        for (var cz = 0; cz < chunksZ; cz++)
        {
            for (var cx = 0; cx < chunksX; cx++)
            {
                if (!TryGetResident(new ColumnKey(level, firstChunkX + cx, firstChunkZ + cz), out var column))
                {
                    continue;
                }

                for (var cy = 0; cy < chunksY; cy++)
                {
                    var layer = firstChunkY + cy - Grid.MinChunkYAt(level);
                    if ((uint)layer < (uint)column.LayerCount)
                    {
                        var index = cx | (cy << 1) | (cz << 2);
                        chunks[index] = column.LayerAt(layer);
                        readable |= 1 << index;
                    }
                }
            }
        }

        // A box held whole by one empty or uniform chunk is one fill.
        if (readable == 1 && chunksX == 1 && chunksY == 1 && chunksZ == 1 && chunks[0].IsUniformOrEmpty)
        {
            samples.Fill(chunks[0].Material);
            densities.Fill(VoxelDensity.Default(chunks[0].Material));
            return true;
        }

        // First each brick's state, with the stored bricks copied whole; then the empty and uniform bricks, each row of
        // them in runs of equal bricks.
        Span<ChildState> bricks = stackalloc ChildState[BlockBricksPerAxis * BlockBricksPerAxis * BlockBricksPerAxis];
        Span<byte> brickVoxels = stackalloc byte[VoxelConstants.BrickVoxels];
        var missing = false;
        for (var bzi = 0; bzi < BlockBricksPerAxis; bzi++)
        {
            for (var byi = 0; byi < BlockBricksPerAxis; byi++)
            {
                for (var bxi = 0; bxi < BlockBricksPerAxis; bxi++)
                {
                    var chunk = xs[bxi].Chunk | (ys[byi].Chunk << 1) | (zs[bzi].Chunk << 2);
                    var index = bxi + BlockBricksPerAxis * (byi + BlockBricksPerAxis * bzi);
                    if ((readable & (1 << chunk)) == 0)
                    {
                        bricks[index] = Unreadable;
                        missing = true;
                        continue;
                    }

                    var brick = ChunkTree.GetBrick(memory, chunks[chunk], xs[bxi].Brick, ys[byi].Brick, zs[bzi].Brick);
                    bricks[index] = brick;
                    if (brick.Kind == ChildKind.Handle)
                    {
                        BrickCodec.Decode(memory, brick.Handle, brickVoxels);
                        CopyStoredBrick(brickVoxels, in xs[bxi], in ys[byi], in zs[bzi], samples);
                        if (withDensities)
                        {
                            BrickCodec.DecodeDensities(memory, brick.Handle, brickVoxels);
                            CopyStoredBrick(brickVoxels, in xs[bxi], in ys[byi], in zs[bzi], densities);
                        }
                    }
                }
            }
        }

        Span<BlockRun> runs = stackalloc BlockRun[BlockBricksPerAxis];
        for (var bzi = 0; bzi < BlockBricksPerAxis; bzi++)
        {
            for (var byi = 0; byi < BlockBricksPerAxis; byi++)
            {
                var row = bricks.Slice(BlockBricksPerAxis * (byi + BlockBricksPerAxis * bzi), BlockBricksPerAxis);
                var runCount = MakeRuns(row, xs, runs);
                for (var sz = zs[bzi].Sample; sz < zs[bzi].Sample + zs[bzi].Count; sz++)
                {
                    for (var sy = ys[byi].Sample; sy < ys[byi].Sample + ys[byi].Count; sy++)
                    {
                        for (var r = 0; r < runCount; r++)
                        {
                            var run = runs[r];
                            samples.Slice(SampleIndex(run.Sample, sy, sz), run.Count).Fill(run.Material);
                            if (withDensities)
                            {
                                densities
                                    .Slice(SampleIndex(run.Sample, sy, sz), run.Count)
                                    .Fill(VoxelDensity.Default(run.Material));
                            }
                        }
                    }
                }
            }
        }

        if (missing)
        {
            // The bricks that could not be read copy from the block's own box, which is all there.
            for (var bzi = 0; bzi < BlockBricksPerAxis; bzi++)
            {
                for (var byi = 0; byi < BlockBricksPerAxis; byi++)
                {
                    for (var bxi = 0; bxi < BlockBricksPerAxis; bxi++)
                    {
                        if (bricks[bxi + BlockBricksPerAxis * (byi + BlockBricksPerAxis * bzi)] == Unreadable)
                        {
                            ClampBrick(in xs[bxi], in ys[byi], in zs[bzi], samples);
                            if (withDensities)
                            {
                                ClampBrick(in xs[bxi], in ys[byi], in zs[bzi], densities);
                            }
                        }
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Reads block (<paramref name="bx"/>, <paramref name="by"/>, <paramref name="bz"/>) of <paramref name="level"/>
    /// without an apron, for a reader that needs only the block's own voxels (the sky field). Returns false, writing
    /// nothing, when the block's column is not resident or the block lies outside the world. Otherwise
    /// <paramref name="cell"/> is the block's cell in its chunk's tree: when that is empty or all one material
    /// (<see cref="ChildState.IsUniformOrEmpty"/>, then <see cref="ChildState.Material"/>), nothing is written; else
    /// <paramref name="voxels"/> (<see cref="OwnBlockVoxels"/> bytes, index <c>x + 32 * (y + 32 * z)</c>) gets the
    /// block's voxels, its empty and uniform bricks by fills and its stored bricks decoded. Allocates nothing. Main
    /// thread.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="voxels"/> is not <see cref="OwnBlockVoxels"/> long.</exception>
    internal bool TryReadOwnBlock(int level, int bx, int by, int bz, Span<byte> voxels, out ChildState cell)
    {
        if (voxels.Length != OwnBlockVoxels)
        {
            throw new ArgumentException($"The voxels must hold {OwnBlockVoxels} bytes.", nameof(voxels));
        }

        var memory = Memory;
        if ((uint)level >= (uint)_levels || !TryGetBlockCell(memory, level, bx, by, bz, out cell))
        {
            cell = default;
            return false;
        }

        if (cell.IsUniformOrEmpty)
        {
            return true;
        }

        const int bricks = BlockEdge / VoxelConstants.BrickSize;
        const int size = VoxelConstants.BrickSize;
        Span<byte> brickVoxels = stackalloc byte[VoxelConstants.BrickVoxels];
        for (var z = 0; z < bricks; z++)
        {
            for (var y = 0; y < bricks; y++)
            {
                for (var x = 0; x < bricks; x++)
                {
                    var brick = ChunkTree.GetChild(memory, cell.Handle, x | (y << 2) | (z << 4));
                    var first = size * (x + BlockEdge * (y + BlockEdge * z));
                    if (brick.Kind != ChildKind.Handle)
                    {
                        for (var row = 0; row < size * size; row++)
                        {
                            var at = first + BlockEdge * (row % size + BlockEdge * (row / size));
                            voxels.Slice(at, size).Fill(brick.Material);
                        }

                        continue;
                    }

                    BrickCodec.Decode(memory, brick.Handle, brickVoxels);
                    for (var row = 0; row < size * size; row++)
                    {
                        var at = first + BlockEdge * (row % size + BlockEdge * (row / size));
                        brickVoxels.Slice(row * size, size).CopyTo(voxels.Slice(at, size));
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// False only when certainly no edge that block (<paramref name="bx"/>, <paramref name="by"/>,
    /// <paramref name="bz"/>) owns crosses the surface. The block owns the edges from voxel u to u + e (e one of x, y, z)
    /// for u inside it, so the upper voxel may lie in the block's +x, +y, or +z neighbour. Answers false when the
    /// block's cell and those three neighbours' cells are each all air or all one material and all four are air or all
    /// four solid; a neighbour that is not resident, or lies outside the world, counts as the block's own cell (as in
    /// <see cref="TryReadBlock(int, int, int, int, Span{byte})"/>). Also false when the block's own column is not resident or the block lies outside
    /// the world. Reads four cells of the chunk trees and allocates nothing. Main thread.
    /// </summary>
    internal bool BlockMayHaveSurface(int level, int bx, int by, int bz)
    {
        if (!TryGetOwnBlockColumn(level, bx, by, bz))
        {
            return false;
        }

        var memory = Memory;
        if (!TryGetBlockCell(memory, level, bx, by, bz, out var own) || !own.IsUniformOrEmpty)
        {
            return true;
        }

        return NeighbourDiffers(memory, level, bx + 1, by, bz, own)
            || NeighbourDiffers(memory, level, bx, by + 1, bz, own)
            || NeighbourDiffers(memory, level, bx, by, bz + 1, own);
    }

    /// <summary>True when the block lies inside the world and its own column is resident.</summary>
    private bool TryGetOwnBlockColumn(int level, int bx, int by, int bz)
    {
        if ((uint)level >= (uint)_levels)
        {
            return false;
        }

        var chunk = new ChunkKey(level, bx >> 2, by >> 2, bz >> 2);
        return chunk.IsInBounds(Grid) && TryGetResident(chunk.Column, out _);
    }

    /// <summary>
    /// The state of block (bx, by, bz)'s cell, or false when its column is not resident or it lies outside the world.
    /// </summary>
    private bool TryGetBlockCell(VoxelMemory memory, int level, int bx, int by, int bz, out ChildState cell)
    {
        cell = default;
        var chunk = new ChunkKey(level, bx >> 2, by >> 2, bz >> 2);
        if (!chunk.IsInBounds(Grid) || !TryGetResident(chunk.Column, out var column))
        {
            return false;
        }

        cell = ChunkTree.GetCell(memory, column.LayerAt(chunk.Layer(Grid)), bx & 3, by & 3, bz & 3);
        return true;
    }

    /// <summary>
    /// True when the block's cell, if it can be read, is a node, or is all air where <paramref name="own"/> is all
    /// solid or the other way round.
    /// </summary>
    private bool NeighbourDiffers(VoxelMemory memory, int level, int bx, int by, int bz, ChildState own) =>
        TryGetBlockCell(memory, level, bx, by, bz, out var cell)
        && (!cell.IsUniformOrEmpty || cell.IsEmpty != own.IsEmpty);

    /// <summary>
    /// Lays out one axis of a block's sample box over its 6 bricks (the block's 4, and the apron's 2 voxels in the
    /// brick before and the brick after). Returns the lowest chunk coordinate the bricks lie in; the bricks lie in 1 or 2
    /// chunks along the axis, and <paramref name="chunks"/> is how many.
    /// </summary>
    private static int MakeBrickSpans(int block, Span<BlockBrickSpan> spans, out int chunks)
    {
        var sampleOrigin = block * BlockEdge - BlockApron;
        var firstChunk = (block * BlockEdge - VoxelConstants.BrickSize) >> VoxelConstants.ChunkShift;
        chunks = 1;
        for (var i = 0; i < BlockBricksPerAxis; i++)
        {
            var origin = block * BlockEdge + (i - 1) * VoxelConstants.BrickSize;
            var inSamples = origin - sampleOrigin;
            var start = Math.Max(0, -inSamples);
            var end = Math.Min(VoxelConstants.BrickSize, BlockSampleEdge - inSamples);
            var chunk = (origin >> VoxelConstants.ChunkShift) - firstChunk;
            chunks = Math.Max(chunks, chunk + 1);
            spans[i] = new BlockBrickSpan(
                chunk,
                (origin >> VoxelConstants.BrickShift) & (VoxelConstants.BricksPerChunkEdge - 1),
                inSamples + start,
                start,
                end - start
            );
        }

        return firstChunk;
    }

    /// <summary>The state of a brick that could not be read: a handle that no brick has.</summary>
    private static readonly ChildState Unreadable = new(ChildKind.Handle, -1);

    /// <summary>Copies the part of a decoded brick that lies in the sample box into the samples, row by row.</summary>
    private static void CopyStoredBrick(
        ReadOnlySpan<byte> brickVoxels,
        in BlockBrickSpan x,
        in BlockBrickSpan y,
        in BlockBrickSpan z,
        Span<byte> samples
    )
    {
        for (var dz = 0; dz < z.Count; dz++)
        {
            for (var dy = 0; dy < y.Count; dy++)
            {
                var source =
                    ((z.Start + dz) << (2 * VoxelConstants.BrickShift))
                    + ((y.Start + dy) << VoxelConstants.BrickShift)
                    + x.Start;
                brickVoxels
                    .Slice(source, x.Count)
                    .CopyTo(samples.Slice(SampleIndex(x.Sample, y.Sample + dy, z.Sample + dz), x.Count));
            }
        }
    }

    /// <summary>
    /// The runs of neighbouring empty or uniform bricks of one material along x in a row of 6 bricks: where each starts
    /// in the samples, how many samples it covers, and its material. Stored and unreadable bricks end a run and are left
    /// out. Returns the number of runs.
    /// </summary>
    private static int MakeRuns(ReadOnlySpan<ChildState> row, ReadOnlySpan<BlockBrickSpan> xs, Span<BlockRun> runs)
    {
        var count = 0;
        var open = false;
        for (var i = 0; i < BlockBricksPerAxis; i++)
        {
            if (row[i].Kind == ChildKind.Handle)
            {
                open = false;
            }
            else if (open && runs[count - 1].Material == row[i].Material)
            {
                runs[count - 1] = runs[count - 1] with { Count = runs[count - 1].Count + xs[i].Count };
            }
            else
            {
                runs[count++] = new BlockRun(xs[i].Sample, xs[i].Count, row[i].Material);
                open = true;
            }
        }

        return count;
    }

    /// <summary>
    /// Fills the part of an unreadable brick that lies in the sample box with the material at each sample's coordinates
    /// clamped into the block's own box. Along each axis a brick lies wholly before the box (samples 0 and 1), wholly
    /// after it (34 and 35), or wholly inside it, and a brick outside on one axis at least is never inside on all three,
    /// so the rows copied from lie in the block's own box and are not the rows written.
    /// </summary>
    private static void ClampBrick(in BlockBrickSpan x, in BlockBrickSpan y, in BlockBrickSpan z, Span<byte> samples)
    {
        const int last = BlockApron + BlockEdge - 1;
        for (var sz = z.Sample; sz < z.Sample + z.Count; sz++)
        {
            var ownZ = Math.Clamp(sz, BlockApron, last);
            for (var sy = y.Sample; sy < y.Sample + y.Count; sy++)
            {
                var ownY = Math.Clamp(sy, BlockApron, last);
                var row = samples.Slice(SampleIndex(x.Sample, sy, sz), x.Count);
                if (x.Sample + x.Count <= BlockApron)
                {
                    row.Fill(samples[SampleIndex(BlockApron, ownY, ownZ)]);
                }
                else if (x.Sample > last)
                {
                    row.Fill(samples[SampleIndex(last, ownY, ownZ)]);
                }
                else
                {
                    samples.Slice(SampleIndex(x.Sample, ownY, ownZ), x.Count).CopyTo(row);
                }
            }
        }
    }

    private static int SampleIndex(int x, int y, int z) => x + BlockSampleEdge * (y + BlockSampleEdge * z);

    /// <summary>
    /// One brick's place along one axis of a block's sample box: its chunk (0 or 1 from the lowest chunk the box
    /// reaches), its index in that chunk (0 to 15), the first sample it covers, the first voxel of it that is a sample,
    /// and how many of its voxels are.
    /// </summary>
    private readonly record struct BlockBrickSpan(int Chunk, int Brick, int Sample, int Start, int Count);

    /// <summary>Neighbouring bricks of one row that hold one material (or air): the first sample, how many, the material.</summary>
    private readonly record struct BlockRun(int Sample, int Count, byte Material);
}
