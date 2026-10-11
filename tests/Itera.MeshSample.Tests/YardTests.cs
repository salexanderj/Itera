using System.Numerics;
using Itera.Engine.Assets;
using Itera.MeshSample.Systems;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The yard's meshes (<see cref="Yard"/>): every face of a closed piece looks out of it (so none is culled from outside
/// and Bepu, which meets a mesh from its front, meets it), the ground looks up, the ramps rise at 20 and 40 degrees,
/// the ground's checker texture has every mip, no two pieces overlap, and the fox's circle keeps clear of every piece.
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

    /// <summary>
    /// The screen is two primitives: its post and dark panel, then its picture, which lies on the panel's front, faces
    /// +z (the start), and spans its texture coordinates 0 to 1, v 0 at the top, as an image view's rows run.
    /// </summary>
    [Fact]
    public void The_screens_picture_faces_the_start_and_spans_its_image()
    {
        var screen = _assets[_assets.Find(Yard.Screen)];

        Assert.Equal(["screen-frame", Yard.ScreenMaterial], screen.Materials.Select(m => m.Name));
        Assert.Equal(2, screen.Primitives.Length);
        var picture = screen.Primitives[1];
        Assert.Equal(1, picture.Material);
        var corners = new List<(Vector3 Position, Vector2 Uv)>();
        for (var i = picture.FirstIndex; i < picture.FirstIndex + picture.IndexCount; i++)
        {
            var vertex = screen.Vertices[screen.Indices[i]];
            corners.Add((vertex.Position, vertex.Uv));
        }

        var (low, high) = (corners.Min(c => c.Position.Y), corners.Max(c => c.Position.Y));
        foreach (var (position, uv) in corners)
        {
            Assert.Equal(position.X < 1 ? 0 : 1, uv.X);
            Assert.Equal(position.Y < (low + high) / 2 ? 1 : 0, uv.Y);
        }

        var panel = PanelBounds(screen, picture.FirstIndex);
        foreach (var (position, _) in corners)
        {
            Assert.True(position.Z > panel.Max.Z, "The picture lies behind the panel's front.");
            Assert.InRange(position.X, panel.Min.X, panel.Max.X);
            Assert.InRange(position.Y, panel.Min.Y, panel.Max.Y);
        }

        for (var i = 0; i < corners.Count; i += 3)
        {
            var (a, b, c) = (corners[i].Position, corners[i + 1].Position, corners[i + 2].Position);
            Assert.True(Vector3.Normalize(Vector3.Cross(b - a, c - a)).Z > 0.999f);
        }
    }

    /// <summary>
    /// The fox walks its circle with room to spare (a metre, more than half its length) from every piece's footprint, so
    /// no piece hides it or stands in its way.
    /// </summary>
    [Fact]
    public void The_foxs_circle_keeps_clear_of_every_piece()
    {
        const float room = 1;
        foreach (var piece in Yard.Pieces.Where(piece => piece.Asset != Yard.Ground))
        {
            var asset = _assets[_assets.Find(piece.Asset)];
            var min = AtMiddle.Local(piece.Position) + asset.BoundsMin * piece.Scale - new Vector3(room);
            var max = AtMiddle.Local(piece.Position) + asset.BoundsMax * piece.Scale + new Vector3(room);
            for (var step = 0; step < 360; step++)
            {
                var angle = step * MathF.PI / 180;
                var at =
                    AtMiddle.Local(SampleFoxSystem.Middle)
                    + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * SampleFoxSystem.CircleMetres;
                var inside = at.X > min.X && at.X < max.X && at.Z > min.Z && at.Z < max.Z;
                Assert.False(inside, $"The fox's circle passes within {room} m of {piece.Asset} at {piece.Position}.");
            }
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
                    Min: AtMiddle.Local(piece.Position) + asset.BoundsMin * piece.Scale,
                    Max: AtMiddle.Local(piece.Position) + asset.BoundsMax * piece.Scale
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

    /// <summary>The panel's box: the screen's corners before the picture from 4 m up (the post ends there).</summary>
    private static (Vector3 Min, Vector3 Max) PanelBounds(MeshAsset screen, int pictureFirstIndex)
    {
        var (min, max) = (new Vector3(float.MaxValue), new Vector3(float.MinValue));
        for (var i = 0; i < pictureFirstIndex; i++)
        {
            var position = screen.Vertices[screen.Indices[i]].Position;
            if (position.Y >= 4)
            {
                (min, max) = (Vector3.Min(min, position), Vector3.Max(max, position));
            }
        }

        return (min, max);
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
