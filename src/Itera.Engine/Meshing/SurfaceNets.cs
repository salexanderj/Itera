using System.Numerics;
using System.Runtime.CompilerServices;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Voxels;

namespace Itera.Engine.Meshing;

/// <summary>
/// Makes the surface mesh of one 32x32x32 block of voxels with naive Surface Nets. It reads <see cref="SampleCount"/>
/// material bytes (0 is air, anything else is solid) and as many densities (<see cref="VoxelDensity"/>): the block's
/// voxels and an apron of <see cref="Apron"/> voxels on every side, at <see cref="SampleIndex"/>. Block-local voxel v
/// spans [v, v + 1) on each axis, so its centre is at v + 0.5.
/// <para>
/// A cell is the cube between 8 voxel centres; cell c, named by its lowest voxel, has the corners c + {0,1}^3, and the
/// mesher meets the cells c in [-1, 31]^3. A cell has a vertex only when a quad uses it, at the mean of the points where
/// the density crosses one half along those of its 12 edges whose corner voxels differ in solidity (the midpoint when
/// the densities are a solid voxel's and an air voxel's defaults). A block owns the edges between voxels whose lower
/// voxel is inside it; each edge that crosses the surface gives one quad of the 4 cells that share it. So a vertex
/// depends on its cell's corners alone, blocks side by side make the same vertex for a cell they both meet, and none
/// duplicates or misses a quad. A vertex's normal and material come from the voxels round its cell, its openness from
/// the densities round the vertex itself (see <see cref="MeshVertex"/>), and its weights from the materials of its cell's
/// surface corners: the mesh's palette (<see cref="MeshBuffers.Palette"/>) holds its surface's materials, the most common
/// first, and each vertex how many of its surface corners are each (one past the palette's eighth counts as the vertex's
/// own material), so the borders between materials can be blended across the triangles.
/// </para>
/// <para>
/// Each quad is split along its shorter diagonal; of two equal diagonals it takes the one from the first vertex to the
/// third, in the order (low, low), (high j, low), (high j, high k), (low j, high k) where j and k count cells back along
/// the quad's two other axes in the cyclic order x, y, z. The triangles wind so that their geometric normal points from
/// the solid voxel of the edge to its air voxel.
/// </para>
/// <para>
/// A block beside a column drawn one level coarser is meshed in transition mode (<see cref="MeshTransition"/>): the
/// cells that straddle that column's boundary take the coarser level's own vertex where they lie, computed from the
/// coarser voxels as the coarser block computes it, and where the coarser column lies past the block's high face, the
/// block also makes the coarser level's quads of the edges that cross that face, which the coarser block leaves to the
/// block below them; so the two levels share their seam's vertices and meet without gaps (transition cells). A cell's vertex is snapped where any column round it is coarser, so the blocks
/// that share it agree.
/// </para>
/// Not thread safe: it holds scratch, so each worker thread has its own. After its buffers have grown to fit a mesh, a
/// call allocates nothing.
/// </summary>
public sealed class SurfaceNets
{
    /// <summary>Voxels along each side of a block.</summary>
    public const int BlockSize = 32;

    /// <summary>Voxels the input holds beyond each side of the block.</summary>
    public const int Apron = 2;

    /// <summary>Samples along each side of the input.</summary>
    public const int SampleSide = BlockSize + 2 * Apron;

    public const int SampleCount = SampleSide * SampleSide * SampleSide;

    private const int CellSide = BlockSize + 1;
    private const int CellCount = CellSide * CellSide * CellSide;
    private const int StepY = SampleSide;
    private const int StepZ = SampleSide * SampleSide;

    // Corner k of a cell is (k & 1, (k >> 1) & 1, k >> 2). Edge e joins corners EdgeLow[e] and EdgeLow[e] + EdgeStep[e]:
    // four along x, four along y, four along z. EdgeMid is the edge's midpoint from the cell's lowest voxel's low
    // corner (c), in half voxels.
    private static readonly int[] EdgeLow = [0, 2, 4, 6, 0, 1, 4, 5, 0, 1, 2, 3];
    private static readonly int[] EdgeAxis = [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2];
    private static readonly int[] EdgeMidX = [2, 2, 2, 2, 1, 3, 1, 3, 1, 3, 1, 3];
    private static readonly int[] EdgeMidY = [1, 3, 1, 3, 2, 2, 2, 2, 1, 1, 3, 3];
    private static readonly int[] EdgeMidZ = [1, 1, 3, 3, 1, 1, 3, 3, 2, 2, 2, 2];

    // The cell's corners in the order that settles a tie of materials: the upper four first, then z, then x ascending.
    private static readonly int[] MaterialOrder = [2, 3, 6, 7, 0, 1, 4, 5];

    // Sample offsets from a cell's lowest corner to each of its corners.
    private static readonly int[] CornerOffset =
    [
        0,
        1,
        StepY,
        StepY + 1,
        StepZ,
        StepZ + 1,
        StepZ + StepY,
        StepZ + StepY + 1,
    ];

    private static readonly int[] Slope = [-1, -1, 1, 1];
    private static readonly int[] Smooth = [1, 3, 3, 1];

    private readonly uint[] _stamp = new uint[CellCount];
    private readonly int[] _vertexOfCell = new int[CellCount];
    private readonly Vector3[] _positions = new Vector3[CellCount + CoarseCellCount];

    // The coarser level's cells a transition mesh uses, [-1, 31] on each axis of the coarser block (CoarseCellInRange),
    // and their vertices.
    private const int CoarseCellSide = BlockSize + 1;
    private const int CoarseCellCount = CoarseCellSide * CoarseCellSide * CoarseCellSide;
    private readonly uint[] _coarseStamp = new uint[CoarseCellCount];
    private readonly int[] _vertexOfCoarseCell = new int[CoarseCellCount];

    // The densities of a mesh given only materials: each voxel's default.
    private readonly byte[] _defaults = new byte[SampleCount];

    // Each vertex's surface corners' materials (0 for a corner that is not one), eight to a vertex, for the weights
    // (grown by doubling as meshes need, as MeshBuffers grows); the count of each material over them; and each
    // material's palette slot (NoSlot for none).
    private const byte NoSlot = 255;
    private byte[] _cornerMaterials = new byte[4096 * 8];
    private readonly int[] _materialCounts = new int[256];
    private readonly byte[] _slotOf = new byte[256];
    private uint _generation;

    /// <summary>The index of the sample at sample coordinates 0..35 on each axis.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SampleIndex(int x, int y, int z) => x + SampleSide * (y + SampleSide * z);

    /// <summary>
    /// Clears <paramref name="output"/> and fills it with the mesh of a block given only materials, every voxel at its
    /// material's default density. Returns the number of triangles.
    /// </summary>
    /// <exception cref="ArgumentException">The span's length is not <see cref="SampleCount"/>.</exception>
    public int Mesh(ReadOnlySpan<byte> samples, MeshBuffers output) => Mesh(samples, default, output);

    /// <summary>
    /// Clears <paramref name="output"/> and fills it with the block's mesh from its materials and
    /// <paramref name="densities"/> (empty for every voxel's default). Returns the number of triangles.
    /// </summary>
    /// <exception cref="ArgumentException">A span's length is not <see cref="SampleCount"/>.</exception>
    public int Mesh(ReadOnlySpan<byte> samples, ReadOnlySpan<byte> densities, MeshBuffers output) =>
        Mesh(samples, densities, default, default, s_wholeEnd, output);

    /// <summary>
    /// <see cref="Mesh(ReadOnlySpan{byte}, ReadOnlySpan{byte}, MeshBuffers)"/> owning only the edges whose lower voxel lies
    /// in the block-local box from <paramref name="ownedMin"/> up to but not including <paramref name="ownedMax"/> (each
    /// within 0 to <see cref="BlockSize"/>): for a model in parts that each read their neighbours' voxels round them, so the
    /// faces between two parts are made once, by the part that holds their lower voxel. Vertices still come from every
    /// cell a quad uses, the apron's too.
    /// </summary>
    /// <exception cref="ArgumentException">A span's length is not <see cref="SampleCount"/>.</exception>
    public int Mesh(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<byte> densities,
        VoxelCoord ownedMin,
        VoxelCoord ownedMax,
        MeshBuffers output
    ) =>
        Mesh(
            samples,
            densities,
            default,
            new VoxelCoord(
                Math.Clamp(ownedMin.X, 0, BlockSize),
                Math.Clamp(ownedMin.Y, 0, BlockSize),
                Math.Clamp(ownedMin.Z, 0, BlockSize)
            ),
            new VoxelCoord(
                Math.Clamp(ownedMax.X, 0, BlockSize),
                Math.Clamp(ownedMax.Y, 0, BlockSize),
                Math.Clamp(ownedMax.Z, 0, BlockSize)
            ),
            output
        );

    // The whole block, from its corner (VoxelCoord's default) to s_wholeEnd: what a block owns unless told otherwise.
    private static readonly VoxelCoord s_wholeEnd = new(BlockSize, BlockSize, BlockSize);

    /// <summary>
    /// <see cref="Mesh(ReadOnlySpan{byte}, ReadOnlySpan{byte}, MeshBuffers)"/> for a block beside columns drawn one level
    /// coarser (<paramref name="sides"/>): <paramref name="coarseSamples"/> and <paramref name="coarseDensities"/> are
    /// the coarser level's block that holds this one (its voxels and apron, as a block of that level is read), in which
    /// this block's low corner lies at <paramref name="coarseOffset"/> coarser voxels (0 or 16 on each axis).
    /// </summary>
    /// <exception cref="ArgumentException">A span's length is not <see cref="SampleCount"/>, or an offset is not 0 or 16.</exception>
    public int MeshTransition(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<byte> densities,
        ReadOnlySpan<byte> coarseSamples,
        ReadOnlySpan<byte> coarseDensities,
        VoxelCoord coarseOffset,
        TransitionSides sides,
        MeshBuffers output
    )
    {
        if (coarseSamples.Length != SampleCount || coarseDensities.Length != SampleCount)
        {
            throw new ArgumentException(
                $"The coarser block holds {SampleCount} samples and densities.",
                nameof(coarseSamples)
            );
        }

        if (!IsHalf(coarseOffset.X) || !IsHalf(coarseOffset.Y) || !IsHalf(coarseOffset.Z))
        {
            throw new ArgumentException("A block lies at 0 or 16 voxels in the coarser block.", nameof(coarseOffset));
        }

        return Mesh(
            samples,
            densities,
            new Coarse(coarseSamples, coarseDensities, coarseOffset, sides),
            default,
            s_wholeEnd,
            output
        );
    }

    private static bool IsHalf(int offset) => offset is 0 or BlockSize / 2;

    private int Mesh(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<byte> densities,
        in Coarse coarse,
        VoxelCoord ownedMin,
        VoxelCoord ownedMax,
        MeshBuffers output
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        if (samples.Length != SampleCount)
        {
            throw new ArgumentException(
                $"The mesher reads {SampleCount} samples, not {samples.Length}.",
                nameof(samples)
            );
        }

        if (!densities.IsEmpty && densities.Length != SampleCount)
        {
            throw new ArgumentException(
                $"The mesher reads {SampleCount} densities, not {densities.Length}.",
                nameof(densities)
            );
        }

        output.Clear();
        if (!HasCrossing(samples) && !coarse.HasHighFace)
        {
            return 0;
        }

        if (densities.IsEmpty)
        {
            VoxelDensity.Defaults(samples, _defaults);
            densities = _defaults;
        }

        NextGeneration();
        for (var z = ownedMin.Z; z < ownedMax.Z; z++)
        {
            for (var y = ownedMin.Y; y < ownedMax.Y; y++)
            {
                var row = SampleIndex(Apron, y + Apron, z + Apron);
                for (var x = ownedMin.X; x < ownedMax.X; x++)
                {
                    var i = row + x;
                    var solid = samples[i] != 0;
                    if (solid != (samples[i + 1] != 0))
                    {
                        EmitQuad(samples, densities, in coarse, output, 0, x, y, z, solid);
                    }

                    if (solid != (samples[i + StepY] != 0))
                    {
                        EmitQuad(samples, densities, in coarse, output, 1, x, y, z, solid);
                    }

                    if (solid != (samples[i + StepZ] != 0))
                    {
                        EmitQuad(samples, densities, in coarse, output, 2, x, y, z, solid);
                    }
                }
            }
        }

        if (coarse.HasHighFace)
        {
            EmitCoarseFaces(in coarse, output);
        }

        FillWeights(output);
        return output.IndexCount / 3;
    }

    /// <summary>
    /// The mesh's palette, its surface's materials by how many of the vertices' surface corners have each, the most first
    /// (of equal counts the lower id), at most <see cref="MeshBuffers.PaletteSize"/>; and each vertex's weights, how many
    /// of its surface corners are each of them, a material past the palette counting as the vertex's own.
    /// </summary>
    private void FillWeights(MeshBuffers output)
    {
        var vertexCount = output.VertexCount;
        var corners = _cornerMaterials.AsSpan(0, vertexCount * 8);
        Array.Clear(_materialCounts);
        foreach (var material in corners)
        {
            _materialCounts[material]++;
        }

        _materialCounts[0] = 0;
        Array.Fill(_slotOf, NoSlot);
        ulong palette = 0;
        for (var slot = 0; slot < MeshBuffers.PaletteSize; slot++)
        {
            var best = 0;
            for (var material = 1; material < 256; material++)
            {
                if (_materialCounts[material] > _materialCounts[best] && _slotOf[material] == NoSlot)
                {
                    best = material;
                }
            }

            if (best == 0)
            {
                break;
            }

            _slotOf[best] = (byte)slot;
            palette |= (ulong)best << (8 * slot);
        }

        var overflowed = false;
        var vertices = output.Vertices;
        for (var v = 0; v < vertexCount; v++)
        {
            var own = _slotOf[MeshVertex.DecodeMaterial(in vertices[v])];
            uint weights = 0;
            for (var k = 0; k < 8; k++)
            {
                var material = corners[v * 8 + k];
                if (material == 0)
                {
                    continue;
                }

                var slot = _slotOf[material];
                if (slot == NoSlot)
                {
                    overflowed = true;
                    slot = own == NoSlot ? (byte)0 : own;
                }

                weights += 1u << (slot * 4);
            }

            vertices[v].Weights = weights;
        }

        output.Palette = palette;
        output.PaletteOverflowed = overflowed;
    }

    /// <summary>Records the materials of the surface corners of the cell at <paramref name="corner"/> as vertex <paramref name="index"/>'s.</summary>
    private void RecordCorners(ReadOnlySpan<byte> samples, int corner, int index)
    {
        if ((index + 1) * 8 > _cornerMaterials.Length)
        {
            Array.Resize(ref _cornerMaterials, _cornerMaterials.Length * 2);
        }

        var surface = SurfaceCorners(samples, corner);
        for (var k = 0; k < 8; k++)
        {
            _cornerMaterials[index * 8 + k] = (surface & (1 << k)) != 0 ? samples[corner + CornerOffset[k]] : (byte)0;
        }
    }

    /// <summary>
    /// The coarser level's quads of the edges that cross the block's high x and z faces where the coarser column lies
    /// past them: the coarser block owns the edges whose lower voxel is in it, so the block below them makes them. Each
    /// coarser edge whose other two coordinates fall in this block's half is made here once.
    /// </summary>
    private void EmitCoarseFaces(in Coarse coarse, MeshBuffers output)
    {
        var offset = coarse.Offset;
        const int half = BlockSize / 2;
        if ((coarse.Sides & TransitionSides.HighX) != 0)
        {
            var x = offset.X + half - 1;
            for (var z = offset.Z; z < offset.Z + half; z++)
            {
                for (var y = offset.Y; y < offset.Y + half; y++)
                {
                    var i = SampleIndex(x + Apron, y + Apron, z + Apron);
                    var solid = coarse.Samples[i] != 0;
                    if (solid != (coarse.Samples[i + 1] != 0))
                    {
                        EmitCoarseQuad(in coarse, output, 0, x, y, z, solid);
                    }
                }
            }
        }

        if ((coarse.Sides & TransitionSides.HighZ) != 0)
        {
            var z = offset.Z + half - 1;
            for (var y = offset.Y; y < offset.Y + half; y++)
            {
                for (var x = offset.X; x < offset.X + half; x++)
                {
                    var i = SampleIndex(x + Apron, y + Apron, z + Apron);
                    var solid = coarse.Samples[i] != 0;
                    if (solid != (coarse.Samples[i + StepZ] != 0))
                    {
                        EmitCoarseQuad(in coarse, output, 2, x, y, z, solid);
                    }
                }
            }
        }
    }

    /// <summary>The quad of the coarser edge from coarser voxel (x, y, z) along <paramref name="axis"/>, as <see cref="EmitQuad"/>.</summary>
    private void EmitCoarseQuad(in Coarse coarse, MeshBuffers output, int axis, int x, int y, int z, bool lowIsSolid)
    {
        var (jx, jy, jz, kx, ky, kz) = QuadSteps(axis);
        var q0 = CoarseVertex(in coarse, output, x, y, z);
        var q1 = CoarseVertex(in coarse, output, x + jx, y + jy, z + jz);
        var q2 = CoarseVertex(in coarse, output, x + jx + kx, y + jy + ky, z + jz + kz);
        var q3 = CoarseVertex(in coarse, output, x + kx, y + ky, z + kz);
        AddQuad(output, q0, q1, q2, q3, lowIsSolid);
    }

    /// <summary>
    /// True when some edge the block owns crosses the surface. Those edges join the voxels of [0, 32]^3 of which at
    /// most one coordinate is 32, which are all alike exactly when no owned edge crosses.
    /// </summary>
    private static bool HasCrossing(ReadOnlySpan<byte> samples)
    {
        var first = samples[SampleIndex(Apron, Apron, Apron)] != 0;
        for (var z = Apron; z <= BlockSize + Apron; z++)
        {
            for (var y = Apron; y <= BlockSize + Apron; y++)
            {
                var pastY = y == BlockSize + Apron;
                var pastZ = z == BlockSize + Apron;
                if (pastY && pastZ)
                {
                    continue;
                }

                var length = pastY || pastZ ? BlockSize : BlockSize + 1;
                var row = samples.Slice(SampleIndex(Apron, y, z), length);
                if (first ? row.Contains((byte)0) : row.IndexOfAnyExcept((byte)0) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The coarser level's block and the block's place in it, for a transition mesh; default for none.</summary>
    private readonly ref struct Coarse(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<byte> densities,
        VoxelCoord offset,
        TransitionSides sides
    )
    {
        public readonly ReadOnlySpan<byte> Samples = samples;
        public readonly ReadOnlySpan<byte> Densities = densities;
        public readonly VoxelCoord Offset = offset;
        public readonly TransitionSides Sides = sides;

        /// <summary>True when a coarser column lies past the block's high x or z face.</summary>
        public bool HasHighFace => (Sides & (TransitionSides.HighX | TransitionSides.HighZ)) != 0;

        /// <summary>
        /// True when cell (cx, *, cz) straddles the boundary of a coarser column: a cell at -1 or 31 on x or z straddles
        /// the block's face there; one at a corner straddles two faces and the corner, and snaps where any of the three
        /// columns past them is coarser.
        /// </summary>
        public bool Snaps(int cx, int cz)
        {
            var sx =
                cx < 0 ? -1
                : cx == BlockSize - 1 ? 1
                : 0;
            var sz =
                cz < 0 ? -1
                : cz == BlockSize - 1 ? 1
                : 0;
            if (sx == 0 && sz == 0)
            {
                return false;
            }

            if (sx != 0 && (Sides & (sx < 0 ? TransitionSides.LowX : TransitionSides.HighX)) != 0)
            {
                return true;
            }

            if (sz != 0 && (Sides & (sz < 0 ? TransitionSides.LowZ : TransitionSides.HighZ)) != 0)
            {
                return true;
            }

            if (sx == 0 || sz == 0)
            {
                return false;
            }

            var corner =
                sx < 0
                    ? (sz < 0 ? TransitionSides.LowXLowZ : TransitionSides.LowXHighZ)
                    : (sz < 0 ? TransitionSides.HighXLowZ : TransitionSides.HighXHighZ);
            return (Sides & corner) != 0;
        }
    }

    private void NextGeneration()
    {
        _generation++;
        if (_generation == 0)
        {
            Array.Clear(_stamp);
            Array.Clear(_coarseStamp);
            _generation = 1;
        }
    }

    /// <summary>
    /// Emits the quad of the edge from block voxel (x, y, z) along <paramref name="axis"/>. Seen from the edge's high
    /// end, the cells (low, low), (high j, low), (high j, high k), (low j, high k) run counter-clockwise, which winds
    /// the triangles towards +axis; an air voxel at the low end turns them round.
    /// </summary>
    private void EmitQuad(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<byte> densities,
        in Coarse coarse,
        MeshBuffers output,
        int axis,
        int x,
        int y,
        int z,
        bool lowIsSolid
    )
    {
        var (jx, jy, jz, kx, ky, kz) = QuadSteps(axis);
        // A quad of four snapped cells lies on a coarser column's boundary: the coarser level's own quads stand there
        // (EmitCoarseFaces, or the coarser block's).
        if (
            coarse.Sides != TransitionSides.None
            && coarse.Snaps(x, z)
            && coarse.Snaps(x + jx, z + jz)
            && coarse.Snaps(x + jx + kx, z + jz + kz)
            && coarse.Snaps(x + kx, z + kz)
        )
        {
            return;
        }

        var q0 = CellVertex(samples, densities, in coarse, output, x, y, z);
        var q1 = CellVertex(samples, densities, in coarse, output, x + jx, y + jy, z + jz);
        var q2 = CellVertex(samples, densities, in coarse, output, x + jx + kx, y + jy + ky, z + jz + kz);
        var q3 = CellVertex(samples, densities, in coarse, output, x + kx, y + ky, z + kz);
        AddQuad(output, q0, q1, q2, q3, lowIsSolid);
    }

    /// <summary>The steps from a quad's first cell to its second (j) and fourth (k) along <paramref name="axis"/>'s quad.</summary>
    private static (int Jx, int Jy, int Jz, int Kx, int Ky, int Kz) QuadSteps(int axis) =>
        axis switch
        {
            0 => (0, -1, 0, 0, 0, -1),
            1 => (0, 0, -1, -1, 0, 0),
            _ => (-1, 0, 0, 0, -1, 0),
        };

    /// <summary>
    /// Adds a quad's two triangles, split along its shorter diagonal; a triangle two of whose vertices are one (cells
    /// snapped to the same coarser vertex) is left out.
    /// </summary>
    private void AddQuad(MeshBuffers output, int q0, int q1, int q2, int q3, bool lowIsSolid)
    {
        var p0 = _positions[q0];
        var p1 = _positions[q1];
        var p2 = _positions[q2];
        var p3 = _positions[q3];
        var firstDiagonal = Vector3.DistanceSquared(p0, p2);
        var secondDiagonal = Vector3.DistanceSquared(p1, p3);
        if (firstDiagonal <= secondDiagonal)
        {
            AddTriangle(output, q0, q1, q2, lowIsSolid);
            AddTriangle(output, q0, q2, q3, lowIsSolid);
        }
        else
        {
            AddTriangle(output, q0, q1, q3, lowIsSolid);
            AddTriangle(output, q1, q2, q3, lowIsSolid);
        }
    }

    private static void AddTriangle(MeshBuffers output, int a, int b, int c, bool forward)
    {
        if (a == b || b == c || a == c)
        {
            return;
        }

        if (forward)
        {
            output.AddTriangle(a, b, c);
        }
        else
        {
            output.AddTriangle(a, c, b);
        }
    }

    /// <summary>
    /// The index of the vertex of block-local cell (cx, cy, cz), made on first use: a cell snapped to a coarser column
    /// (<see cref="Coarse.Snaps"/>) takes the coarser cell's vertex where its own would lie (<see cref="SnappedVertex"/>).
    /// </summary>
    private int CellVertex(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<byte> densities,
        in Coarse coarse,
        MeshBuffers output,
        int cx,
        int cy,
        int cz
    )
    {
        var key = cx + 1 + CellSide * (cy + 1 + CellSide * (cz + 1));
        if (_stamp[key] == _generation)
        {
            return _vertexOfCell[key];
        }

        MakeVertex(
            samples,
            densities,
            cx,
            cy,
            cz,
            out var position,
            out var normal,
            out var material,
            out var openness
        );
        var index =
            coarse.Sides != TransitionSides.None && coarse.Snaps(cx, cz)
                ? SnappedVertex(in coarse, output, cx, cy, cz, position)
                : -1;
        if (index < 0)
        {
            index = output.AddVertex(MeshVertex.Pack(position, normal, material, openness));
            _positions[index] = position;
            RecordCorners(samples, SampleIndex(cx + Apron, cy + Apron, cz + Apron), index);
        }

        _stamp[key] = _generation;
        _vertexOfCell[key] = index;
        return index;
    }

    /// <summary>
    /// The vertex a cell straddling a coarser column's boundary takes: that of the coarser cell over it, each coarser cell
    /// standing over two of these cells along each axis (so cells side by side take the same coarser cell or ones side by
    /// side, never one at a corner), the cell across the boundary on each axis the cell straddles; of a coarser cell the
    /// surface does not cross, the one beside it that it does nearest the cell's own vertex (<paramref name="position"/>,
    /// block-local). -1 where none does, and the cell keeps its own
    /// vertex. Measured over random shapes (<c>SurfaceNetsTransitionTests</c>), this leaves a few holes of a voxel or two
    /// along each seam where the two levels' samples disagree on which cells the surface crosses (choosing by where the
    /// cell's own vertex lay left twice as many).
    /// </summary>
    private int SnappedVertex(in Coarse coarse, MeshBuffers output, int cx, int cy, int cz, Vector3 position)
    {
        var offset = coarse.Offset;
        // Cell c spans the voxel centres c + 0.5 to c + 1.5, its middle at c + 1; the coarser cell over that middle (in
        // the coarser block's voxels), or across the boundary on an axis the cell straddles.
        var ox =
            cx < 0 ? offset.X - 1
            : cx == BlockSize - 1 ? offset.X + BlockSize / 2 - 1
            : CoarseCellOver(cx, offset.X);
        var oz =
            cz < 0 ? offset.Z - 1
            : cz == BlockSize - 1 ? offset.Z + BlockSize / 2 - 1
            : CoarseCellOver(cz, offset.Z);
        var oy = CoarseCellOver(cy, offset.Y);
        if (CoarseCellInRange(ox, oy, oz) && Crosses(coarse.Samples, ox, oy, oz))
        {
            return CoarseVertex(in coarse, output, ox, oy, oz);
        }

        // Along an axis the cell straddles the coarser cell is fixed; along the others it may move by one.
        var freeX = cx >= 0 && cx < BlockSize - 1;
        var freeZ = cz >= 0 && cz < BlockSize - 1;
        // The cell's own vertex in the coarser block's voxels.
        var at = position / 2 + new Vector3(offset.X, offset.Y, offset.Z);
        int bestX = 0,
            bestY = 0,
            bestZ = 0;
        var bestDistance = float.MaxValue;
        for (var dz = freeZ ? -1 : 0; dz <= (freeZ ? 1 : 0); dz++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = freeX ? -1 : 0; dx <= (freeX ? 1 : 0); dx++)
                {
                    int x = ox + dx,
                        y = oy + dy,
                        z = oz + dz;
                    if (
                        !InHalf(x, offset.X)
                        || !InHalf(y, offset.Y)
                        || !InHalf(z, offset.Z)
                        || !Crosses(coarse.Samples, x, y, z)
                    )
                    {
                        continue;
                    }

                    var distance = Vector3.DistanceSquared(at, new Vector3(x + 1, y + 1, z + 1));
                    if (distance < bestDistance)
                    {
                        (bestX, bestY, bestZ, bestDistance) = (x, y, z, distance);
                    }
                }
            }
        }

        return bestDistance < float.MaxValue ? CoarseVertex(in coarse, output, bestX, bestY, bestZ) : -1;
    }

    /// <summary>The coarser cell over the middle of cell <paramref name="cell"/> on one axis, the block at <paramref name="offset"/> coarser voxels.</summary>
    private static int CoarseCellOver(int cell, int offset) => (int)MathF.Floor((cell + 1) / 2f + offset - 0.5f);

    /// <summary>
    /// True for a coarser cell over the block's own cells along one axis, <paramref name="offset"/> - 1 to
    /// <paramref name="offset"/> + 15 (those <see cref="CoarseCellOver"/> gives): its vertex lies within a voxel of the
    /// block, where the block's culling and <see cref="MeshVertex"/>'s positions reach. One further out lies up to three.
    /// </summary>
    private static bool InHalf(int cell, int offset) => cell >= offset - 1 && cell < offset + BlockSize / 2;

    /// <summary>
    /// True for a coarser cell whose vertex the coarser block's samples can make: -1 to 31 on each axis, as a block's own
    /// cells (<see cref="MakeVertex(ReadOnlySpan{byte}, ReadOnlySpan{byte}, int, int, int, out Vector3)"/> reads the
    /// 4x4x4 voxels from the cell's lowest corner - 1).
    /// </summary>
    private static bool CoarseCellInRange(int x, int y, int z) =>
        (uint)(x + 1) <= BlockSize && (uint)(y + 1) <= BlockSize && (uint)(z + 1) <= BlockSize;

    private static int CoarseKey(int x, int y, int z) => x + 1 + CoarseCellSide * (y + 1 + CoarseCellSide * (z + 1));

    /// <summary>True when the surface crosses cell (x, y, z) of a block's samples: its corners are not all alike.</summary>
    private static bool Crosses(ReadOnlySpan<byte> samples, int x, int y, int z)
    {
        var corner = SampleIndex(x + Apron, y + Apron, z + Apron);
        var first = samples[corner] != 0;
        for (var k = 1; k < 8; k++)
        {
            if ((samples[corner + CornerOffset[k]] != 0) != first)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The index of the vertex of the coarser block's cell (x, y, z), made on first use as the coarser block makes it,
    /// its position rounded as that block stores it and given in this block's voxels.
    /// </summary>
    private int CoarseVertex(in Coarse coarse, MeshBuffers output, int x, int y, int z)
    {
        // Past them the samples' rows would wrap: a fault rather than a wrong vertex.
        if (!CoarseCellInRange(x, y, z))
        {
            throw new InvalidOperationException(
                $"Coarser cell ({x}, {y}, {z}) lies outside the coarser block's cells."
            );
        }

        var key = CoarseKey(x, y, z);
        if (_coarseStamp[key] == _generation)
        {
            return _vertexOfCoarseCell[key];
        }

        MakeVertex(
            coarse.Samples,
            coarse.Densities,
            x,
            y,
            z,
            out var coarsePosition,
            out var normal,
            out var material,
            out var openness
        );
        // Rounded as the coarser block stores it, then into this block's voxels (twice as many, exactly).
        var stored = MeshVertex.DecodePosition(MeshVertex.Pack(coarsePosition, normal, material, openness));
        var offset = coarse.Offset;
        var position = (stored - new Vector3(offset.X, offset.Y, offset.Z)) * 2;
        var index = output.AddVertex(MeshVertex.Pack(position, normal, material, openness));
        _positions[index] = position;
        RecordCorners(coarse.Samples, SampleIndex(x + Apron, y + Apron, z + Apron), index);
        _coarseStamp[key] = _generation;
        _vertexOfCoarseCell[key] = index;
        return index;
    }

    private static MeshVertex MakeVertex(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<byte> densities,
        int cx,
        int cy,
        int cz,
        out Vector3 position
    )
    {
        MakeVertex(samples, densities, cx, cy, cz, out position, out var normal, out var material, out var openness);
        return MeshVertex.Pack(position, normal, material, openness);
    }

    private static void MakeVertex(
        ReadOnlySpan<byte> samples,
        ReadOnlySpan<byte> densities,
        int cx,
        int cy,
        int cz,
        out Vector3 position,
        out Vector3 normal,
        out byte material,
        out byte openness
    )
    {
        var corner = SampleIndex(cx + Apron, cy + Apron, cz + Apron);

        var solidMask = 0;
        for (var k = 0; k < 8; k++)
        {
            if (samples[corner + CornerOffset[k]] != 0)
            {
                solidMask |= 1 << k;
            }
        }

        // Where the surface crosses the cell: the mean of the points where the density crosses one half along its
        // crossing edges, from the cell's lowest voxel's centre in voxels.
        var crossings = 0;
        var sum = Vector3.Zero;
        for (var e = 0; e < 12; e++)
        {
            var low = EdgeLow[e];
            var axis = EdgeAxis[e];
            var high = low + (1 << axis);
            if (((solidMask >> low) & 1) == ((solidMask >> high) & 1))
            {
                continue;
            }

            float lowDensity = densities[corner + CornerOffset[low]];
            float highDensity = densities[corner + CornerOffset[high]];
            // Within the edge while each density lies on its material's side of one half (VoxelDensity.Fit); a
            // caller's densities that do not give the midpoint.
            var across = lowDensity - highDensity;
            var share = across != 0 ? Math.Clamp((lowDensity - Half) / across, 0f, 1f) : 0.5f;
            crossings++;
            sum += new Vector3(
                axis == 0 ? share : (EdgeMidX[e] - 1) >> 1,
                axis == 1 ? share : (EdgeMidY[e] - 1) >> 1,
                axis == 2 ? share : (EdgeMidZ[e] - 1) >> 1
            );
        }

        var inCell = sum / crossings;
        position = new Vector3(cx + 0.5f, cy + 0.5f, cz + 0.5f) + inCell;

        // The 4x4x4 voxels from the cell's lowest corner - 1: the density's gradient for the normal, and for the
        // openness each corner's mean of the 3x3x3 densities round it, blended to the vertex's place in the cell (a
        // weight of 1 - f, 1, 1, f along each axis), so the openness follows the vertex and not its cell.
        var gradientX = 0;
        var gradientY = 0;
        var gradientZ = 0;
        Span<float> weightX = stackalloc float[4];
        Span<float> weightY = stackalloc float[4];
        Span<float> weightZ = stackalloc float[4];
        Spread(inCell.X, weightX);
        Spread(inCell.Y, weightY);
        Spread(inCell.Z, weightZ);
        var solid = 0f;
        var window = SampleIndex(cx + Apron - 1, cy + Apron - 1, cz + Apron - 1);
        for (var k = 0; k < 4; k++)
        {
            for (var j = 0; j < 4; j++)
            {
                var row = window + j * StepY + k * StepZ;
                int d0 = densities[row];
                int d1 = densities[row + 1];
                int d2 = densities[row + 2];
                int d3 = densities[row + 3];
                var rowSmooth = d0 + 3 * d1 + 3 * d2 + d3;
                gradientX += (d2 + d3 - d0 - d1) * Smooth[j] * Smooth[k];
                gradientY += rowSmooth * Slope[j] * Smooth[k];
                gradientZ += rowSmooth * Smooth[j] * Slope[k];
                solid +=
                    (d0 * weightX[0] + d1 * weightX[1] + d2 * weightX[2] + d3 * weightX[3]) * weightY[j] * weightZ[k];
            }
        }

        normal = new Vector3(-gradientX, -gradientY, -gradientZ);
        var lengthSquared = (float)gradientX * gradientX + (float)gradientY * gradientY + (float)gradientZ * gradientZ;
        if (lengthSquared > 1e-12f)
        {
            normal /= MathF.Sqrt(lengthSquared);
        }
        else
        {
            normal = CrossingDirection(solidMask);
        }

        // The weights sum to 27, the voxels round one corner.
        openness = (byte)Math.Clamp((int)MathF.Round(VoxelDensity.Full - solid / 27f), 0, VoxelDensity.Full);
        material = MostFrequentMaterial(samples, corner);
    }

    /// <summary>The density at which the surface crosses an edge: halfway between empty and full.</summary>
    private const float Half = VoxelDensity.Full / 2f;

    /// <summary>
    /// The weights of the four voxels along one axis of a cell's 4x4x4 window for a point <paramref name="share"/> of
    /// the way from the cell's low corner to its high one: the low corner's 3 voxels times 1 - share and the high
    /// corner's 3 times share.
    /// </summary>
    private static void Spread(float share, Span<float> weights)
    {
        weights[0] = 1 - share;
        weights[1] = 1;
        weights[2] = 1;
        weights[3] = share;
    }

    /// <summary>
    /// The normalized sum of the directions from solid to air across the cell's crossing edges, or (0, 1, 0) when it is zero.
    /// </summary>
    private static Vector3 CrossingDirection(int solidMask)
    {
        var x = 0;
        var y = 0;
        var z = 0;
        for (var e = 0; e < 12; e++)
        {
            var low = EdgeLow[e];
            var axis = EdgeAxis[e];
            var lowSolid = (solidMask >> low) & 1;
            if (lowSolid == ((solidMask >> (low + (1 << axis))) & 1))
            {
                continue;
            }

            var sign = lowSolid == 1 ? 1 : -1;
            switch (axis)
            {
                case 0:
                    x += sign;
                    break;
                case 1:
                    y += sign;
                    break;
                default:
                    z += sign;
                    break;
            }
        }

        var lengthSquared = (float)(x * x + y * y + z * z);
        return lengthSquared > 0f ? new Vector3(x, y, z) / MathF.Sqrt(lengthSquared) : new Vector3(0f, 1f, 0f);
    }

    /// <summary>
    /// The cell's surface corners as a mask (bit k for corner k): its solid corners with an air corner along one of the
    /// cell's edges.
    /// </summary>
    private static int SurfaceCorners(ReadOnlySpan<byte> samples, int corner)
    {
        // Corner k's neighbours along the cell's edges are k ^ 1 (x), k ^ 2 (y), and k ^ 4 (z).
        var air = 0;
        for (var k = 0; k < 8; k++)
        {
            if (samples[corner + CornerOffset[k]] == 0)
            {
                air |= 1 << k;
            }
        }

        var surface = 0;
        for (var k = 0; k < 8; k++)
        {
            if ((air & (1 << k)) == 0 && (air & ((1 << (k ^ 1)) | (1 << (k ^ 2)) | (1 << (k ^ 4)))) != 0)
            {
                surface |= 1 << k;
            }
        }

        return surface;
    }

    /// <summary>
    /// The material that most of the cell's surface corners have: its solid corners with an air corner along one of the
    /// cell's edges, the voxels a ray from the air meets first, so a voxel under a one-voxel skin (a photo-textured skin over a
    /// head) does not outvote the skin. Of equal counts, the first in <see cref="MaterialOrder"/>.
    /// </summary>
    private static byte MostFrequentMaterial(ReadOnlySpan<byte> samples, int corner)
    {
        var surface = SurfaceCorners(samples, corner);
        byte best = 0;
        var bestCount = 0;
        foreach (var k in MaterialOrder)
        {
            if ((surface & (1 << k)) == 0)
            {
                continue;
            }

            var material = samples[corner + CornerOffset[k]];
            var count = 0;
            for (var m = 0; m < 8; m++)
            {
                if ((surface & (1 << m)) != 0 && samples[corner + CornerOffset[m]] == material)
                {
                    count++;
                }
            }

            if (count > bestCount)
            {
                best = material;
                bestCount = count;
            }
        }

        return best;
    }
}
