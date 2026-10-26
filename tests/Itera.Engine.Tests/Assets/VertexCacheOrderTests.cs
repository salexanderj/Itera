using System.Numerics;
using System.Text.Json.Nodes;
using Itera.Engine.Assets;
using Itera.Engine.Maths;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Tests.Assets;

/// <summary>
/// <see cref="VertexCacheOrder"/> on grids whose triangles are shuffled: the same triangles with their winding come out,
/// the vertices numbered by first use, far fewer misses of a 32-vertex FIFO cache, the same result on every run; edge
/// cases; and the import moving every attribute of a vertex with it.
/// </summary>
public sealed class VertexCacheOrderTests
{
    [Fact]
    public void The_triangles_are_the_same_with_their_winding()
    {
        var (indices, vertexCount) = ShuffledGrid(24, seed: 1);
        var reordered = indices.ToArray();

        var order = VertexCacheOrder.Reorder(reordered, vertexCount);

        Assert.Equal(Triangles(indices, null), Triangles(reordered, order));
    }

    [Fact]
    public void The_vertices_are_numbered_in_the_order_the_triangles_first_use_them()
    {
        var (indices, vertexCount) = ShuffledGrid(24, seed: 2);

        var order = VertexCacheOrder.Reorder(indices, vertexCount);

        var next = 0u;
        foreach (var index in indices)
        {
            if (index > next)
            {
                Assert.Fail($"index {index} before vertex {next} was used");
            }

            next = Math.Max(next, index + 1);
        }

        Assert.Equal((uint)vertexCount, next);
        Assert.Equal(Enumerable.Range(0, vertexCount), order.Order());
    }

    [Fact]
    public void A_shuffled_grid_misses_the_cache_far_less_often()
    {
        var (indices, vertexCount) = ShuffledGrid(40, seed: 3);
        var before = MissRatio(indices);

        VertexCacheOrder.Reorder(indices, vertexCount);

        var after = MissRatio(indices);
        Assert.True(before > 2.5, $"the shuffled grid misses {before} a triangle");
        Assert.True(after < 0.8, $"misses {before} a triangle before, {after} after");
    }

    [Fact]
    public void Two_runs_give_the_same_order()
    {
        var (first, vertexCount) = ShuffledGrid(30, seed: 4);
        var second = first.ToArray();

        var firstOrder = VertexCacheOrder.Reorder(first, vertexCount);
        var secondOrder = VertexCacheOrder.Reorder(second, vertexCount);

        Assert.Equal(first, second);
        Assert.Equal(firstOrder, secondOrder);
    }

    [Fact]
    public void No_triangles_leave_the_vertices_in_order()
    {
        Assert.Equal([0, 1, 2, 3], VertexCacheOrder.Reorder([], 4));
        Assert.Empty(VertexCacheOrder.Reorder([], 0));
    }

    [Fact]
    public void One_triangle_numbers_its_corners_in_order_and_unused_vertices_last()
    {
        uint[] indices = [3, 0, 4];

        var order = VertexCacheOrder.Reorder(indices, 6);

        Assert.Equal([0u, 1, 2], indices);
        Assert.Equal([3, 0, 4, 1, 2, 5], order);
    }

    [Fact]
    public void Indices_past_sixteen_bits_keep_their_vertices()
    {
        const int vertexCount = 70_000;
        uint[] input = [69_999, 65_536, 5, 65_536, 69_998, 5];
        var indices = input.ToArray();

        var order = VertexCacheOrder.Reorder(indices, vertexCount);

        Assert.Equal(Triangles(input, null), Triangles(indices, order));
        Assert.Equal(vertexCount, order.Distinct().Count());
        // The unused vertices follow the used ones, in their order.
        Assert.Equal(Enumerable.Range(0, vertexCount).Except([5, 65_536, 69_998, 69_999]), order[4..]);
    }

    [Fact]
    public void A_triangle_with_a_repeated_corner_is_kept()
    {
        uint[] input = [0, 1, 2, 2, 2, 3, 1, 3, 2, 0, 0, 0];
        var indices = input.ToArray();

        var order = VertexCacheOrder.Reorder(indices, 4);

        Assert.Equal(Triangles(input, null), Triangles(indices, order));
    }

    [Fact]
    public void An_index_out_of_range_or_a_part_triangle_is_refused()
    {
        Assert.Throws<ArgumentException>(() => VertexCacheOrder.Reorder(new uint[] { 0, 1, 3 }, 3));
        Assert.Throws<ArgumentException>(() => VertexCacheOrder.Reorder(new uint[] { 0, 1 }, 3));
    }

    [Fact]
    public void The_import_moves_every_attribute_with_its_vertex()
    {
        const int size = 12;
        var (indices, gridVertices) = ShuffledGrid(size, seed: 5);
        // One more vertex that no triangle uses.
        var vertexCount = gridVertices + 1;
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var tangents = new Vector4[vertexCount];
        var uv0 = new Vector2[vertexCount];
        var uv1 = new Vector2[vertexCount];
        var colours = new Vector4[vertexCount];
        var joints = new ushort[vertexCount * 4];
        var weights = new Vector4[vertexCount];
        for (var v = 0; v < vertexCount; v++)
        {
            var (x, y) = v < gridVertices ? (v % (size + 1), v / (size + 1)) : (100, 100);
            positions[v] = new Vector3(x, y, 0);
            normals[v] = Expected.Normal(positions[v]);
            tangents[v] = Expected.Tangent(positions[v]);
            uv0[v] = Expected.Uv0(positions[v]);
            uv1[v] = Expected.Uv1(positions[v]);
            colours[v] = Expected.Colour(positions[v]);
            joints[v * 4 + 1] = 1;
            weights[v] = new Vector4(1 - Expected.WeightOfB(positions[v]), Expected.WeightOfB(positions[v]), 0, 0);
        }

        var gltf = new GltfBuilder();
        var primitive = GltfBuilder.Primitive(
            gltf.Accessor(positions),
            gltf.Accessor(normals),
            gltf.Accessor(uv0),
            gltf.Indices(indices),
            tangent: gltf.Accessor(tangents),
            joints: gltf.Joints(joints),
            weights: gltf.Accessor(weights)
        );
        var attributes = primitive["attributes"]!.AsObject();
        attributes["TEXCOORD_1"] = gltf.Accessor(uv1);
        attributes["COLOR_0"] = gltf.Accessor(colours);
        var mesh = gltf.Mesh(primitive);
        // Nodes: 0 the joint a, 1 the joint b under it, 2 the mesh.
        gltf.Node(new JsonObject { ["name"] = "a", ["children"] = new JsonArray(1) });
        gltf.Node(new JsonObject { ["name"] = "b" }, root: false);
        var skin = gltf.Skin(gltf.Accessor(new[] { Matrix4x4.Identity, Matrix4x4.Identity }), 0, 1);
        gltf.Node(new JsonObject { ["mesh"] = mesh, ["skin"] = skin });
        var folder = Directory.CreateTempSubdirectory("itera-vertex-cache-").FullName;
        MeshAsset asset;
        var warnings = new List<string>();
        try
        {
            asset = GltfImport.Load("grid", gltf.WriteGltf(folder, "grid"), 1, TextureReading.None, warnings);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        Assert.Empty(warnings);
        Assert.Equal(vertexCount, asset.Vertices.Length);
        Assert.Equal(new Vector3(100, 100, 0), asset.Vertices[^1].Position);
        var ids = asset
            .Vertices.Select(vertex => (int)vertex.Position.X + (int)vertex.Position.Y * (size + 1))
            .ToArray();
        Assert.Equal(Triangles(indices, null), Triangles(asset.Indices, ids));
        var b = asset.Rig!.FindJoint("b");
        for (var v = 0; v < asset.Vertices.Length; v++)
        {
            var vertex = asset.Vertices[v];
            var skinned = asset.Skin![v];
            var weightOfB = 0f;
            for (var slot = 0; slot < 4; slot++)
            {
                weightOfB += skinned.Joint(slot) == b ? skinned.Weight(slot) : 0;
            }

            var tangent = Octahedral.UnpackTangent(vertex.Tangent, out var handedness);
            if (
                Vector3.Distance(Octahedral.UnpackSnorm16(vertex.Normal), Expected.Normal(vertex.Position)) > 1e-3f
                || Vector3.Distance(tangent, Xyz(Expected.Tangent(vertex.Position))) > 1e-3f
                || handedness != Expected.Tangent(vertex.Position).W
                || vertex.Uv != Expected.Uv0(vertex.Position)
                || vertex.Uv1 != MeshAssetVertex.PackHalves(Expected.Uv1(vertex.Position))
                || vertex.Colour != MeshAssetVertex.PackColour(Expected.Colour(vertex.Position))
                || MathF.Abs(weightOfB - Expected.WeightOfB(vertex.Position)) > 1e-3f
            )
            {
                Assert.Fail($"vertex {v} at {vertex.Position} lost an attribute");
            }
        }
    }

    /// <summary>
    /// A square grid of <paramref name="size"/> by <paramref name="size"/> quads, two triangles each, counter-clockwise
    /// seen from +z, in a shuffled order, each triangle's corners turned round by a random step (its winding kept).
    /// </summary>
    private static (uint[] Indices, int VertexCount) ShuffledGrid(int size, int seed)
    {
        var random = new Random(seed);
        var triangles = new List<(uint, uint, uint)>();
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var corner = (uint)(x + y * (size + 1));
                var right = corner + 1;
                var above = corner + (uint)(size + 1);
                triangles.Add((corner, right, above + 1));
                triangles.Add((corner, above + 1, above));
            }
        }

        var shuffled = triangles.ToArray();
        random.Shuffle(shuffled);
        var indices = new uint[shuffled.Length * 3];
        for (var t = 0; t < shuffled.Length; t++)
        {
            var (a, b, c) = shuffled[t];
            (a, b, c) = random.Next(3) switch
            {
                0 => (a, b, c),
                1 => (b, c, a),
                _ => (c, a, b),
            };
            (indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2]) = (a, b, c);
        }

        return (indices, (size + 1) * (size + 1));
    }

    /// <summary>
    /// The triangles of <paramref name="indices"/> as vertices of <paramref name="vertexOf"/> (each index's own number
    /// when null), each turned round to start at its least vertex (its winding kept), sorted.
    /// </summary>
    private static (int, int, int)[] Triangles(uint[] indices, int[]? vertexOf)
    {
        var triangles = new (int, int, int)[indices.Length / 3];
        for (var t = 0; t < triangles.Length; t++)
        {
            var a = vertexOf is null ? (int)indices[t * 3] : vertexOf[indices[t * 3]];
            var b = vertexOf is null ? (int)indices[t * 3 + 1] : vertexOf[indices[t * 3 + 1]];
            var c = vertexOf is null ? (int)indices[t * 3 + 2] : vertexOf[indices[t * 3 + 2]];
            triangles[t] =
                a <= b && a <= c ? (a, b, c)
                : b <= c ? (b, c, a)
                : (c, a, b);
        }

        Array.Sort(triangles);
        return triangles;
    }

    /// <summary>The average cache misses a triangle of a first-in, first-out cache of 32 vertices (ACMR).</summary>
    private static double MissRatio(uint[] indices)
    {
        var cache = new Queue<uint>();
        var misses = 0;
        foreach (var index in indices)
        {
            if (cache.Contains(index))
            {
                continue;
            }

            misses++;
            cache.Enqueue(index);
            if (cache.Count > VertexCacheOrder.CacheSize)
            {
                cache.Dequeue();
            }
        }

        return (double)misses / (indices.Length / 3);
    }

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);

    /// <summary>Each attribute of the import's grid as a function of the vertex's place, so a vertex moved without one shows.</summary>
    private static class Expected
    {
        public static Vector3 Normal(Vector3 p) => Vector3.Normalize(new Vector3(p.X - 6, p.Y - 6, 12));

        public static Vector4 Tangent(Vector3 p) =>
            new(Vector3.Normalize(new Vector3(1, p.Y / 12, 0)), (int)p.X % 2 == 0 ? 1 : -1);

        public static Vector2 Uv0(Vector3 p) => new(p.X / 12, p.Y / 12);

        public static Vector2 Uv1(Vector3 p) => new(p.Y / 24, p.X / 24);

        public static Vector4 Colour(Vector3 p) => new(Math.Min(p.X / 12, 1), Math.Min(p.Y / 12, 1), 0.25f, 1);

        public static float WeightOfB(Vector3 p) => Math.Min(p.X / 12, 1);
    }
}
