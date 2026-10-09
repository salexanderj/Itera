using System.Numerics;
using System.Runtime.CompilerServices;
using Itera.Engine.Rendering.GpuData;

namespace Itera.Engine.Tests.Meshing;

public sealed class MeshVertexTests
{
    [Fact]
    public void AVertexIsSixteenBytes() => Assert.Equal(16, Unsafe.SizeOf<MeshVertex>());

    [Fact]
    public void FieldsHoldTheDocumentedBits()
    {
        var vertex = MeshVertex.Pack(new Vector3(1f, 2f, 3f), Vector3.UnitX, 9, 200);

        Assert.Equal(768u | (1024u << 16), vertex.PositionXY);
        Assert.Equal(1280u | (9u << 16) | (200u << 24), vertex.PositionZMaterial);
        Assert.Equal(32767u, vertex.Normal);
        Assert.Equal(0u, MeshVertex.Pack(Vector3.Zero, Vector3.UnitZ, 0, 0).Normal);
        Assert.Equal(0x8001u, MeshVertex.Pack(Vector3.Zero, -Vector3.UnitX, 0, 0).Normal);
    }

    [Fact]
    public void PositionsMaterialsAndOpennessRoundTrip()
    {
        var random = new Random(5);
        for (var i = 0; i < 2000; i++)
        {
            var position = new Vector3(
                -0.5f + 33f * random.NextSingle(),
                -0.5f + 33f * random.NextSingle(),
                -0.5f + 33f * random.NextSingle()
            );
            var material = (byte)random.Next(256);
            var openness = (byte)random.Next(256);

            var vertex = MeshVertex.Pack(position, Vector3.UnitY, material, openness);

            Assert.True(MathF.Abs(MeshVertex.DecodePosition(vertex).X - position.X) <= 1f / 256f);
            Assert.True(MathF.Abs(MeshVertex.DecodePosition(vertex).Y - position.Y) <= 1f / 256f);
            Assert.True(MathF.Abs(MeshVertex.DecodePosition(vertex).Z - position.Z) <= 1f / 256f);
            Assert.Equal(material, MeshVertex.DecodeMaterial(vertex));
            Assert.Equal(openness, MeshVertex.DecodeOpenness(vertex));
        }
    }

    [Fact]
    public void TheEdgesOfTheBlockRangeFit()
    {
        var low = MeshVertex.Pack(new Vector3(-0.5f), Vector3.UnitY, 1, 1);
        var high = MeshVertex.Pack(new Vector3(32.5f), Vector3.UnitY, 1, 1);

        Assert.Equal(new Vector3(-0.5f), MeshVertex.DecodePosition(low));
        Assert.Equal(new Vector3(32.5f), MeshVertex.DecodePosition(high));
    }

    [Fact]
    public void NormalsRoundTripIncludingNegativeZAndZeroComponents()
    {
        var normals = new List<Vector3>
        {
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ,
            new(0.3f, -0.2f, -0.9f),
            new(-0.5f, 0.5f, -0.7f),
            new(0f, -0.6f, -0.8f),
            new(-0.6f, 0f, -0.8f),
            new(0f, 0.6f, 0.8f),
            new(1f, 1f, 1f),
            new(-1f, -1f, -1f),
            new(1f, 0f, -1f),
            new(0f, -1f, -1f),
            new(1e-3f, -1e-3f, -1f),
        };
        var random = new Random(11);
        for (var i = 0; i < 3000; i++)
        {
            normals.Add(
                new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f)
            );
        }

        foreach (var raw in normals)
        {
            var normal = Vector3.Normalize(raw);

            var decoded = MeshVertex.DecodeNormal(MeshVertex.Pack(Vector3.Zero, normal, 0, 0));

            Assert.True(Vector3.Distance(normal, decoded) < 0.001f, $"{normal} came back as {decoded}");
            Assert.True(MathF.Abs(decoded.Length() - 1f) < 1e-4f);
        }
    }

    [Fact]
    public void ALongNormalStoresItsDirectionAndAZeroNormalDoesNotBreak()
    {
        var stretched = MeshVertex.DecodeNormal(MeshVertex.Pack(Vector3.Zero, new Vector3(0f, 0f, -250f), 0, 0));
        Assert.True(Vector3.Distance(stretched, -Vector3.UnitZ) < 0.001f);

        var zero = MeshVertex.DecodeNormal(MeshVertex.Pack(Vector3.Zero, Vector3.Zero, 0, 0));
        Assert.True(Vector3.Distance(zero, Vector3.UnitZ) < 0.001f);
    }
}
