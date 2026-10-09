using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Itera.Engine.Meshing;
using Itera.Engine.Rendering.GpuData;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Meshing;

[RunsLong]
public sealed class SurfaceNetsTests(ITestOutputHelper output)
{
    private const int Side = SurfaceNets.SampleSide;

    [Fact]
    public void AllAirAndAllSolidMakeNoTriangles()
    {
        var mesher = new SurfaceNets();
        var buffers = new MeshBuffers();
        buffers.AddVertex(default);
        buffers.AddTriangle(0, 0, 0);

        Assert.Equal(0, mesher.Mesh(new byte[SurfaceNets.SampleCount], buffers));
        Assert.Equal(0, buffers.VertexCount);
        Assert.Equal(0, buffers.IndexCount);

        var solid = new byte[SurfaceNets.SampleCount];
        Array.Fill(solid, (byte)3);
        Assert.Equal(0, mesher.Mesh(solid, buffers));
        Assert.Equal(0, buffers.VertexCount);
    }

    [Fact]
    public void AWrongLengthThrows()
    {
        var mesher = new SurfaceNets();
        Assert.Throws<ArgumentException>(() => mesher.Mesh(new byte[SurfaceNets.SampleCount - 1], new MeshBuffers()));
    }

    [Theory]
    [InlineData(-1, 5, 5)]
    [InlineData(5, -2, 5)]
    [InlineData(5, 5, -1)]
    [InlineData(32, 32, 5)]
    [InlineData(32, 32, 32)]
    public void ASolidVoxelWhoseEdgesTheBlockDoesNotOwnMakesNoTriangles(int x, int y, int z)
    {
        var samples = new byte[SurfaceNets.SampleCount];
        Set(samples, x, y, z, 1);

        Assert.Equal(0, new SurfaceNets().Mesh(samples, new MeshBuffers()));
    }

    [Theory]
    [InlineData(32, 5, 5)]
    [InlineData(5, 32, 5)]
    [InlineData(5, 5, 32)]
    public void ASolidVoxelJustPastTheUpperFaceMakesTheQuadOfTheEdgeIntoIt(int x, int y, int z)
    {
        var samples = new byte[SurfaceNets.SampleCount];
        Set(samples, x, y, z, 1);
        var buffers = new MeshBuffers();

        Assert.Equal(2, new SurfaceNets().Mesh(samples, buffers));
    }

    [Fact]
    public void OneSolidVoxelMakesAClosedMeshFacingOutward()
    {
        var samples = new byte[SurfaceNets.SampleCount];
        Set(samples, 16, 16, 16, 4);
        var buffers = new MeshBuffers();

        var triangles = new SurfaceNets().Mesh(samples, buffers);

        Assert.Equal(12, triangles);
        Assert.Equal(8, buffers.VertexCount);
        AssertClosed(Indices(buffers));
        var centre = new Vector3(16.5f);
        for (var t = 0; t < triangles; t++)
        {
            Triangle(buffers, t, out var a, out var b, out var c);
            var normal = Vector3.Cross(b - a, c - a);
            Assert.True(normal.Length() > 1e-4f, "a triangle has no area");
            Assert.True(Vector3.Dot(normal, (a + b + c) / 3f - centre) > 0f, "a triangle faces the voxel");
        }

        Assert.True(SignedVolume(buffers) > 0f);
        for (var i = 0; i < buffers.VertexCount; i++)
        {
            Assert.Equal(4, MeshVertex.DecodeMaterial(buffers.Vertices[i]));
        }
    }

    [Fact]
    public void ASolidSphereMakesAClosedMeshOnItsSurface()
    {
        const float radius = 10f;
        var centre = new Vector3(16f);
        var samples = Build(
            (x, y, z) =>
                Vector3.DistanceSquared(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f), centre) <= radius * radius
                    ? (byte)1
                    : (byte)0
        );
        var buffers = new MeshBuffers();

        var triangles = new SurfaceNets().Mesh(samples, buffers);

        Assert.True(triangles > 500);
        AssertClosed(Indices(buffers));
        Assert.True(SignedVolume(buffers) > 0f);
        var sphereVolume = 4f / 3f * MathF.PI * radius * radius * radius;
        Assert.InRange(SignedVolume(buffers), sphereVolume * 0.85f, sphereVolume * 1.15f);
        var cos45 = MathF.Cos(MathF.PI / 4f);
        for (var i = 0; i < buffers.VertexCount; i++)
        {
            var position = MeshVertex.DecodePosition(buffers.Vertices[i]);
            var direction = Vector3.Normalize(position - centre);
            Assert.InRange((position - centre).Length(), radius - 1f, radius + 1f);
            Assert.True(
                Vector3.Dot(MeshVertex.DecodeNormal(buffers.Vertices[i]), direction) >= cos45,
                "normal off the radius"
            );
        }

        for (var t = 0; t < triangles; t++)
        {
            Triangle(buffers, t, out var a, out var b, out var c);
            Assert.True(
                Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - centre) >= 0f,
                "triangle faces inward"
            );
        }
    }

    [Fact]
    public void TwoBlocksSideBySideMakeOneClosedMeshWithTheSameVerticesOnTheirBoundary()
    {
        // The field is 68 voxels long in x. Block A's samples are its x 0..35, block B's are 32..67, so B's origin is
        // 32 voxels on from A's. A voxel at field x f is A's voxel f - 2.
        const int length = 68;
        var field = new byte[length * Side * Side];
        var centre = new Vector3(32f, 16f, 16f);
        for (var z = 0; z < Side; z++)
        {
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < length; x++)
                {
                    var voxel = new Vector3(x - 2 + 0.5f, y - 2 + 0.5f, z - 2 + 0.5f);
                    if (Vector3.DistanceSquared(voxel, centre) <= 9f * 9f)
                    {
                        field[x + length * (y + Side * z)] = (byte)(1 + (x + 3 * y + z) % 3);
                    }
                }
            }
        }

        var meshA = MeshWindow(field, length, 0);
        var meshB = MeshWindow(field, length, 32);
        Assert.True(meshA.IndexCount > 0);
        Assert.True(meshB.IndexCount > 0);

        var ids = new Dictionary<(uint, uint, uint), int>();
        var byPosition = new Dictionary<(uint, uint), MeshVertex>();
        var all = new List<int>();
        var shared = 0;
        foreach (var (mesh, shift) in new[] { (meshA, 0u), (meshB, 32u * 256u) })
        {
            var mapped = new int[mesh.VertexCount];
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                var v = mesh.Vertices[i];
                v.PositionXY += shift;
                mapped[i] = ids.TryAdd((v.PositionXY, v.PositionZMaterial, v.Normal), ids.Count)
                    ? ids.Count - 1
                    : ids[(v.PositionXY, v.PositionZMaterial, v.Normal)];
                if (byPosition.TryGetValue((v.PositionXY, v.PositionZMaterial & 0xFFFF), out var other))
                {
                    Assert.Equal(
                        (other.PositionXY, other.PositionZMaterial, other.Normal),
                        (v.PositionXY, v.PositionZMaterial, v.Normal)
                    );
                    shared++;
                }
                else
                {
                    byPosition.Add((v.PositionXY, v.PositionZMaterial & 0xFFFF), v);
                }
            }

            for (var i = 0; i < mesh.IndexCount; i++)
            {
                all.Add(mapped[mesh.Indices[i]]);
            }
        }

        Assert.True(shared > 50, "the blocks share no vertices");
        AssertClosed([.. all]);
    }

    [Fact]
    public void FlatGroundFacesUpWithHalfOpenSpace()
    {
        var samples = Build((_, y, _) => y < 16 ? (byte)1 : (byte)0);
        var buffers = new MeshBuffers();

        var triangles = new SurfaceNets().Mesh(samples, buffers);

        Assert.Equal(2 * 32 * 32, triangles);
        Assert.Equal(33 * 33, buffers.VertexCount);
        for (var t = 0; t < triangles; t++)
        {
            Triangle(buffers, t, out var a, out var b, out var c);
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            Assert.True(Vector3.Dot(normal, Vector3.UnitY) > 0.999f);
        }

        for (var i = 0; i < buffers.VertexCount; i++)
        {
            var vertex = buffers.Vertices[i];
            Assert.True(Vector3.Distance(MeshVertex.DecodeNormal(vertex), Vector3.UnitY) < 1e-3f);
            Assert.Equal(128, MeshVertex.DecodeOpenness(vertex));
            Assert.Equal(16f, MeshVertex.DecodePosition(vertex).Y);
        }
    }

    [Theory]
    [InlineData(255, 77)]
    [InlineData(200, 0)]
    [InlineData(128, 127)]
    public void FlatGroundLiesWhereTheDensityCrossesOneHalf(int top, int above)
    {
        // Solid below y = 15, the top solid layer at y = 15 and the air layer over it at y = 16 of these densities.
        var samples = Build((_, y, _) => y < 16 ? (byte)1 : (byte)0);
        var densities = Build(
            (_, y, _) =>
                y switch
                {
                    < 15 => 255,
                    15 => (byte)top,
                    16 => (byte)above,
                    _ => 0,
                }
        );
        var buffers = new MeshBuffers();

        new SurfaceNets().Mesh(samples, densities, buffers);

        // Between the voxels' centres at 15.5 and 16.5.
        var expected = 15.5f + (top - 127.5f) / (top - above);
        for (var i = 0; i < buffers.VertexCount; i++)
        {
            var vertex = buffers.Vertices[i];
            Assert.Equal(expected, MeshVertex.DecodePosition(vertex).Y, 0.01f);
            Assert.True(Vector3.Distance(MeshVertex.DecodeNormal(vertex), Vector3.UnitY) < 1e-3f);
        }
    }

    [Fact]
    public void AWallAtAnAngleToTheGridIsAsOpenAlongItsLength()
    {
        // A wall through the block, its face turned 30 degrees from the grid; each voxel's density is the share of
        // its 10x10x10 points behind the face.
        var (sin, cos) = MathF.SinCos(MathF.PI / 6);
        var normal = new Vector3(cos, 0, sin);
        var share = new byte[SurfaceNets.SampleCount];
        var samples = Build(
            (x, y, z) =>
            {
                var solid = 0;
                for (var k = 0; k < 10; k++)
                {
                    for (var j = 0; j < 10; j++)
                    {
                        for (var i = 0; i < 10; i++)
                        {
                            var point = new Vector3(x + (i + 0.5f) / 10, y + (j + 0.5f) / 10, z + (k + 0.5f) / 10);
                            solid += Vector3.Dot(point - new Vector3(16.3f, 0, 16), normal) < 0 ? 1 : 0;
                        }
                    }
                }

                var density = (byte)((solid * 255 + 500) / 1000);
                share[SurfaceNets.SampleIndex(x + 2, y + 2, z + 2)] = density;
                return density >= 128 ? (byte)1 : (byte)0;
            }
        );
        var buffers = new MeshBuffers();

        new SurfaceNets().Mesh(samples, share, buffers);

        // Away from the block's edges, every vertex of the face sees the same half of the space round it, give or take
        // a few steps (124 to 131 when written); counted round each vertex's cell, as the mesher did before densities,
        // it ran from 79 to 175 along this wall, the stripes on the stone walls.
        int least = 255,
            most = 0,
            counted = 0;
        for (var i = 0; i < buffers.VertexCount; i++)
        {
            var vertex = buffers.Vertices[i];
            var position = MeshVertex.DecodePosition(vertex);
            if (position.Y < 4 || position.Y > 28 || position.Z < 4 || position.Z > 28)
            {
                continue;
            }

            Assert.InRange(Vector3.Dot(position - new Vector3(16.3f, 0, 16), normal), -0.05f, 0.05f);
            var openness = MeshVertex.DecodeOpenness(vertex);
            least = Math.Min(least, openness);
            most = Math.Max(most, openness);
            counted++;
        }

        Assert.True(counted > 100, $"{counted} vertices on the face");
        Assert.True(most - least <= 8, $"openness from {least} to {most}");
    }

    [Fact]
    public void WrongLengthDensitiesThrow()
    {
        var mesher = new SurfaceNets();
        Assert.Throws<ArgumentException>(() =>
            mesher.Mesh(new byte[SurfaceNets.SampleCount], new byte[10], new MeshBuffers())
        );
    }

    [Fact]
    public void ATopLayerGivesTheTopVerticesItsMaterial()
    {
        var samples = Build(
            (_, y, _) =>
                y < 15 ? (byte)2
                : y == 15 ? (byte)5
                : (byte)0
        );
        var buffers = new MeshBuffers();

        new SurfaceNets().Mesh(samples, buffers);

        Assert.Equal(33 * 33, buffers.VertexCount);
        for (var i = 0; i < buffers.VertexCount; i++)
        {
            Assert.Equal(5, MeshVertex.DecodeMaterial(buffers.Vertices[i]));
        }
    }

    /// <summary>
    /// The palette holds the surface's materials, the most common first, and each vertex how many of its cell's surface
    /// corners are each: flat ground of one material gives every vertex its four top corners in slot 0; ground whose left
    /// part is another material puts both in the palette, the border's vertices counting two of each.
    /// </summary>
    [Fact]
    public void TheWeightsCountEachVertexsSurfaceCornersByTheMeshsPalette()
    {
        var buffers = new MeshBuffers();
        var mesher = new SurfaceNets();
        mesher.Mesh(Build((_, y, _) => y < 15 ? (byte)4 : (byte)0), buffers);
        Assert.Equal(4ul, buffers.Palette);
        Assert.False(buffers.PaletteOverflowed);
        for (var i = 0; i < buffers.VertexCount; i++)
        {
            Assert.Equal(4u, buffers.Vertices[i].Weights);
        }

        // Material 7 on x < 10 (fewer of the surface's corners), 4 from x = 10: 4 first in the palette.
        mesher.Mesh(
            Build(
                (x, y, _) =>
                    y >= 15 ? (byte)0
                    : x < 10 ? (byte)7
                    : (byte)4
            ),
            buffers
        );
        Assert.Equal(4ul | 7ul << 8, buffers.Palette);
        var border = 0;
        for (var i = 0; i < buffers.VertexCount; i++)
        {
            var vertex = buffers.Vertices[i];
            var x = MeshVertex.DecodePosition(in vertex).X;
            var four = MeshVertex.DecodeWeight(in vertex, 0);
            var seven = MeshVertex.DecodeWeight(in vertex, 1);
            Assert.Equal(4, four + seven);
            // The vertex of cell 9 lies at x = 10, its corners 9 and 10: two of each.
            if (MathF.Abs(x - 10f) < 0.01f)
            {
                Assert.Equal(2, four);
                border++;
            }
            else
            {
                Assert.Equal(x < 10f ? 0 : 4, four);
            }
        }

        Assert.Equal(33, border);
    }

    /// <summary>
    /// A surface of more materials than the palette holds keeps the eight most common; the rest count as each vertex's
    /// own material, so every vertex's corners are still all counted.
    /// </summary>
    [Fact]
    public void MaterialsPastThePaletteCountAsTheVertexsOwn()
    {
        var buffers = new MeshBuffers();
        // Ten bands along x of materials 1 to 10, the first eight wider.
        new SurfaceNets().Mesh(
            Build((x, y, _) => y >= 15 ? (byte)0 : (byte)(1 + Math.Clamp((x + 2) / 4, 0, 9))),
            buffers
        );

        Assert.True(buffers.PaletteOverflowed);
        for (var slot = 0; slot < MeshBuffers.PaletteSize; slot++)
        {
            Assert.NotEqual(0ul, (buffers.Palette >> (8 * slot)) & 0xFF);
        }

        for (var i = 0; i < buffers.VertexCount; i++)
        {
            var vertex = buffers.Vertices[i];
            var total = 0;
            for (var slot = 0; slot < MeshBuffers.PaletteSize; slot++)
            {
                total += MeshVertex.DecodeWeight(in vertex, slot);
            }

            Assert.Equal(4, total);
        }
    }

    [Fact]
    public void AMajorityOfCornersSetsTheMaterial()
    {
        // Three corners of material 2 and one upper corner of material 9: 2 wins although 9 comes first in the order.
        var material = MaterialOfCell((0, 1, 0, 9), (1, 1, 1, 2), (0, 0, 0, 2), (1, 0, 1, 2));
        Assert.Equal(2, material);
    }

    /// <summary>
    /// Seven solid corners: the three beside the air corner are a skin of material 5 over material 9, which the other
    /// four have. The skin is what the surface shows.
    /// </summary>
    [Fact]
    public void AMaterialUnderASkinDoesNotOutvoteIt()
    {
        var samples = new byte[SurfaceNets.SampleCount];
        foreach (var (x, y, z, m) in new[] { (0, 1, 1, 5), (1, 0, 1, 5), (1, 1, 0, 5) })
        {
            Set(samples, 10 + x, 10 + y, 10 + z, (byte)m);
        }

        foreach (var (x, y, z) in new[] { (0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1) })
        {
            Set(samples, 10 + x, 10 + y, 10 + z, 9);
        }

        var buffers = new MeshBuffers();
        new SurfaceNets().Mesh(samples, buffers);

        // The cell's vertex: the one nearest where the reference puts it.
        var position = Reference.Position(samples, null, (10, 10, 10));
        var nearest = 0;
        for (var i = 1; i < buffers.VertexCount; i++)
        {
            if (
                Vector3.Distance(MeshVertex.DecodePosition(buffers.Vertices[i]), position)
                < Vector3.Distance(MeshVertex.DecodePosition(buffers.Vertices[nearest]), position)
            )
            {
                nearest = i;
            }
        }

        Assert.True(Vector3.Distance(MeshVertex.DecodePosition(buffers.Vertices[nearest]), position) < 0.5f);
        Assert.Equal(5, MeshVertex.DecodeMaterial(buffers.Vertices[nearest]));
    }

    [Fact]
    public void ATieGoesToTheUpperCornersFirst()
    {
        Assert.Equal(8, MaterialOfCell((0, 0, 0, 7), (1, 1, 1, 8)));
        Assert.Equal(7, MaterialOfCell((0, 0, 0, 8), (1, 1, 1, 7)));
    }

    [Fact]
    public void ATieOfCornersOnTheSameLevelGoesToTheLowerZBeforeTheLowerX()
    {
        // Corners (1, 1, 0) and (0, 1, 1): z ascending comes before x ascending, so (1, 1, 0) wins.
        Assert.Equal(5, MaterialOfCell((1, 1, 0, 5), (0, 1, 1, 6)));
        Assert.Equal(6, MaterialOfCell((1, 1, 0, 6), (0, 1, 1, 5)));
        // And x ascending breaks a tie on the same row.
        Assert.Equal(4, MaterialOfCell((0, 0, 1, 4), (1, 0, 1, 3)));
        Assert.Equal(3, MaterialOfCell((0, 0, 1, 3), (1, 0, 1, 4)));
    }

    [Fact]
    public void ATwoCountTieUsesTheCornerOrder()
    {
        Assert.Equal(3, MaterialOfCell((0, 1, 0, 3), (1, 1, 1, 4), (0, 0, 0, 4), (1, 0, 1, 3)));
        Assert.Equal(4, MaterialOfCell((0, 1, 0, 4), (1, 1, 1, 3), (0, 0, 0, 3), (1, 0, 1, 4)));
    }

    [Fact]
    public void AZeroGradientFallsBackToTheCrossingDirections()
    {
        // Solid voxels at opposite corners of one cell: the occupancy is symmetric about its centre, the gradient is
        // zero, and the directions across the cell's edges cancel too, which leaves (0, 1, 0).
        var samples = new byte[SurfaceNets.SampleCount];
        Set(samples, 10, 10, 10, 1);
        Set(samples, 11, 11, 11, 1);
        var buffers = new MeshBuffers();

        new SurfaceNets().Mesh(samples, buffers);

        var vertex = buffers.Vertices[FindVertex(buffers, new Vector3(11f, 11f, 11f))];
        Assert.True(Vector3.Distance(MeshVertex.DecodeNormal(vertex), Vector3.UnitY) < 1e-3f);
    }

    [Fact]
    public void EveryVertexMatchesAnIndependentReferenceOnRandomNoise() => MatchesTheReference(withDensities: false);

    [Fact]
    public void EveryVertexMatchesAnIndependentReferenceOnRandomDensities() => MatchesTheReference(withDensities: true);

    private static void MatchesTheReference(bool withDensities)
    {
        var random = new Random(1234);
        var samples = new byte[SurfaceNets.SampleCount];
        var densities = withDensities ? new byte[SurfaceNets.SampleCount] : null;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = random.NextDouble() < 0.3 ? (byte)(1 + random.Next(4)) : (byte)0;
            if (densities is not null)
            {
                // Kept a few steps from one half, so no vertex comes within a hair of a voxel's centre, where the
                // vertices of the eight cells round it would meet and the nearest vertex might be another cell's.
                densities[i] = (byte)(samples[i] != 0 ? random.Next(136, 256) : random.Next(0, 120));
            }
        }

        var buffers = new MeshBuffers();
        var triangles = new SurfaceNets().Mesh(samples, densities, buffers);

        var cells = Reference.UsedCells(samples, out var crossings);
        Assert.Equal(2 * crossings, triangles);
        Assert.Equal(cells.Count, buffers.VertexCount);
        var expected = new MeshVertex[cells.Count];
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = Reference.Vertex(samples, densities, cells[i]);
        }

        // Positions in doubles and in floats may round to neighbouring steps of the packed position: matched by the
        // nearest vertex instead of by sorting.
        var actual = buffers.Vertices.AsSpan(0, buffers.VertexCount).ToArray();
        foreach (var vertex in expected)
        {
            var position = MeshVertex.DecodePosition(vertex);
            var nearest = actual.MinBy(other => Vector3.DistanceSquared(MeshVertex.DecodePosition(other), position));
            Assert.True(
                Vector3.Distance(MeshVertex.DecodePosition(nearest), position) < 1e-3f,
                $"vertex at {position}"
            );
            Assert.Equal(MeshVertex.DecodeMaterial(vertex), MeshVertex.DecodeMaterial(nearest));
            Assert.InRange(MeshVertex.DecodeOpenness(nearest) - MeshVertex.DecodeOpenness(vertex), -1, 1);
            Assert.True(
                Vector3.Dot(MeshVertex.DecodeNormal(vertex), MeshVertex.DecodeNormal(nearest)) > 0.9999f,
                $"normal of the vertex at {position}"
            );
        }

        for (var i = 0; i < buffers.IndexCount; i++)
        {
            Assert.True(buffers.Indices[i] < buffers.VertexCount);
        }
    }

    [Fact]
    public void TheSameInputMakesTheSameBytes()
    {
        var random = new Random(77);
        var samples = new byte[SurfaceNets.SampleCount];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = random.NextDouble() < 0.4 ? (byte)(1 + random.Next(6)) : (byte)0;
        }

        var shared = new SurfaceNets();
        var first = new MeshBuffers();
        var second = new MeshBuffers(16, 16);
        var third = new MeshBuffers();
        shared.Mesh(BlobsAndGround(), first);
        shared.Mesh(samples, first);
        shared.Mesh(samples, second);
        new SurfaceNets().Mesh(samples, third);

        foreach (var other in new[] { second, third })
        {
            Assert.Equal(first.VertexCount, other.VertexCount);
            Assert.Equal(first.IndexCount, other.IndexCount);
            Assert.True(
                MemoryMarshal
                    .AsBytes(first.Vertices.AsSpan(0, first.VertexCount))
                    .SequenceEqual(MemoryMarshal.AsBytes(other.Vertices.AsSpan(0, other.VertexCount)))
            );
            Assert.True(
                first.Indices.AsSpan(0, first.IndexCount).SequenceEqual(other.Indices.AsSpan(0, other.IndexCount))
            );
        }
    }

    [Fact]
    public void MeshingAgainAllocatesNothing()
    {
        var samples = BlobsAndGround();
        var mesher = new SurfaceNets();
        var buffers = new MeshBuffers();
        Assert.True(mesher.Mesh(samples, buffers) > 1000);
        mesher.Mesh(samples, buffers);

        var before = SystemAllocation.StartMeasurement();
        var triangles = 0;
        for (var i = 0; i < 20; i++)
        {
            triangles += mesher.Mesh(samples, buffers);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(triangles > 20000);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ABlockCrossedByGentleGroundTakesThisLongPerCall()
    {
        var samples = Build((x, y, z) => y < 14 + 0.1f * x + 0.05f * z ? (byte)(1 + (y < 10 ? 1 : 0)) : (byte)0);
        var mesher = new SurfaceNets();
        var buffers = new MeshBuffers();
        var triangles = mesher.Mesh(samples, buffers);
        // Run for half a second first, so the JIT has promoted the mesher to its optimised code.
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 500)
        {
            mesher.Mesh(samples, buffers);
        }

        const int calls = 300;
        watch.Restart();
        for (var i = 0; i < calls; i++)
        {
            mesher.Mesh(samples, buffers);
        }

        watch.Stop();
        output.WriteLine(
            $"Gentle slope: {triangles} triangles, {buffers.VertexCount} vertices, {watch.Elapsed.TotalMilliseconds / calls:F3} ms per call"
        );

        var air = new byte[SurfaceNets.SampleCount];
        watch.Restart();
        for (var i = 0; i < calls; i++)
        {
            mesher.Mesh(air, buffers);
        }

        watch.Stop();
        output.WriteLine($"All air: {watch.Elapsed.TotalMilliseconds / calls:F4} ms per call");
        Assert.True(triangles > 0);
    }

    private static byte[] BlobsAndGround()
    {
        var blobs = new (Vector3 Centre, float Radius)[]
        {
            (new Vector3(10f, 14f, 12f), 6f),
            (new Vector3(22f, 18f, 20f), 8f),
        };
        return Build(
            (x, y, z) =>
            {
                var point = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
                foreach (var (c, r) in blobs)
                {
                    if (Vector3.DistanceSquared(point, c) <= r * r)
                    {
                        return 3;
                    }
                }

                return y < 8 + 0.2f * x ? (byte)1 : (byte)0;
            }
        );
    }

    /// <summary>Samples from a function of block-local voxel coordinates (-2 to 33 on each axis).</summary>
    private static byte[] Build(Func<int, int, int, byte> material)
    {
        var samples = new byte[SurfaceNets.SampleCount];
        for (var z = 0; z < Side; z++)
        {
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < Side; x++)
                {
                    samples[SurfaceNets.SampleIndex(x, y, z)] = material(x - 2, y - 2, z - 2);
                }
            }
        }

        return samples;
    }

    private static void Set(byte[] samples, int x, int y, int z, byte material) =>
        samples[SurfaceNets.SampleIndex(x + 2, y + 2, z + 2)] = material;

    /// <summary>Meshes the 36-voxel window of a field that is <paramref name="length"/> voxels long in x, from x = <paramref name="start"/>.</summary>
    private static MeshBuffers MeshWindow(byte[] field, int length, int start)
    {
        var samples = new byte[SurfaceNets.SampleCount];
        for (var z = 0; z < Side; z++)
        {
            for (var y = 0; y < Side; y++)
            {
                for (var x = 0; x < Side; x++)
                {
                    samples[SurfaceNets.SampleIndex(x, y, z)] = field[start + x + length * (y + Side * z)];
                }
            }
        }

        var buffers = new MeshBuffers();
        new SurfaceNets().Mesh(samples, buffers);
        return buffers;
    }

    /// <summary>
    /// Meshes air with some solid corners of the cell whose lowest voxel is (10, 10, 10), each given as
    /// (x, y, z, material) with x, y, z in {0, 1}, and returns that cell's vertex's material.
    /// </summary>
    private static int MaterialOfCell(params (int X, int Y, int Z, int Material)[] corners)
    {
        var samples = new byte[SurfaceNets.SampleCount];
        foreach (var (x, y, z, material) in corners)
        {
            Set(samples, 10 + x, 10 + y, 10 + z, (byte)material);
        }

        var buffers = new MeshBuffers();
        new SurfaceNets().Mesh(samples, buffers);
        var position = Reference.Position(samples, null, (10, 10, 10));
        return MeshVertex.DecodeMaterial(buffers.Vertices[FindVertex(buffers, position)]);
    }

    private static int FindVertex(MeshBuffers buffers, Vector3 position)
    {
        var found = -1;
        for (var i = 0; i < buffers.VertexCount; i++)
        {
            if (Vector3.Distance(MeshVertex.DecodePosition(buffers.Vertices[i]), position) < 1f / 512f)
            {
                Assert.Equal(-1, found);
                found = i;
            }
        }

        Assert.NotEqual(-1, found);
        return found;
    }

    private static int CompareVertices(MeshVertex a, MeshVertex b)
    {
        var byXY = a.PositionXY.CompareTo(b.PositionXY);
        if (byXY != 0)
        {
            return byXY;
        }

        var byZ = a.PositionZMaterial.CompareTo(b.PositionZMaterial);
        return byZ != 0 ? byZ : a.Normal.CompareTo(b.Normal);
    }

    private static int[] Indices(MeshBuffers buffers)
    {
        var indices = new int[buffers.IndexCount];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = buffers.Indices[i];
        }

        return indices;
    }

    private static void Triangle(MeshBuffers buffers, int t, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        a = MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[3 * t]]);
        b = MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[3 * t + 1]]);
        c = MeshVertex.DecodePosition(buffers.Vertices[buffers.Indices[3 * t + 2]]);
    }

    private static float SignedVolume(MeshBuffers buffers)
    {
        var volume = 0f;
        for (var t = 0; t < buffers.IndexCount / 3; t++)
        {
            Triangle(buffers, t, out var a, out var b, out var c);
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
        }

        return volume;
    }

    /// <summary>Every directed edge is used once and its reverse once: closed, and wound the same way throughout.</summary>
    private static void AssertClosed(int[] indices)
    {
        var edges = new Dictionary<(int, int), int>();
        for (var t = 0; t < indices.Length; t += 3)
        {
            for (var e = 0; e < 3; e++)
            {
                var from = indices[t + e];
                var to = indices[t + (e + 1) % 3];
                Assert.NotEqual(from, to);
                edges[(from, to)] = edges.GetValueOrDefault((from, to)) + 1;
            }
        }

        foreach (var ((from, to), count) in edges)
        {
            Assert.Equal(1, count);
            Assert.True(edges.ContainsKey((to, from)), "an edge is used by one triangle");
        }
    }

    /// <summary>A plain, slow version of the mesher's vertex rules, written from the spec rather than from the mesher.</summary>
    private static class Reference
    {
        private static readonly int[] Slope = [-1, -1, 1, 1];
        private static readonly int[] Smooth = [1, 3, 3, 1];

        public static bool Solid(byte[] samples, int x, int y, int z) =>
            samples[SurfaceNets.SampleIndex(x + 2, y + 2, z + 2)] != 0;

        /// <summary>The cells some owned crossing edge's quad uses, and the number of crossing edges.</summary>
        public static List<(int X, int Y, int Z)> UsedCells(byte[] samples, out int crossings)
        {
            var cells = new HashSet<(int, int, int)>();
            var list = new List<(int X, int Y, int Z)>();
            crossings = 0;
            for (var axis = 0; axis < 3; axis++)
            {
                for (var z = 0; z < 32; z++)
                {
                    for (var y = 0; y < 32; y++)
                    {
                        for (var x = 0; x < 32; x++)
                        {
                            var (nx, ny, nz) = (
                                x + (axis == 0 ? 1 : 0),
                                y + (axis == 1 ? 1 : 0),
                                z + (axis == 2 ? 1 : 0)
                            );
                            if (Solid(samples, x, y, z) == Solid(samples, nx, ny, nz))
                            {
                                continue;
                            }

                            crossings++;
                            for (var j = 0; j < 2; j++)
                            {
                                for (var k = 0; k < 2; k++)
                                {
                                    var cell = axis switch
                                    {
                                        0 => (x, y - j, z - k),
                                        1 => (x - k, y, z - j),
                                        _ => (x - j, y - k, z),
                                    };
                                    if (cells.Add(cell))
                                    {
                                        list.Add(cell);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return list;
        }

        /// <summary>The density of sample (x, y, z) in block voxels: given, or the material's default.</summary>
        public static double Density(byte[] samples, byte[]? densities, int x, int y, int z)
        {
            var index = SurfaceNets.SampleIndex(x + 2, y + 2, z + 2);
            return densities?[index] ?? (samples[index] != 0 ? 255 : 0);
        }

        public static Vector3 Position(byte[] samples, byte[]? densities, (int X, int Y, int Z) cell)
        {
            double sx = 0,
                sy = 0,
                sz = 0;
            var n = 0;
            foreach (var (low, high) in Edges(cell))
            {
                if (Solid(samples, low.X, low.Y, low.Z) != Solid(samples, high.X, high.Y, high.Z))
                {
                    // Where the density crosses one half between the two voxels' centres.
                    var a = Density(samples, densities, low.X, low.Y, low.Z);
                    var b = Density(samples, densities, high.X, high.Y, high.Z);
                    var t = (a - 127.5) / (a - b);
                    sx += low.X + (high.X - low.X) * t + 0.5;
                    sy += low.Y + (high.Y - low.Y) * t + 0.5;
                    sz += low.Z + (high.Z - low.Z) * t + 0.5;
                    n++;
                }
            }

            return new Vector3((float)(sx / n), (float)(sy / n), (float)(sz / n));
        }

        /// <summary>
        /// The openness at a point in block voxels: 255 less the mean density of the 3x3x3 voxels round each of the 8
        /// voxels of the cell that holds it, blended between them by the point's place in the cell.
        /// </summary>
        public static int Openness(byte[] samples, byte[]? densities, (int X, int Y, int Z) cell, Vector3 point)
        {
            double fx = point.X - (cell.X + 0.5),
                fy = point.Y - (cell.Y + 0.5),
                fz = point.Z - (cell.Z + 0.5);
            double solid = 0;
            for (var corner = 0; corner < 8; corner++)
            {
                int cx = cell.X + (corner & 1),
                    cy = cell.Y + ((corner >> 1) & 1),
                    cz = cell.Z + (corner >> 2);
                double mean = 0;
                for (var k = -1; k <= 1; k++)
                {
                    for (var j = -1; j <= 1; j++)
                    {
                        for (var i = -1; i <= 1; i++)
                        {
                            mean += Density(samples, densities, cx + i, cy + j, cz + k) / 27;
                        }
                    }
                }

                var weight =
                    ((corner & 1) != 0 ? fx : 1 - fx)
                    * (((corner >> 1) & 1) != 0 ? fy : 1 - fy)
                    * ((corner >> 2) != 0 ? fz : 1 - fz);
                solid += weight * mean;
            }

            return (int)Math.Round(255 - solid);
        }

        public static MeshVertex Vertex(byte[] samples, (int X, int Y, int Z) cell) => Vertex(samples, null, cell);

        public static MeshVertex Vertex(byte[] samples, byte[]? densities, (int X, int Y, int Z) cell)
        {
            double gx = 0,
                gy = 0,
                gz = 0;
            for (var k = 0; k < 4; k++)
            {
                for (var j = 0; j < 4; j++)
                {
                    for (var i = 0; i < 4; i++)
                    {
                        var density = Density(samples, densities, cell.X - 1 + i, cell.Y - 1 + j, cell.Z - 1 + k);
                        gx += density * Slope[i] * Smooth[j] * Smooth[k];
                        gy += density * Smooth[i] * Slope[j] * Smooth[k];
                        gz += density * Smooth[i] * Smooth[j] * Slope[k];
                    }
                }
            }

            var normal = new Vector3((float)-gx, (float)-gy, (float)-gz);
            if (normal.Length() < 1e-6f)
            {
                var sum = Vector3.Zero;
                foreach (var (low, high) in Edges(cell))
                {
                    var lowSolid = Solid(samples, low.X, low.Y, low.Z);
                    if (lowSolid != Solid(samples, high.X, high.Y, high.Z))
                    {
                        var direction = new Vector3(high.X - low.X, high.Y - low.Y, high.Z - low.Z);
                        sum += lowSolid ? direction : -direction;
                    }
                }

                normal = sum.Length() < 1e-6f ? Vector3.UnitY : sum;
            }

            var position = Position(samples, densities, cell);
            return MeshVertex.Pack(
                position,
                Vector3.Normalize(normal),
                Material(samples, cell),
                (byte)Math.Clamp(Openness(samples, densities, cell, position), 0, 255)
            );
        }

        /// <summary>The most frequent material of the cell's solid corners that have an air corner beside them.</summary>
        private static byte Material(byte[] samples, (int X, int Y, int Z) cell)
        {
            byte At(int dx, int dy, int dz) =>
                samples[SurfaceNets.SampleIndex(cell.X + dx + 2, cell.Y + dy + 2, cell.Z + dz + 2)];

            var counts = new int[256];
            var order = new List<byte>();
            foreach (var dy in new[] { 1, 0 })
            {
                foreach (var dz in new[] { 0, 1 })
                {
                    foreach (var dx in new[] { 0, 1 })
                    {
                        var m = At(dx, dy, dz);
                        var besideAir = At(1 - dx, dy, dz) == 0 || At(dx, 1 - dy, dz) == 0 || At(dx, dy, 1 - dz) == 0;
                        if (m != 0 && besideAir)
                        {
                            counts[m]++;
                            order.Add(m);
                        }
                    }
                }
            }

            var best = order[0];
            foreach (var m in order)
            {
                if (counts[m] > counts[best])
                {
                    best = m;
                }
            }

            return best;
        }

        private static IEnumerable<((int X, int Y, int Z) Low, (int X, int Y, int Z) High)> Edges(
            (int X, int Y, int Z) cell
        )
        {
            for (var a = 0; a < 2; a++)
            {
                for (var b = 0; b < 2; b++)
                {
                    yield return ((cell.X, cell.Y + a, cell.Z + b), (cell.X + 1, cell.Y + a, cell.Z + b));
                    yield return ((cell.X + a, cell.Y, cell.Z + b), (cell.X + a, cell.Y + 1, cell.Z + b));
                    yield return ((cell.X + a, cell.Y + b, cell.Z), (cell.X + a, cell.Y + b, cell.Z + 1));
                }
            }
        }
    }
}
