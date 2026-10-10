using System.Numerics;
using Itera.Engine.Physics;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Triangles for collider tests, in metres, built from quads and boxes, each face counter-clockwise seen from its front
/// (as glTF's), added to <see cref="MeshColliders"/> as one collider.
/// </summary>
internal sealed class TestTriangles
{
    private readonly List<Vector3> _positions = [];
    private readonly List<uint> _indices = [];

    public int Count => _indices.Count / 3;

    /// <summary>A quad of corners in order round it, its front where <paramref name="front"/> points.</summary>
    public TestTriangles Quad(Vector3 front, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), front) < 0)
        {
            (p0, p1, p2, p3) = (p3, p2, p1, p0);
        }

        var first = (uint)_positions.Count;
        _positions.AddRange([p0, p1, p2, p3]);
        _indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        return this;
    }

    /// <summary>A triangle, its front where <paramref name="front"/> points.</summary>
    public TestTriangles Triangle(Vector3 front, Vector3 a, Vector3 b, Vector3 c)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), front) < 0)
        {
            (b, c) = (c, b);
        }

        var first = (uint)_positions.Count;
        _positions.AddRange([a, b, c]);
        _indices.AddRange([first, first + 1, first + 2]);
        return this;
    }

    /// <summary>Flat ground at height <paramref name="y"/> from (x0, z0) to (x1, z1), its front up.</summary>
    public TestTriangles Ground(float x0, float z0, float x1, float z1, float y) =>
        Quad(Vector3.UnitY, new(x0, y, z0), new(x0, y, z1), new(x1, y, z1), new(x1, y, z0));

    /// <summary>A closed box from <paramref name="min"/> to <paramref name="max"/>, each face's front outwards.</summary>
    public TestTriangles Box(Vector3 min, Vector3 max)
    {
        Vector3 C(int x, int y, int z) => new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);

        Quad(-Vector3.UnitX, C(0, 0, 0), C(0, 1, 0), C(0, 1, 1), C(0, 0, 1));
        Quad(Vector3.UnitX, C(1, 0, 0), C(1, 1, 0), C(1, 1, 1), C(1, 0, 1));
        Quad(-Vector3.UnitY, C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1));
        Quad(Vector3.UnitY, C(0, 1, 0), C(1, 1, 0), C(1, 1, 1), C(0, 1, 1));
        Quad(-Vector3.UnitZ, C(0, 0, 0), C(1, 0, 0), C(1, 1, 0), C(0, 1, 0));
        return Quad(Vector3.UnitZ, C(0, 0, 1), C(1, 0, 1), C(1, 1, 1), C(0, 1, 1));
    }

    /// <summary>
    /// A closed box of whole cells of <paramref name="cellCentimetres"/>, from cell (x0, y0, z0) to (x1, y1, z1)
    /// inclusive: the triangles over the voxels of a box of that grid.
    /// </summary>
    public TestTriangles CellBox(int x0, int y0, int z0, int x1, int y1, int z1, int cellCentimetres = 1)
    {
        var metres = cellCentimetres / 100f;
        return Box(new Vector3(x0, y0, z0) * metres, new Vector3(x1 + 1, y1 + 1, z1 + 1) * metres);
    }

    /// <summary>Adds the triangles as one collider with <paramref name="tags"/>; its handle.</summary>
    public int AddTo(MeshColliders colliders, uint tags = 0) =>
        colliders.Add([.. _positions], [.. _indices], Matrix4x4.Identity, tags);
}
