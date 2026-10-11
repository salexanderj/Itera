using Itera.Engine.Maths;
using Itera.Engine.Meshing;
using Itera.Engine.Voxels;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// One block's mesh state (<see cref="MeshColumn.Blocks"/>): whether it waits to be meshed, its version, and where its
/// mesh lies in the heap.
/// </summary>
internal struct MeshBlock
{
    /// <summary>Raised by every change that may alter the block's mesh; a finished job of an older version is stale.</summary>
    public int Version;

    /// <summary>True when the block must be meshed again (it may or may not be in flight with an older version).</summary>
    public bool Dirty;

    /// <summary>True while a job of the block is with the workers or waits for room to upload.</summary>
    public bool InFlight;

    /// <summary>True from an edit's change to the block until a mesh of its voxels since is staged (the edits' latency).</summary>
    public bool Edited;

    /// <summary>The heap range of the mesh, in words; 0 words for no mesh.</summary>
    public uint RangeOffset;

    public int RangeWords;

    /// <summary>The first word of the vertices (the range's first word).</summary>
    public uint VertexBase;

    /// <summary>The first 16-bit index in the heap, and how many there are (three per triangle).</summary>
    public uint FirstIndex;

    public int IndexCount;

    /// <summary>The block's place in <see cref="MeshColumn.Drawn"/>, or -1.</summary>
    public int DrawnSlot;

    /// <summary>
    /// Where the block's glowing surfaces' light is, in the block's voxels from its corner: their middle, weighted by
    /// glow. Meaningless while <see cref="LightPower"/> is 0.
    /// </summary>
    public System.Numerics.Vector3 LightAt;

    /// <summary>The colour of the block's glowing surfaces, weighted by glow.</summary>
    public System.Numerics.Vector3 LightColour;

    /// <summary>The glowing surfaces' area times their glow, in the block's voxel faces; 0 for no light.</summary>
    public float LightPower;
}

/// <summary>
/// A block's stitched mesh (<see cref="MeshColumn.Stitches"/>): the block meshed in the transition mode beside columns
/// drawn one level coarser (<see cref="SurfaceNets.MeshTransition"/>), drawn by the camera and the near cascade in place
/// of its plain mesh while those columns are where it was meshed for. Its own range in the heap.
/// </summary>
internal struct MeshStitch
{
    /// <summary>The sides it was meshed for; <see cref="TransitionSides.None"/> while there is none.</summary>
    public TransitionSides Sides;

    /// <summary>The block's <see cref="MeshBlock.Version"/> when its voxels were read.</summary>
    public int BlockVersion;

    /// <summary><see cref="CoarseChanges"/> when the coarser level's block was read.</summary>
    public int CoarseVersion;

    /// <summary>Raised by every change that may alter the coarser level's block that holds this one, apron included.</summary>
    public int CoarseChanges;

    /// <summary>True while a job of it is with the workers or waits for room to upload.</summary>
    public bool InFlight;

    /// <summary>True when the block's plain mesh stands in: the mesher failed, or the heap had no room.</summary>
    public bool UsePlain;

    /// <summary>The heap range of the mesh, in words; 0 words for none.</summary>
    public uint RangeOffset;

    public int RangeWords;

    /// <summary>The first word of the vertices.</summary>
    public uint VertexBase;

    /// <summary>The first 16-bit index in the heap, and how many there are; 0 when the stitched mesh is empty.</summary>
    public uint FirstIndex;

    public int IndexCount;

    /// <summary>True when it was meshed for <paramref name="sides"/> from the block's and the coarser block's voxels as they are.</summary>
    public readonly bool IsCurrent(TransitionSides sides, int blockVersion) =>
        Sides == sides && BlockVersion == blockVersion && CoarseVersion == CoarseChanges;
}

/// <summary>
/// The blocks of one resident column at a meshed level, for the layers that can hold a surface (the voxel column's
/// stored layers and one each side, where they meet the fills below and above them: <see cref="FirstLayer"/>,
/// <see cref="Layers"/>): 64 a chunk (4 x 4 x 4 cells of 32 voxels), block index
/// <c>(layer - FirstLayer) * 64 + x + 4y + 16z</c> with x, y, z the cell in the chunk. Main thread.
/// </summary>
internal sealed class MeshColumn
{
    public const int BlocksPerChunk = 64;

    private readonly int _firstChunkY;

    /// <param name="key">The column.</param>
    /// <param name="grid">Its world's grid.</param>
    /// <param name="firstLayer">The first layer with blocks, counted from the grid's floor at the column's level.</param>
    /// <param name="endLayer">The layer after the last with blocks.</param>
    public MeshColumn(ColumnKey key, VoxelGrid grid, int firstLayer, int endLayer)
    {
        Key = key;
        _firstChunkY = grid.MinChunkYAt(key.Level);
        FirstLayer = firstLayer;
        Layers = Math.Max(endLayer - firstLayer, 0);
        Blocks = new MeshBlock[Layers * BlocksPerChunk];
        for (var i = 0; i < Blocks.Length; i++)
        {
            Blocks[i].DrawnSlot = -1;
        }
    }

    public ColumnKey Key { get; }

    /// <summary>The first layer with blocks, counted from the grid's floor at the column's level.</summary>
    public int FirstLayer { get; }

    /// <summary>How many layers have blocks.</summary>
    public int Layers { get; }

    public MeshBlock[] Blocks { get; }

    /// <summary>True when the layers from <paramref name="firstLayer"/> to before <paramref name="endLayer"/> have blocks.</summary>
    public bool Covers(int firstLayer, int endLayer) => firstLayer >= FirstLayer && endLayer <= FirstLayer + Layers;

    /// <summary>The block index of <paramref name="cell"/> (x + 4y + 16z) in <paramref name="layer"/>; false where the layer has no blocks.</summary>
    public bool TryIndex(int layer, int cell, out int index)
    {
        var row = layer - FirstLayer;
        index = row * BlocksPerChunk + cell;
        return (uint)row < (uint)Layers;
    }

    /// <summary>
    /// The blocks' stitched meshes, laid out as <see cref="Blocks"/>; made when the column is first drawn beside one a
    /// level coarser, null until then.
    /// </summary>
    public MeshStitch[]? Stitches { get; private set; }

    /// <summary>The blocks that have a mesh, in no order.</summary>
    public List<int> Drawn { get; } = [];

    /// <summary>Blocks dirty or in flight.</summary>
    public int Pending { get; set; }

    /// <summary>True once every block has been meshed since the column loaded: its finer children may stand in for it.</summary>
    public bool MeshedOnce { get; set; }

    /// <summary>
    /// True while the column is near enough to be drawn (<c>WorldMeshes.IsWanted</c>): only then are its blocks meshed.
    /// </summary>
    public bool Wanted { get; set; }

    /// <summary>The <c>WorldMeshes.Uploads</c> of the last frame whose camera or far cascade pass drew the column; -1 for none.</summary>
    public long DrawnUpload { get; set; } = -1;

    /// <summary>
    /// The <c>WorldMeshes.Uploads</c> of the last frame that needed the column: drew it, or would have drawn it in place of
    /// its coarser column had it been meshed. Eviction leaves such columns alone; -1 for none.
    /// </summary>
    public long NeededUpload { get; set; } = -1;

    /// <summary>The <c>WorldMeshes.Uploads</c> of the last frame that staged a mesh of the column; -1 for none.</summary>
    public long StagedUpload { get; set; } = -1;

    /// <summary>
    /// True while its meshes were freed to make room in a full heap though it is still wanted: it is meshed again only
    /// when the view needs it (<c>WorldMeshes.Evict</c>).
    /// </summary>
    public bool Evicted { get; set; }

    /// <summary>Squared horizontal distance from the view to the column's centre, in level-0 voxels, at the last sort.</summary>
    public double DistanceSquared { get; set; }

    /// <summary><see cref="Stitches"/>, made on first use.</summary>
    public MeshStitch[] StitchesMade() => Stitches ??= new MeshStitch[Blocks.Length];

    /// <summary>
    /// The sides of block <paramref name="index"/> past which a column one level coarser lies, of the column's
    /// <paramref name="sides"/>: a block meets a side only where it lies along it (its x or z cell in the chunk 0 or 3),
    /// and a corner of the block lies past the column's side, corner, or nothing (inside the column) by where it lies.
    /// </summary>
    public static TransitionSides BlockSides(TransitionSides sides, int index)
    {
        var cell = index % BlocksPerChunk;
        var sx = Edge(cell & 3);
        var sz = Edge(cell >> 4);
        if (sides == TransitionSides.None || (sx == 0 && sz == 0))
        {
            return TransitionSides.None;
        }

        var block = TransitionSides.None;
        if (Past(sides, sx, 0))
        {
            block |= sx < 0 ? TransitionSides.LowX : TransitionSides.HighX;
        }

        if (Past(sides, 0, sz))
        {
            block |= sz < 0 ? TransitionSides.LowZ : TransitionSides.HighZ;
        }

        // The block's four corners: each lies past the column's side, corner, or nothing, by the block's place.
        for (var cz = -1; cz <= 1; cz += 2)
        {
            for (var cx = -1; cx <= 1; cx += 2)
            {
                if (Past(sides, cx == sx ? sx : 0, cz == sz ? sz : 0))
                {
                    block |= Corner(cx, cz);
                }
            }
        }

        return block;
    }

    /// <summary>-1 for the first of a chunk's four cells along an axis, 1 for the last, else 0.</summary>
    private static int Edge(int cell) =>
        cell == 0 ? -1
        : cell == 3 ? 1
        : 0;

    /// <summary>True when the column's neighbour (dx, dz), each -1, 0, or 1, is one level coarser; (0, 0) is the column.</summary>
    private static bool Past(TransitionSides sides, int dx, int dz)
    {
        var side = (dx, dz) switch
        {
            (0, 0) => TransitionSides.None,
            (_, 0) => dx < 0 ? TransitionSides.LowX : TransitionSides.HighX,
            (0, _) => dz < 0 ? TransitionSides.LowZ : TransitionSides.HighZ,
            _ => Corner(dx, dz),
        };
        return (sides & side) != 0;
    }

    private static TransitionSides Corner(int dx, int dz) =>
        dx < 0
            ? (dz < 0 ? TransitionSides.LowXLowZ : TransitionSides.LowXHighZ)
            : (dz < 0 ? TransitionSides.HighXLowZ : TransitionSides.HighXHighZ);

    /// <summary>The block's coordinates (in blocks of its level) for index <paramref name="index"/>.</summary>
    public (int X, int Y, int Z) BlockAt(int index)
    {
        var layer = index / BlocksPerChunk;
        var cell = index % BlocksPerChunk;
        var chunkY = _firstChunkY + FirstLayer + layer;
        return ((Key.X << 2) + (cell & 3), (chunkY << 2) + ((cell >> 2) & 3), (Key.Z << 2) + (cell >> 4));
    }

    /// <summary>
    /// Writes the indices of the blocks to read (dirty, not in flight) into <paramref name="order"/>, nearest
    /// <paramref name="viewPosition"/> first by the distance to each block's centre, and returns how many: within a
    /// column many layers high, the blocks at the view's height and side come before those deep below the ground or
    /// across it. <paramref name="distances"/> is scratch; both hold at least <see cref="Blocks"/>' length. Allocates
    /// nothing.
    /// </summary>
    public int DirtyNearestFirst(VoxelGrid grid, in WorldPoint viewPosition, int[] order, float[] distances)
    {
        // In absolute metres, whatever the match's origin.
        var blockMetres = SurfaceNets.BlockSize * (double)grid.Edge(Key.Level) / VoxelConstants.CentimetresPerMetre;
        var count = 0;
        for (var i = 0; i < Blocks.Length; i++)
        {
            ref readonly var block = ref Blocks[i];
            if (!block.Dirty || block.InFlight)
            {
                continue;
            }

            var (x, y, z) = BlockAt(i);
            double dx = (x + 0.5) * blockMetres - viewPosition.X,
                dy = (y + 0.5) * blockMetres - viewPosition.Y,
                dz = (z + 0.5) * blockMetres - viewPosition.Z;
            order[count] = i;
            distances[count++] = (float)(dx * dx + dy * dy + dz * dz);
        }

        Array.Sort(distances, order, 0, count);
        return count;
    }

    /// <summary>Adds a block with a mesh to <see cref="Drawn"/>, once.</summary>
    public void AddDrawn(int index)
    {
        ref var block = ref Blocks[index];
        if (block.DrawnSlot >= 0)
        {
            return;
        }

        block.DrawnSlot = Drawn.Count;
        Drawn.Add(index);
    }

    /// <summary>Takes a block out of <see cref="Drawn"/>, moving the last one into its place.</summary>
    public void RemoveDrawn(int index)
    {
        ref var block = ref Blocks[index];
        var slot = block.DrawnSlot;
        if (slot < 0)
        {
            return;
        }

        var last = Drawn[^1];
        Drawn[slot] = last;
        Blocks[last].DrawnSlot = slot;
        Drawn.RemoveAt(Drawn.Count - 1);
        block.DrawnSlot = -1;
    }
}
