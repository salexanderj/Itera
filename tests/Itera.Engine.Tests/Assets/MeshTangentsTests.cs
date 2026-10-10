using System.Numerics;
using Itera.Engine.Assets;

namespace Itera.Engine.Tests.Assets;

public sealed class MeshTangentsTests
{
    private static readonly uint[] QuadIndices = [0, 1, 2, 0, 2, 3];

    private static readonly Vector3[] QuadPositions =
    [
        new(0f, 0f, 0f),
        new(2f, 0f, 0f),
        new(2f, 1f, 0f),
        new(0f, 1f, 0f),
    ];

    /// <summary>A quad in the XY plane facing +z, with the UVs of its corners given in order.</summary>
    private static Vector4[] GenerateQuad(Vector2[] uvs)
    {
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        var tangents = new Vector4[4];
        MeshTangents.Generate(QuadPositions, normals, uvs, QuadIndices, tangents);
        return tangents;
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True((expected - actual).Length() < 1e-5f, $"expected {expected}, got {actual}");
    }

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);

    [Fact]
    public void U_along_x_and_v_along_y_gives_the_x_tangent_and_plus_one()
    {
        var tangents = GenerateQuad([new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)]);

        foreach (var tangent in tangents)
        {
            AssertNear(Vector3.UnitX, Xyz(tangent));
            Assert.Equal(1f, tangent.W);
        }
    }

    [Fact]
    public void U_along_minus_x_gives_the_minus_x_tangent_and_a_bitangent_along_plus_y()
    {
        var tangents = GenerateQuad([new(1f, 0f), new(0f, 0f), new(0f, 1f), new(1f, 1f)]);

        foreach (var tangent in tangents)
        {
            AssertNear(-Vector3.UnitX, Xyz(tangent));
            Assert.Equal(-1f, tangent.W);
            AssertNear(Vector3.UnitY, Vector3.Cross(Vector3.UnitZ, Xyz(tangent)) * tangent.W);
        }
    }

    [Fact]
    public void V_along_minus_y_flips_the_handedness()
    {
        var tangents = GenerateQuad([new(0f, 1f), new(1f, 1f), new(1f, 0f), new(0f, 0f)]);

        foreach (var tangent in tangents)
        {
            AssertNear(Vector3.UnitX, Xyz(tangent));
            Assert.Equal(-1f, tangent.W);
            AssertNear(-Vector3.UnitY, Vector3.Cross(Vector3.UnitZ, Xyz(tangent)) * tangent.W);
        }
    }

    [Fact]
    public void Equal_UVs_give_a_unit_tangent_perpendicular_to_the_normal()
    {
        var tangents = GenerateQuad([new(0.5f, 0.5f), new(0.5f, 0.5f), new(0.5f, 0.5f), new(0.5f, 0.5f)]);

        foreach (var tangent in tangents)
        {
            Assert.Equal(1f, Xyz(tangent).Length(), 1e-5f);
            Assert.Equal(0f, Vector3.Dot(Vector3.UnitZ, Xyz(tangent)), 1e-5f);
            Assert.Equal(1f, tangent.W);
        }
    }

    [Fact]
    public void A_tilted_normal_gets_a_tangent_perpendicular_to_it()
    {
        // The quad's plane faces +z, but the vertex normals lean towards +x and +y.
        var normal = Vector3.Normalize(new Vector3(0.3f, 0.2f, 1f));
        Vector3[] normals = [normal, normal, normal, normal];
        Vector2[] uvs = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)];
        var tangents = new Vector4[4];

        MeshTangents.Generate(QuadPositions, normals, uvs, QuadIndices, tangents);

        foreach (var tangent in tangents)
        {
            Assert.Equal(1f, Xyz(tangent).Length(), 1e-5f);
            Assert.Equal(0f, Vector3.Dot(normal, Xyz(tangent)), 1e-5f);
            Assert.True(Xyz(tangent).X > 0.9f);
            Assert.Equal(1f, tangent.W);
        }
    }

    [Fact]
    public void A_vertex_no_triangle_uses_gets_a_unit_tangent_perpendicular_to_its_normal()
    {
        Vector3[] positions = [.. QuadPositions, new Vector3(5f, 5f, 5f)];
        var normal = Vector3.Normalize(new Vector3(1f, 2f, 3f));
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, normal];
        Vector2[] uvs = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f), new(0.3f, 0.3f)];
        var tangents = new Vector4[5];

        MeshTangents.Generate(positions, normals, uvs, QuadIndices, tangents);

        Assert.Equal(1f, Xyz(tangents[4]).Length(), 1e-5f);
        Assert.Equal(0f, Vector3.Dot(normal, Xyz(tangents[4])), 1e-5f);
    }

    [Fact]
    public void Larger_triangles_weigh_more_where_they_share_a_vertex()
    {
        // Vertex 0 is shared by a large triangle with u along +x and a small one with u along +y. The large one wins.
        Vector3[] positions = [new(0f, 0f, 0f), new(10f, 0f, 0f), new(0f, 10f, 0f), new(0f, -1f, 0f), new(1f, -1f, 0f)];
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        Vector2[] uvs = [new(0f, 0f), new(10f, 0f), new(0f, 10f), new(0f, -1f), new(0f, -1f)];
        uint[] indices = [0, 1, 2];
        var tangents = new Vector4[5];

        MeshTangents.Generate(positions, normals, uvs, indices, tangents);

        AssertNear(Vector3.UnitX, Xyz(tangents[0]));
    }

    [Fact]
    public void Mismatched_lengths_throw()
    {
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        Vector2[] uvs = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)];

        Assert.Throws<ArgumentException>(() =>
            MeshTangents.Generate(QuadPositions, normals.AsSpan(0, 3), uvs, QuadIndices, new Vector4[4])
        );
        Assert.Throws<ArgumentException>(() =>
            MeshTangents.Generate(QuadPositions, normals, uvs.AsSpan(0, 3), QuadIndices, new Vector4[4])
        );
        Assert.Throws<ArgumentException>(() =>
            MeshTangents.Generate(QuadPositions, normals, uvs, QuadIndices, new Vector4[3])
        );
    }

    [Fact]
    public void An_index_count_that_is_not_a_multiple_of_three_throws()
    {
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        Vector2[] uvs = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)];

        Assert.Throws<ArgumentException>(() =>
            MeshTangents.Generate(QuadPositions, normals, uvs, QuadIndices.AsSpan(0, 4), new Vector4[4])
        );
    }

    [Fact]
    public void An_index_out_of_range_throws()
    {
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        Vector2[] uvs = [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)];
        uint[] indices = [0, 1, 4];

        Assert.Throws<ArgumentException>(() =>
            MeshTangents.Generate(QuadPositions, normals, uvs, indices, new Vector4[4])
        );
    }
}
