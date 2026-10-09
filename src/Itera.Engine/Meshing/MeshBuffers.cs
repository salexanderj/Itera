using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Meshing;

/// <summary>
/// The mesh of one block: its vertices and its triangle-list indices into them (<see cref="SurfaceNets.Mesh(ReadOnlySpan{byte}, ReadOnlySpan{byte}, MeshBuffers)"/>
/// fills it). The arrays grow by doubling when full and never shrink, so a buffer that is reused allocates only
/// while it grows. A block has at most 33^3 = 35,937 vertices, so indices fit in <see cref="ushort"/>. Not thread safe.
/// </summary>
public sealed class MeshBuffers
{
    private const int MinCapacity = 16;

    public MeshBuffers(int vertexCapacity = 1024, int indexCapacity = 3072)
    {
        Vertices = new MeshVertex[Math.Max(vertexCapacity, MinCapacity)];
        Indices = new ushort[Math.Max(indexCapacity, MinCapacity)];
    }

    /// <summary>The vertex array; the first <see cref="VertexCount"/> entries are in use.</summary>
    public MeshVertex[] Vertices { get; private set; }

    public int VertexCount { get; private set; }

    /// <summary>The index array; the first <see cref="IndexCount"/> entries are in use, three to a triangle.</summary>
    public ushort[] Indices { get; private set; }

    public int IndexCount { get; private set; }

    /// <summary>Materials of the mesh's palette, at most <see cref="PaletteSize"/>.</summary>
    public const int PaletteSize = 8;

    /// <summary>Words of the palette in the mesh heap, before the vertices.</summary>
    public const int PaletteWords = 2;

    /// <summary>
    /// The materials of the vertices' weights (<see cref="MeshVertex.Weights"/>), a byte each from the lowest, slot 0 in the
    /// lowest byte of the first word: the mesh's surface materials, the most common first; 0 past the last.
    /// </summary>
    public ulong Palette { get; set; }

    /// <summary>True when the mesh's surface had more materials than the palette holds (the rest counted as another).</summary>
    public bool PaletteOverflowed { get; set; }

    /// <summary>Empties the mesh; the arrays keep their size.</summary>
    public void Clear()
    {
        VertexCount = 0;
        IndexCount = 0;
        Palette = 0;
        PaletteOverflowed = false;
    }

    /// <summary>Appends a vertex and returns its index.</summary>
    public int AddVertex(in MeshVertex vertex)
    {
        if (VertexCount == Vertices.Length)
        {
            var grown = new MeshVertex[Vertices.Length * 2];
            Array.Copy(Vertices, grown, VertexCount);
            Vertices = grown;
        }

        Vertices[VertexCount] = vertex;
        return VertexCount++;
    }

    /// <summary>Appends one triangle's three indices.</summary>
    public void AddTriangle(int a, int b, int c)
    {
        if (IndexCount + 3 > Indices.Length)
        {
            var grown = new ushort[Indices.Length * 2];
            Array.Copy(Indices, grown, IndexCount);
            Indices = grown;
        }

        Indices[IndexCount] = (ushort)a;
        Indices[IndexCount + 1] = (ushort)b;
        Indices[IndexCount + 2] = (ushort)c;
        IndexCount += 3;
    }
}
