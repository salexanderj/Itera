using System.Numerics;

namespace Itera.Engine.Voxels;

/// <summary>
/// Operations on one chunk's tree. A chunk's state is empty, one material, or a root node; the root's 64 children are
/// 32-voxel cells, each empty, one material, or a cell node; a cell node's 64 children are bricks, each empty, one
/// material, or a stored brick (<see cref="BrickCodec"/>). A node keeps its children in a packed list read by rank.
/// After every change a node that is all air or all one material is freed and replaced by that state in its parent,
/// so equal voxels always give the same tree shape.
/// </summary>
internal static unsafe class ChunkTree
{
    /// <summary>Tree depth of a chunk root; a cell node is 1 and a brick is 0.</summary>
    public const int RootDepth = 2;

    private const int NoList = -1;
    private const int ListClasses = 4;

    /// <summary>Entries a child list of <paramref name="listClass"/> holds: 8, 16, 32, or 64.</summary>
    public static int ListCapacity(int listClass) => 8 << listClass;

    /// <summary>The material at chunk-local voxel (x, y, z), each 0 to 127.</summary>
    public static byte ReadVoxel(VoxelMemory memory, ChildState chunk, int x, int y, int z)
    {
        var brick = GetBrick(memory, chunk, x >> 3, y >> 3, z >> 3);
        if (brick.Kind != ChildKind.Handle)
        {
            return brick.Material;
        }

        return BrickCodec.Read(memory, brick.Handle, (x & 7) | ((y & 7) << 3) | ((z & 7) << 6));
    }

    /// <summary>The state of the 32-voxel cell at cell coordinates (x, y, z) in the chunk, each 0 to 3.</summary>
    public static ChildState GetCell(VoxelMemory memory, ChildState chunk, int x, int y, int z) =>
        chunk.Kind != ChildKind.Handle ? chunk : GetChild(memory, chunk.Handle, x | (y << 2) | (z << 4));

    /// <summary>The state of the brick at brick coordinates (x, y, z) in the chunk, each 0 to 15.</summary>
    public static ChildState GetBrick(VoxelMemory memory, ChildState chunk, int x, int y, int z)
    {
        if (chunk.Kind != ChildKind.Handle)
        {
            return chunk;
        }

        var cell = GetChild(memory, chunk.Handle, (x >> 2) | ((y >> 2) << 2) | ((z >> 2) << 4));
        if (cell.Kind != ChildKind.Handle)
        {
            return cell;
        }

        return GetChild(memory, cell.Handle, (x & 3) | ((y & 3) << 2) | ((z & 3) << 4));
    }

    /// <summary>
    /// Puts <paramref name="brick"/> at brick coordinates (x, y, z) and returns the chunk's new state. A stored brick
    /// that was there is freed. Nodes are created and collapsed as needed. Any thread that owns the tree; it may pass the budget.
    /// </summary>
    public static ChildState SetBrick(VoxelMemory memory, ChildState chunk, int x, int y, int z, ChildState brick)
    {
        var cellIndex = (x >> 2) | ((y >> 2) << 2) | ((z >> 2) << 4);
        var brickIndex = (x & 3) | ((y & 3) << 2) | ((z & 3) << 4);
        var old = GetBrick(memory, chunk, x, y, z);
        if (old == brick)
        {
            return chunk;
        }

        var root = chunk.Kind == ChildKind.Handle ? chunk.Handle : NewUniformNode(memory, chunk);
        var cell = GetChild(memory, root, cellIndex);
        var cellNode = cell.Kind == ChildKind.Handle ? cell.Handle : NewUniformNode(memory, cell);
        if (old.Kind == ChildKind.Handle)
        {
            BrickCodec.Free(memory, old.Handle);
        }

        SetChild(memory, cellNode, brickIndex, brick);
        SetChild(memory, root, cellIndex, Collapse(memory, cellNode));
        return Collapse(memory, root);
    }

    /// <summary>
    /// Stores a whole chunk from <paramref name="voxels"/>, 128^3 materials at index <c>x + 128y + 16384z</c>, and
    /// returns its state. When memory runs out, nothing is kept, <paramref name="outOfMemory"/> is true, and the result
    /// is empty.
    /// </summary>
    public static ChildState Encode(
        VoxelMemory memory,
        ReadOnlySpan<byte> voxels,
        bool mayExceedBudget,
        out bool outOfMemory
    ) => Encode(memory, voxels, Whole, mayExceedBudget, out outOfMemory);

    /// <summary>
    /// <see cref="Encode(VoxelMemory, ReadOnlySpan{byte}, bool, out bool)"/> for a chunk whose voxels outside the box
    /// from (0, 0, 0) to <paramref name="extent"/> count as air whatever <paramref name="voxels"/> holds there: bricks
    /// wholly outside it are not read.
    /// </summary>
    public static ChildState Encode(
        VoxelMemory memory,
        ReadOnlySpan<byte> voxels,
        VoxelCoord extent,
        bool mayExceedBudget,
        out bool outOfMemory
    ) => Encode(memory, voxels, default, extent, mayExceedBudget, out outOfMemory);

    /// <summary>
    /// Stores a whole chunk from <paramref name="voxels"/> and <paramref name="densities"/> (128^3 each at index
    /// <c>x + 128y + 16384z</c>; empty densities give every voxel its material's default) and returns its state. When
    /// memory runs out, nothing is kept, <paramref name="outOfMemory"/> is true, and the result is empty.
    /// </summary>
    public static ChildState Encode(
        VoxelMemory memory,
        ReadOnlySpan<byte> voxels,
        ReadOnlySpan<byte> densities,
        bool mayExceedBudget,
        out bool outOfMemory
    ) => Encode(memory, voxels, densities, Whole, mayExceedBudget, out outOfMemory);

    /// <summary>
    /// <see cref="Encode(VoxelMemory, ReadOnlySpan{byte}, ReadOnlySpan{byte}, bool, out bool)"/> for a chunk whose voxels
    /// outside the box from (0, 0, 0) to <paramref name="extent"/> count as empty air whatever the spans hold there.
    /// </summary>
    public static ChildState Encode(
        VoxelMemory memory,
        ReadOnlySpan<byte> voxels,
        ReadOnlySpan<byte> densities,
        VoxelCoord extent,
        bool mayExceedBudget,
        out bool outOfMemory
    )
    {
        Span<byte> brickVoxels = stackalloc byte[VoxelConstants.BrickVoxels];
        Span<byte> brickDensities = densities.IsEmpty ? default : stackalloc byte[VoxelConstants.BrickVoxels];
        Span<ChildState> cells = stackalloc ChildState[VoxelConstants.NodeChildren];
        Span<ChildState> bricks = stackalloc ChildState[VoxelConstants.NodeChildren];
        outOfMemory = false;

        for (var c = 0; c < VoxelConstants.NodeChildren; c++)
        {
            for (var b = 0; b < VoxelConstants.NodeChildren; b++)
            {
                var originX = (((c & 3) << 2) | (b & 3)) << VoxelConstants.BrickShift;
                var originY = ((((c >> 2) & 3) << 2) | ((b >> 2) & 3)) << VoxelConstants.BrickShift;
                var originZ = (((c >> 4) << 2) | (b >> 4)) << VoxelConstants.BrickShift;
                if (originX >= extent.X || originY >= extent.Y || originZ >= extent.Z)
                {
                    bricks[b] = ChildState.Empty;
                    continue;
                }

                Gather(voxels, originX, originY, originZ, brickVoxels);
                ClearOutside(brickVoxels, originX, originY, originZ, extent);
                if (!densities.IsEmpty)
                {
                    Gather(densities, originX, originY, originZ, brickDensities);
                    ClearOutside(brickDensities, originX, originY, originZ, extent);
                }

                bricks[b] = BrickCodec.Encode(memory, brickVoxels, brickDensities, mayExceedBudget, out outOfMemory);
                if (outOfMemory)
                {
                    FreeAll(memory, bricks[..b], 0);
                    FreeAll(memory, cells[..c], 1);
                    return ChildState.Empty;
                }
            }

            cells[c] = BuildNode(memory, bricks, mayExceedBudget, out outOfMemory);
            if (outOfMemory)
            {
                FreeAll(memory, bricks, 0);
                FreeAll(memory, cells[..c], 1);
                return ChildState.Empty;
            }
        }

        var root = BuildNode(memory, cells, mayExceedBudget, out outOfMemory);
        if (outOfMemory)
        {
            FreeAll(memory, cells, 1);
            return ChildState.Empty;
        }

        return root;
    }

    /// <summary>Writes a chunk's 128^3 materials into <paramref name="voxels"/>, index <c>x + 128y + 16384z</c>.</summary>
    public static void Decode(VoxelMemory memory, ChildState chunk, Span<byte> voxels) =>
        Decode(memory, chunk, voxels, Whole);

    /// <summary>
    /// Writes the chunk's materials into <paramref name="voxels"/> (index <c>x + 128y + 16384z</c>) for the bricks that
    /// reach into the box from (0, 0, 0) to <paramref name="extent"/>; the rest of <paramref name="voxels"/> is left as
    /// it was.
    /// </summary>
    public static void Decode(VoxelMemory memory, ChildState chunk, Span<byte> voxels, VoxelCoord extent)
    {
        if (chunk.Kind != ChildKind.Handle && extent == Whole)
        {
            voxels[..VoxelConstants.ChunkVoxels].Fill(chunk.Material);
            return;
        }

        Span<byte> brickVoxels = stackalloc byte[VoxelConstants.BrickVoxels];
        var bricksX = Math.Min(VoxelConstants.BricksPerChunkEdge, (extent.X + VoxelConstants.BrickSize - 1) >> 3);
        var bricksY = Math.Min(VoxelConstants.BricksPerChunkEdge, (extent.Y + VoxelConstants.BrickSize - 1) >> 3);
        var bricksZ = Math.Min(VoxelConstants.BricksPerChunkEdge, (extent.Z + VoxelConstants.BrickSize - 1) >> 3);
        for (var z = 0; z < bricksZ; z++)
        {
            for (var y = 0; y < bricksY; y++)
            {
                for (var x = 0; x < bricksX; x++)
                {
                    BrickCodec.Fill(memory, GetBrick(memory, chunk, x, y, z), brickVoxels);
                    Scatter(brickVoxels, x << VoxelConstants.BrickShift, y << 3, z << 3, voxels);
                }
            }
        }
    }

    /// <summary>
    /// Writes the chunk's densities into <paramref name="densities"/> (index <c>x + 128y + 16384z</c>) for the bricks
    /// that reach into the box from (0, 0, 0) to <paramref name="extent"/>; the rest is left as it was.
    /// </summary>
    public static void DecodeDensities(VoxelMemory memory, ChildState chunk, Span<byte> densities, VoxelCoord extent)
    {
        if (chunk.Kind != ChildKind.Handle && extent == Whole)
        {
            densities[..VoxelConstants.ChunkVoxels].Fill(VoxelDensity.Default(chunk.Material));
            return;
        }

        Span<byte> brickDensities = stackalloc byte[VoxelConstants.BrickVoxels];
        var bricksX = Math.Min(VoxelConstants.BricksPerChunkEdge, (extent.X + VoxelConstants.BrickSize - 1) >> 3);
        var bricksY = Math.Min(VoxelConstants.BricksPerChunkEdge, (extent.Y + VoxelConstants.BrickSize - 1) >> 3);
        var bricksZ = Math.Min(VoxelConstants.BricksPerChunkEdge, (extent.Z + VoxelConstants.BrickSize - 1) >> 3);
        for (var z = 0; z < bricksZ; z++)
        {
            for (var y = 0; y < bricksY; y++)
            {
                for (var x = 0; x < bricksX; x++)
                {
                    BrickCodec.FillDensities(memory, GetBrick(memory, chunk, x, y, z), brickDensities);
                    Scatter(brickDensities, x << VoxelConstants.BrickShift, y << 3, z << 3, densities);
                }
            }
        }
    }

    /// <summary>Writes a chunk's 128^3 densities into <paramref name="densities"/>, index <c>x + 128y + 16384z</c>.</summary>
    public static void DecodeDensities(VoxelMemory memory, ChildState chunk, Span<byte> densities) =>
        DecodeDensities(memory, chunk, densities, Whole);

    /// <summary>The density at chunk-local voxel (x, y, z), each 0 to 127.</summary>
    public static byte ReadDensity(VoxelMemory memory, ChildState chunk, int x, int y, int z)
    {
        var brick = GetBrick(memory, chunk, x >> 3, y >> 3, z >> 3);
        if (brick.Kind != ChildKind.Handle)
        {
            return VoxelDensity.Default(brick.Material);
        }

        return BrickCodec.ReadDensity(memory, brick.Handle, (x & 7) | ((y & 7) << 3) | ((z & 7) << 6));
    }

    /// <summary>
    /// A copy of a state's nodes and bricks, so changes to either leave the other alone. <paramref name="depth"/> is 2
    /// for a chunk, 1 for a cell, 0 for a brick. It copies only what is stored (a part of a body has a few hundred
    /// bricks); decoding the chunk and encoding it again would scan 128^3 voxels. Any thread that owns the trees; it may pass the budget.
    /// </summary>
    public static ChildState Copy(VoxelMemory memory, ChildState state, int depth)
    {
        if (state.Kind != ChildKind.Handle)
        {
            return state;
        }

        if (depth == 0)
        {
            return BrickCodec.Copy(memory, state.Handle);
        }

        Span<ChildState> children = stackalloc ChildState[VoxelConstants.NodeChildren];
        for (var child = 0; child < VoxelConstants.NodeChildren; child++)
        {
            children[child] = Copy(memory, GetChild(memory, state.Handle, child), depth - 1);
        }

        return BuildNode(memory, children, mayExceedBudget: true, out _);
    }

    /// <summary>
    /// The size (128, 32, 8, 4, 2, or 1) of the largest part of a chunk's tree that holds chunk-local voxel (x, y, z),
    /// each 0 to 127, and is all air or all one material, and that material. Ray walks step across such parts whole
    /// (<see cref="VoxelWorld.Raycast"/>, <see cref="Raycast"/>).
    /// </summary>
    public static int Probe(VoxelMemory memory, ChildState chunk, int x, int y, int z, out byte material)
    {
        if (chunk.Kind != ChildKind.Handle)
        {
            material = chunk.Material;
            return VoxelConstants.ChunkSize;
        }

        var cell = GetChild(memory, chunk.Handle, (x >> 5) | ((y >> 5) << 2) | ((z >> 5) << 4));
        if (cell.Kind != ChildKind.Handle)
        {
            material = cell.Material;
            return 1 << VoxelConstants.CellShift;
        }

        var brick = GetChild(memory, cell.Handle, ((x >> 3) & 3) | (((y >> 3) & 3) << 2) | (((z >> 3) & 3) << 4));
        if (brick.Kind != ChildKind.Handle)
        {
            material = brick.Material;
            return VoxelConstants.BrickSize;
        }

        material = VoxelConstants.Air;
        var record = memory.Brick(brick.Handle);
        if (record->Bits == 0 && record->MaterialOrCount == VoxelConstants.Air)
        {
            // A brick stored for its densities alone: all air.
            return VoxelConstants.BrickSize;
        }

        int bx = x & 7,
            by = y & 7,
            bz = z & 7;
        var mask = memory.BrickMask(brick.Handle);
        var quarter = 0x0F0F0F0FUL << (4 * (bx >> 2) + 32 * (by >> 2));
        var quarterLayers = bz & ~3;
        if (
            (
                (mask[quarterLayers] | mask[quarterLayers + 1] | mask[quarterLayers + 2] | mask[quarterLayers + 3])
                & quarter
            ) == 0
        )
        {
            return 4;
        }

        var eighth = 0x0303UL << (2 * (bx >> 1) + 16 * (by >> 1));
        var eighthLayers = bz & ~1;
        if (((mask[eighthLayers] | mask[eighthLayers + 1]) & eighth) == 0)
        {
            return 2;
        }

        material = BrickCodec.Read(memory, brick.Handle, bx | (by << 3) | (bz << 6));
        return 1;
    }

    /// <summary>
    /// Finds the first solid voxel of a chunk's tree along a ray, inside the box from (0, 0, 0) to
    /// <paramref name="size"/> and within <paramref name="maxT"/>: the ray (origin o, unit direction d, in the chunk's
    /// voxels) starts where it enters the box, or at o when o lies in it, and stops where it leaves, so a ray that misses
    /// the box takes no step. It walks the largest empty or solid part of the tree that holds the current voxel, as
    /// <see cref="VoxelWorld.Raycast"/> does. <paramref name="axis"/> is the axis of the face it entered the voxel
    /// through (0, 1, 2), or -1 when o lies in it; <paramref name="t"/> the distance to the entry point.
    /// </summary>
    public static bool Raycast(
        VoxelMemory memory,
        ChildState chunk,
        VoxelCoord size,
        double ox,
        double oy,
        double oz,
        double dx,
        double dy,
        double dz,
        double maxT,
        out VoxelCoord voxel,
        out int axis,
        out byte material,
        out double t
    )
    {
        voxel = default;
        axis = -1;
        material = VoxelConstants.Air;
        t = 0;
        var exit = maxT;
        if (
            !Clip(ox, dx, size.X, 0, ref t, ref exit, ref axis)
            || !Clip(oy, dy, size.Y, 1, ref t, ref exit, ref axis)
            || !Clip(oz, dz, size.Z, 2, ref t, ref exit, ref axis)
            || t > exit
        )
        {
            return false;
        }

        // The voxel the ray enters by, or starts in; on the axis it enters across, the first layer on that side.
        int vx = axis == 0 ? (dx > 0 ? 0 : size.X - 1) : Math.Clamp((int)Math.Floor(ox + dx * t), 0, size.X - 1),
            vy = axis == 1 ? (dy > 0 ? 0 : size.Y - 1) : Math.Clamp((int)Math.Floor(oy + dy * t), 0, size.Y - 1),
            vz = axis == 2 ? (dz > 0 ? 0 : size.Z - 1) : Math.Clamp((int)Math.Floor(oz + dz * t), 0, size.Z - 1);
        for (var step = 0; step < 4 * VoxelConstants.ChunkSize; step++)
        {
            if ((uint)vx >= (uint)size.X || (uint)vy >= (uint)size.Y || (uint)vz >= (uint)size.Z || t > exit)
            {
                return false;
            }

            var block = Probe(memory, chunk, vx, vy, vz, out material);
            if (material != VoxelConstants.Air)
            {
                voxel = new VoxelCoord(vx, vy, vz);
                return true;
            }

            // Leave the air block through its nearest face, as the world's walk does.
            int lx = vx & ~(block - 1),
                ly = vy & ~(block - 1),
                lz = vz & ~(block - 1);
            var ex = RayExit(lx, block, ox, dx);
            var ey = RayExit(ly, block, oy, dy);
            var ez = RayExit(lz, block, oz, dz);
            var next = Math.Min(ex, Math.Min(ey, ez));
            axis =
                next == ex ? 0
                : next == ey ? 1
                : 2;
            t = Math.Max(next, t);
            vx = RayForward((int)Math.Floor(ox + dx * t), vx, dx);
            vy = RayForward((int)Math.Floor(oy + dy * t), vy, dy);
            vz = RayForward((int)Math.Floor(oz + dz * t), vz, dz);
            switch (axis)
            {
                case 0:
                    vx = dx > 0 ? lx + block : lx - 1;
                    break;
                case 1:
                    vy = dy > 0 ? ly + block : ly - 1;
                    break;
                default:
                    vz = dz > 0 ? lz + block : lz - 1;
                    break;
            }
        }

        return false;
    }

    /// <summary>Distance along a ray to the face it leaves a block of <paramref name="size"/> by, on one axis.</summary>
    internal static double RayExit(int low, int size, double origin, double direction) =>
        direction > 0 ? (low + size - origin) / direction
        : direction < 0 ? (low - origin) / direction
        : double.PositiveInfinity;

    /// <summary>A ray's new voxel coordinate on one axis, but never behind the old one along the ray.</summary>
    internal static int RayForward(int floored, int current, double direction) =>
        direction > 0 ? Math.Max(floored, current)
        : direction < 0 ? Math.Min(floored, current)
        : current;

    /// <summary>
    /// Narrows a ray's range [enter, exit] to the slab from 0 to <paramref name="size"/> on one axis, and records the
    /// axis when the ray enters later across it; false when the ray runs beside the slab.
    /// </summary>
    private static bool Clip(
        double origin,
        double direction,
        int size,
        int slab,
        ref double enter,
        ref double exit,
        ref int axis
    )
    {
        if (direction == 0)
        {
            return origin >= 0 && origin < size;
        }

        var near = (direction > 0 ? 0 - origin : size - origin) / direction;
        var far = (direction > 0 ? size - origin : 0 - origin) / direction;
        if (near > enter)
        {
            enter = near;
            axis = slab;
        }

        exit = Math.Min(exit, far);
        return true;
    }

    /// <summary>Frees a state's nodes and bricks. <paramref name="depth"/> is 2 for a chunk, 1 for a cell, 0 for a brick.</summary>
    public static void Free(VoxelMemory memory, ChildState state, int depth)
    {
        if (state.Kind != ChildKind.Handle)
        {
            return;
        }

        if (depth == 0)
        {
            BrickCodec.Free(memory, state.Handle);
            return;
        }

        var node = memory.Node(state.Handle);
        for (var mask = node->ChildMask & ~node->UniformMask; mask != 0; mask &= mask - 1)
        {
            Free(memory, GetChild(memory, state.Handle, BitOperations.TrailingZeroCount(mask)), depth - 1);
        }

        FreeNode(memory, state.Handle);
    }

    /// <summary>Adds the chunk's node and stored brick counts to the totals.</summary>
    public static void Count(VoxelMemory memory, ChildState chunk, ref int nodes, ref int bricks)
    {
        if (chunk.Kind != ChildKind.Handle)
        {
            return;
        }

        nodes++;
        var root = memory.Node(chunk.Handle);
        for (var mask = root->ChildMask & ~root->UniformMask; mask != 0; mask &= mask - 1)
        {
            var cell = GetChild(memory, chunk.Handle, BitOperations.TrailingZeroCount(mask));
            var node = memory.Node(cell.Handle);
            nodes++;
            bricks += BitOperations.PopCount(node->ChildMask & ~node->UniformMask);
        }
    }

    /// <summary>The state of child <paramref name="child"/> (0 to 63) of a node.</summary>
    public static ChildState GetChild(VoxelMemory memory, int node, int child)
    {
        var record = memory.Node(node);
        var bit = 1UL << child;
        if ((record->ChildMask & bit) == 0)
        {
            return ChildState.Empty;
        }

        var rank = BitOperations.PopCount(record->ChildMask & (bit - 1));
        var entry = memory.List(record->ListClass, record->List)[rank];
        return (record->UniformMask & bit) != 0
            ? new ChildState(ChildKind.Uniform, (int)entry)
            : ChildState.Of((int)entry);
    }

    /// <summary>
    /// Sets child <paramref name="child"/> of a node, growing or shrinking its list. Does not free what the child held
    /// and does not collapse the node. Any thread that owns the tree; it may pass the budget.
    /// </summary>
    public static void SetChild(VoxelMemory memory, int node, int child, ChildState state)
    {
        var record = memory.Node(node);
        var bit = 1UL << child;
        var present = (record->ChildMask & bit) != 0;
        var count = BitOperations.PopCount(record->ChildMask);
        var rank = BitOperations.PopCount(record->ChildMask & (bit - 1));

        if (state.IsEmpty)
        {
            if (!present)
            {
                return;
            }

            var list = memory.List(record->ListClass, record->List);
            new Span<uint>(list + rank + 1, count - rank - 1).CopyTo(new Span<uint>(list + rank, count - rank - 1));
            record->ChildMask &= ~bit;
            record->UniformMask &= ~bit;
            count--;
            if (count == 0)
            {
                FreeList(memory, record);
            }
            else if (record->ListClass > 0 && count <= ListCapacity(record->ListClass - 1) / 2)
            {
                MoveList(memory, record, record->ListClass - 1, count);
            }

            return;
        }

        var value = state.Kind == ChildKind.Uniform ? state.Material : (uint)state.Handle;
        if (!present)
        {
            if (count == 0)
            {
                record->List = memory.Allocate(ListPool(0));
                record->ListClass = 0;
            }
            else if (count == ListCapacity(record->ListClass))
            {
                MoveList(memory, record, record->ListClass + 1, count);
            }

            var list = memory.List(record->ListClass, record->List);
            new Span<uint>(list + rank, count - rank).CopyTo(new Span<uint>(list + rank + 1, count - rank));
            record->ChildMask |= bit;
        }

        memory.List(record->ListClass, record->List)[rank] = value;
        if (state.Kind == ChildKind.Uniform)
        {
            record->UniformMask |= bit;
        }
        else
        {
            record->UniformMask &= ~bit;
        }
    }

    /// <summary>
    /// Returns the state a node stands for: empty or uniform when all its children are (the node is then freed), or
    /// the node itself.
    /// </summary>
    public static ChildState Collapse(VoxelMemory memory, int node)
    {
        var record = memory.Node(node);
        if (record->ChildMask == 0)
        {
            FreeNode(memory, node);
            return ChildState.Empty;
        }

        if (record->ChildMask == ulong.MaxValue && record->UniformMask == ulong.MaxValue)
        {
            var list = new ReadOnlySpan<uint>(
                memory.List(record->ListClass, record->List),
                VoxelConstants.NodeChildren
            );
            var material = list[0];
            if (list.IndexOfAnyExcept(material) < 0)
            {
                FreeNode(memory, node);
                return ChildState.Uniform((byte)material);
            }
        }

        return ChildState.Of(node);
    }

    /// <summary>
    /// Stores a node for 64 child states, or returns empty or uniform when they all are. When memory runs out, nothing
    /// is kept (the children are not freed) and <paramref name="outOfMemory"/> is true.
    /// </summary>
    public static ChildState BuildNode(
        VoxelMemory memory,
        ReadOnlySpan<ChildState> children,
        bool mayExceedBudget,
        out bool outOfMemory
    )
    {
        outOfMemory = false;
        var first = children[0];
        if (first.IsUniformOrEmpty && children[..VoxelConstants.NodeChildren].IndexOfAnyExcept(first) < 0)
        {
            return first;
        }

        ulong childMask = 0;
        ulong uniformMask = 0;
        var count = 0;
        for (var i = 0; i < VoxelConstants.NodeChildren; i++)
        {
            if (!children[i].IsEmpty)
            {
                childMask |= 1UL << i;
                count++;
                if (children[i].Kind == ChildKind.Uniform)
                {
                    uniformMask |= 1UL << i;
                }
            }
        }

        var listClass = ListClassFor(count);
        if (!memory.TryAllocate(VoxelPool.Nodes, mayExceedBudget, out var node))
        {
            outOfMemory = true;
            return ChildState.Empty;
        }

        if (!memory.TryAllocate(ListPool(listClass), mayExceedBudget, out var listHandle))
        {
            memory.Free(VoxelPool.Nodes, node);
            outOfMemory = true;
            return ChildState.Empty;
        }

        var record = memory.Node(node);
        record->ChildMask = childMask;
        record->UniformMask = uniformMask;
        record->List = listHandle;
        record->ListClass = listClass;
        var list = memory.List(listClass, listHandle);
        var rank = 0;
        for (var i = 0; i < VoxelConstants.NodeChildren; i++)
        {
            var child = children[i];
            if (!child.IsEmpty)
            {
                list[rank++] = child.Kind == ChildKind.Uniform ? child.Material : (uint)child.Handle;
            }
        }

        return ChildState.Of(node);
    }

    /// <summary>A new node whose 64 children are all <paramref name="state"/> (empty or uniform).</summary>
    private static int NewUniformNode(VoxelMemory memory, ChildState state)
    {
        var node = memory.Allocate(VoxelPool.Nodes);
        var record = memory.Node(node);
        if (state.IsEmpty)
        {
            record->ChildMask = 0;
            record->UniformMask = 0;
            record->List = NoList;
            record->ListClass = NoList;
            return node;
        }

        var listClass = ListClasses - 1;
        record->ChildMask = ulong.MaxValue;
        record->UniformMask = ulong.MaxValue;
        record->List = memory.Allocate(ListPool(listClass));
        record->ListClass = listClass;
        new Span<uint>(memory.List(listClass, record->List), VoxelConstants.NodeChildren).Fill(state.Material);
        return node;
    }

    private static void MoveList(VoxelMemory memory, NodeRecord* record, int listClass, int count)
    {
        var list = memory.Allocate(ListPool(listClass));
        new Span<uint>(memory.List(record->ListClass, record->List), count).CopyTo(
            new Span<uint>(memory.List(listClass, list), count)
        );
        memory.Free(ListPool(record->ListClass), record->List);
        record->List = list;
        record->ListClass = listClass;
    }

    private static void FreeList(VoxelMemory memory, NodeRecord* record)
    {
        if (record->List != NoList)
        {
            memory.Free(ListPool(record->ListClass), record->List);
        }

        record->List = NoList;
        record->ListClass = NoList;
    }

    private static void FreeNode(VoxelMemory memory, int node)
    {
        FreeList(memory, memory.Node(node));
        memory.Free(VoxelPool.Nodes, node);
    }

    private static void FreeAll(VoxelMemory memory, ReadOnlySpan<ChildState> states, int depth)
    {
        foreach (var state in states)
        {
            Free(memory, state, depth);
        }
    }

    private static int ListClassFor(int count) =>
        count switch
        {
            <= 8 => 0,
            <= 16 => 1,
            <= 32 => 2,
            _ => 3,
        };

    private static VoxelPool ListPool(int listClass) => VoxelPool.List8 + listClass;

    /// <summary>The box of a whole chunk.</summary>
    private static VoxelCoord Whole =>
        new(VoxelConstants.ChunkSize, VoxelConstants.ChunkSize, VoxelConstants.ChunkSize);

    /// <summary>Makes air of a gathered brick's voxels that lie outside the box up to <paramref name="extent"/>.</summary>
    private static void ClearOutside(Span<byte> brick, int x, int y, int z, VoxelCoord extent)
    {
        if (
            x + VoxelConstants.BrickSize <= extent.X
            && y + VoxelConstants.BrickSize <= extent.Y
            && z + VoxelConstants.BrickSize <= extent.Z
        )
        {
            return;
        }

        for (var bz = 0; bz < VoxelConstants.BrickSize; bz++)
        {
            for (var by = 0; by < VoxelConstants.BrickSize; by++)
            {
                for (var bx = 0; bx < VoxelConstants.BrickSize; bx++)
                {
                    if (x + bx >= extent.X || y + by >= extent.Y || z + bz >= extent.Z)
                    {
                        brick[bx + (by << 3) + (bz << 6)] = VoxelConstants.Air;
                    }
                }
            }
        }
    }

    private static void Gather(ReadOnlySpan<byte> voxels, int x, int y, int z, Span<byte> brick)
    {
        for (var bz = 0; bz < VoxelConstants.BrickSize; bz++)
        {
            for (var by = 0; by < VoxelConstants.BrickSize; by++)
            {
                var source =
                    x + ((y + by) << VoxelConstants.ChunkShift) + ((z + bz) << (2 * VoxelConstants.ChunkShift));
                voxels.Slice(source, VoxelConstants.BrickSize).CopyTo(brick.Slice((by << 3) + (bz << 6), 8));
            }
        }
    }

    private static void Scatter(ReadOnlySpan<byte> brick, int x, int y, int z, Span<byte> voxels)
    {
        for (var bz = 0; bz < VoxelConstants.BrickSize; bz++)
        {
            for (var by = 0; by < VoxelConstants.BrickSize; by++)
            {
                var target =
                    x + ((y + by) << VoxelConstants.ChunkShift) + ((z + bz) << (2 * VoxelConstants.ChunkShift));
                brick.Slice((by << 3) + (bz << 6), 8).CopyTo(voxels.Slice(target, VoxelConstants.BrickSize));
            }
        }
    }
}
