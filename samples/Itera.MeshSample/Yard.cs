using System.Numerics;
using Itera.Engine.Assets;
using Itera.Engine.Rendering.GpuData;

namespace Itera.MeshSample;

/// <summary>A piece of the yard: a mesh asset by name, where its own origin stands, and its uniform scale.</summary>
/// <param name="Asset">The asset's name (<see cref="Yard"/>'s).</param>
/// <param name="Position">Where the asset's origin stands, in metres.</param>
/// <param name="Scale">Its uniform scale.</param>
public readonly record struct YardPiece(string Asset, Vector3 Position, float Scale = 1);

/// <summary>
/// The sample's world, all built in code: a 60 m square of flat ground (a grey-green checker of 1 m squares), cubes of
/// stone 1, 2, 3, and 4 m tall that the player climbs (their material <see cref="StoneMaterial"/>, tagged
/// <see cref="Climbable"/>), two ramps, of 20 and 40 degrees, a pane of glass, and two amber see-through cubes behind it
/// as the start sees them (blended, two-sided materials: drawn back to front, the far faces through the near). Each
/// piece is drawn as a mesh asset and collides as the same triangles (<c>MeshColliders</c>).
/// </summary>
public static class Yard
{
    public const string Ground = "yard-ground";
    public const string Block = "yard-block";
    public const string GentleRamp = "yard-ramp-20";
    public const string SteepRamp = "yard-ramp-40";
    public const string Glass = "yard-glass";
    public const string Amber = "yard-amber";

    /// <summary>The material the player climbs.</summary>
    public const string StoneMaterial = "stone";

    /// <summary>The surface tag the climbing mode climbs.</summary>
    public const uint Climbable = 1;

    /// <summary>How far the ground reaches from the middle, in metres.</summary>
    public const float GroundHalfMetres = 30;

    /// <summary>
    /// Where the pieces stand: the ground, the four blocks along the north, the ramps to the west and east, and the glass
    /// pane before the start with the amber cubes beyond it.
    /// </summary>
    public static IReadOnlyList<YardPiece> Pieces { get; } =
    [
        new(Ground, Vector3.Zero),
        new(Block, new Vector3(-7, 0, -4), 1),
        new(Block, new Vector3(-4, 0, -6), 2),
        new(Block, new Vector3(0, 0, -8), 3),
        new(Block, new Vector3(5, 0, -10), 4),
        new(GentleRamp, new Vector3(-16, 0, -2)),
        new(SteepRamp, new Vector3(10, 0, 0)),
        new(Glass, new Vector3(2, 0, 1)),
        new(Amber, new Vector3(6, 0, -3)),
        new(Amber, new Vector3(7.5f, 0, -2.5f)),
    ];

    /// <summary>The surface tags of each of the yard's materials: the stone's climbable.</summary>
    public static uint TagsOf(string material) => material == StoneMaterial ? Climbable : 0;

    /// <summary>Adds the yard's assets to the run's (at its run startup).</summary>
    public static void Add(MeshAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        assets.Add(Ground, GroundMesh());
        assets.Add(Block, BlockMesh());
        assets.Add(GentleRamp, RampMesh(GentleRamp, 6, 3, 20));
        assets.Add(SteepRamp, RampMesh(SteepRamp, 4, 3, 40));
        assets.Add(
            Glass,
            SeeThroughBox(Glass, "glass", new Vector3(3, 2.5f, 0.1f), new Vector4(0.55f, 0.75f, 0.85f, 0.3f))
        );
        assets.Add(Amber, SeeThroughBox(Amber, "amber", Vector3.One, new Vector4(0.9f, 0.55f, 0.15f, 0.5f)));
    }

    /// <summary>
    /// A box from the origin to <paramref name="size"/> of a blended, two-sided material of <paramref name="colour"/> (its
    /// alpha how much it covers what is behind).
    /// </summary>
    private static MeshAsset SeeThroughBox(string name, string material, Vector3 size, Vector4 colour)
    {
        var builder = new Builder();
        builder.Box(Vector3.Zero, size);
        var data = MeshMaterialData.Default with
        {
            Name = material,
            BaseColour = colour,
            AlphaMode = MeshAlphaMode.Blend,
            DoubleSided = true,
        };
        return builder.Build(name, data, []);
    }

    /// <summary>The ground: one square at y = 0, its texture repeated every 2 m (a check a metre).</summary>
    private static MeshAsset GroundMesh()
    {
        var builder = new Builder();
        var h = GroundHalfMetres;
        builder.Quad(
            new Vector3(-h, 0, -h),
            new Vector3(-h, 0, h),
            new Vector3(h, 0, h),
            new Vector3(h, 0, -h),
            uvMetres: 2
        );
        var material = MeshMaterialData.Default with
        {
            Name = "ground",
            BaseColourTexture = new MeshTextureRef(0, MeshSampler.LinearRepeat, 0),
        };
        return builder.Build(Ground, material, [Checker()]);
    }

    /// <summary>A cube from (0, 0, 0) to (1, 1, 1) of stone.</summary>
    private static MeshAsset BlockMesh()
    {
        var builder = new Builder();
        builder.Box(Vector3.Zero, Vector3.One);
        var material = MeshMaterialData.Default with
        {
            Name = StoneMaterial,
            BaseColour = new Vector4(0.48f, 0.36f, 0.26f, 1),
        };
        return builder.Build(Block, material, []);
    }

    /// <summary>
    /// A ramp <paramref name="length"/> metres long along +x from its origin, <paramref name="width"/> wide along +z,
    /// rising at <paramref name="degrees"/> to a back wall: a closed wedge.
    /// </summary>
    private static MeshAsset RampMesh(string name, float length, float width, float degrees)
    {
        var height = length * MathF.Tan(degrees * MathF.PI / 180);
        Vector3 a = new(0, 0, 0),
            b = new(length, 0, 0),
            c = new(length, 0, width),
            d = new(0, 0, width),
            e = new(length, height, 0),
            f = new(length, height, width);
        var builder = new Builder();
        builder.Quad(a, d, f, e);
        builder.Quad(b, e, f, c);
        builder.Quad(a, b, c, d);
        builder.Triangle(a, e, b);
        builder.Triangle(d, c, f);
        var material = MeshMaterialData.Default with { Name = "ramp", BaseColour = new Vector4(0.4f, 0.4f, 0.38f, 1) };
        return builder.Build(name, material, []);
    }

    /// <summary>A 64-texel square of two grey-greens in a 2 by 2 checker.</summary>
    private static MeshTextureData Checker()
    {
        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var light = (x < size / 2) == (y < size / 2);
                var i = 4 * (x + size * y);
                (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = light
                    ? ((byte)112, (byte)128, (byte)108, (byte)255)
                    : ((byte)96, (byte)112, (byte)94, (byte)255);
            }
        }

        return MeshTextureData.FromRgba("yard checker", size, size, MeshTextureRole.Colour, rgba);
    }

    /// <summary>
    /// Flat-shaded triangles, each face its own vertices (counter-clockwise seen from its front, which faces out of a
    /// closed shape), with texture coordinates from x and z.
    /// </summary>
    private sealed class Builder
    {
        private readonly List<MeshAssetVertex> _vertices = [];
        private readonly List<uint> _indices = [];

        public void Triangle(Vector3 a, Vector3 b, Vector3 c, float uvMetres = 1)
        {
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var first = (uint)_vertices.Count;
            foreach (var corner in (ReadOnlySpan<Vector3>)[a, b, c])
            {
                _vertices.Add(
                    MeshAssetVertex.Pack(
                        corner,
                        normal,
                        new Vector4(1, 0, 0, 1),
                        new Vector2(corner.X, corner.Z) / uvMetres,
                        Vector2.Zero,
                        Vector4.One
                    )
                );
            }

            _indices.AddRange([first, first + 1, first + 2]);
        }

        /// <summary>Corners in order counter-clockwise from the front.</summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float uvMetres = 1)
        {
            Triangle(a, b, c, uvMetres);
            Triangle(a, c, d, uvMetres);
        }

        public void Box(Vector3 min, Vector3 max)
        {
            Vector3 C(int x, int y, int z) =>
                new(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
            Quad(C(0, 0, 0), C(1, 0, 0), C(1, 0, 1), C(0, 0, 1));
            Quad(C(0, 1, 0), C(0, 1, 1), C(1, 1, 1), C(1, 1, 0));
            Quad(C(0, 0, 0), C(0, 1, 0), C(1, 1, 0), C(1, 0, 0));
            Quad(C(0, 0, 1), C(1, 0, 1), C(1, 1, 1), C(0, 1, 1));
            Quad(C(0, 0, 0), C(0, 0, 1), C(0, 1, 1), C(0, 1, 0));
            Quad(C(1, 0, 0), C(1, 1, 0), C(1, 1, 1), C(1, 0, 1));
        }

        public MeshAsset Build(string name, MeshMaterialData material, MeshTextureData[] textures) =>
            new(name, [.. _vertices], [.. _indices], [new MeshPrimitive(0, _indices.Count, 0)], [material], textures);
    }
}
