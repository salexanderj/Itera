using System.Numerics;
using Itera.Engine.Assets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The yard's meshes (<see cref="Yard"/>): every face of a closed piece looks out of it (so none is culled from outside
/// and Bepu, which meets a mesh from its front, meets it), the ground looks up, the ramps rise at 20 and 40 degrees,
/// the ground's checker texture has every mip, and no two pieces overlap.
/// </summary>
public sealed class YardTests
{
    private readonly MeshAssets _assets = new(NullLogger<MeshAssets>.Instance);

    public YardTests() => Yard.Add(_assets);

    [Theory]
    [InlineData(Yard.Block)]
    [InlineData(Yard.GentleRamp)]
    [InlineData(Yard.SteepRamp)]
    public void Every_face_of_a_closed_piece_looks_out_of_it(string name)
    {
        var asset = _assets[_assets.Find(name)];
        var centre = Vector3.Zero;
        foreach (var vertex in asset.Vertices)
        {
            centre += vertex.Position;
        }

        centre /= asset.Vertices.Length;
        foreach (var (a, b, c) in Triangles(asset))
        {
            var front = Vector3.Cross(b - a, c - a);
            Assert.True(Vector3.Dot(front, (a + b + c) / 3 - centre) > 0, $"A face of {name} looks inwards.");
        }
    }

    [Fact]
    public void The_ground_looks_up()
    {
        foreach (var (a, b, c) in Triangles(_assets[_assets.Find(Yard.Ground)]))
        {
            Assert.True(Vector3.Normalize(Vector3.Cross(b - a, c - a)).Y > 0.999f);
        }
    }

    [Theory]
    [InlineData(Yard.GentleRamp, 20)]
    [InlineData(Yard.SteepRamp, 40)]
    public void A_ramp_rises_at_its_angle(string name, float degrees)
    {
        var slopes = Triangles(_assets[_assets.Find(name)])
            .Select(t => Vector3.Normalize(Vector3.Cross(t.B - t.A, t.C - t.A)))
            .Where(normal => normal.Y > 0.01f && normal.Y < 0.99f)
            .ToList();

        Assert.Equal(2, slopes.Count);
        Assert.All(slopes, normal => Assert.Equal(MathF.Cos(degrees * MathF.PI / 180), normal.Y, 1e-4f));
    }

    [Fact]
    public void The_grounds_checker_has_every_mip()
    {
        var texture = Assert.Single(_assets[_assets.Find(Yard.Ground)].Textures);

        Assert.Equal((64, 64, 7), (texture.Width, texture.Height, texture.Levels));
        Assert.Equal(MeshTextureRole.Colour, texture.Role);
    }

    [Fact]
    public void No_two_pieces_overlap()
    {
        var boxes = Yard
            .Pieces.Where(piece => piece.Asset != Yard.Ground)
            .Select(piece =>
            {
                var asset = _assets[_assets.Find(piece.Asset)];
                return (
                    Min: piece.Position + asset.BoundsMin * piece.Scale,
                    Max: piece.Position + asset.BoundsMax * piece.Scale
                );
            })
            .ToList();

        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var apart =
                    boxes[i].Max.X <= boxes[j].Min.X
                    || boxes[j].Max.X <= boxes[i].Min.X
                    || boxes[i].Max.Z <= boxes[j].Min.Z
                    || boxes[j].Max.Z <= boxes[i].Min.Z;
                Assert.True(apart, $"Pieces {i} and {j} overlap.");
            }
        }
    }

    private static IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> Triangles(MeshAsset asset)
    {
        for (var i = 0; i < asset.Indices.Length; i += 3)
        {
            yield return (
                asset.Vertices[asset.Indices[i]].Position,
                asset.Vertices[asset.Indices[i + 1]].Position,
                asset.Vertices[asset.Indices[i + 2]].Position
            );
        }
    }
}
