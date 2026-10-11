using System.Numerics;
using Itera.Engine.Assets;
using Itera.Engine.Maths;
using Itera.Engine.Rendering.GpuData;

namespace Itera.MeshSample;

/// <summary>A piece of the yard: a mesh asset by name, where its own origin stands, and its uniform scale.</summary>
/// <param name="Asset">The asset's name (<see cref="Yard"/>'s).</param>
/// <param name="Position">Where the asset's origin stands, an absolute point (metres from the map's middle).</param>
/// <param name="Scale">Its uniform scale.</param>
public readonly record struct YardPiece(string Asset, WorldPoint Position, float Scale = 1);

/// <summary>
/// The sample's world, all built in code: a 60 m square of flat ground (a grey-green checker of 1 m squares), cubes of
/// stone 1, 2, 3, and 4 m tall that the player climbs (their material <see cref="StoneMaterial"/>, tagged
/// <see cref="Climbable"/>), two ramps, of 20 and 40 degrees, a pane of glass, and two amber see-through cubes behind it
/// as the start sees them (blended, two-sided materials: drawn back to front, the far faces through the near), and a
/// screen 4 m up on a post past the blocks, ahead of the start, that shows the yard camera's picture
/// (<see cref="YardCamera"/>, an image view shown on its <see cref="ScreenMaterial"/>). Each piece is drawn as a mesh
/// asset and collides as the same triangles (<c>MeshColliders</c>).
/// </summary>
public static class Yard
{
    public const string Ground = "yard-ground";
    public const string Block = "yard-block";
    public const string GentleRamp = "yard-ramp-20";
    public const string SteepRamp = "yard-ramp-40";
    public const string Glass = "yard-glass";
    public const string Amber = "yard-amber";
    public const string Screen = "yard-screen";

    /// <summary>The screen's material that shows the yard camera's picture.</summary>
    public const string ScreenMaterial = "screen";

    /// <summary>The material the player climbs.</summary>
    public const string StoneMaterial = "stone";

    /// <summary>The surface tag the climbing mode climbs.</summary>
    public const uint Climbable = 1;

    /// <summary>How far the ground reaches from the middle, in metres.</summary>
    public const float GroundHalfMetres = 30;

    /// <summary>Where the screen stands: the foot of its panel's left side, absolute.</summary>
    public static readonly WorldPoint ScreenPlace = new(-5, 0, -12);

    /// <summary>The screen's picture: its lower left corner on the panel's front, and its size, in metres.</summary>
    private static readonly Vector3 s_pictureCorner = new(0.1f, 4.1f, 0.004f);

    private static readonly Vector2 s_pictureSize = new(3.2f, 1.8f);

    /// <summary>The middle of the screen's picture, absolute (where the sample's hum comes from).</summary>
    public static WorldPoint ScreenMiddle =>
        ScreenPlace.Plus(
            s_pictureCorner.X + s_pictureSize.X / 2,
            s_pictureCorner.Y + s_pictureSize.Y / 2,
            s_pictureCorner.Z
        );

    /// <summary>
    /// Where the pieces stand: the ground, the four blocks along the north, the ramps to the west and east, the glass
    /// pane before the start with the amber cubes beyond it, and the screen past the blocks.
    /// </summary>
    public static IReadOnlyList<YardPiece> Pieces { get; } =
    [
        new(Ground, WorldPoint.Zero),
        new(Block, new WorldPoint(-7, 0, -4), 1),
        new(Block, new WorldPoint(-4, 0, -6), 2),
        new(Block, new WorldPoint(0, 0, -8), 3),
        new(Block, new WorldPoint(5, 0, -10), 4),
        new(GentleRamp, new WorldPoint(-16, 0, -2)),
        new(SteepRamp, new WorldPoint(10, 0, 0)),
        new(Glass, new WorldPoint(2, 0, 1)),
        new(Amber, new WorldPoint(6, 0, -3)),
        new(Amber, new WorldPoint(7.5, 0, -2.5)),
        new(Screen, ScreenPlace),
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
        assets.Add(Screen, ScreenMesh());
    }

    /// <summary>
    /// The screen: a dark panel 3.4 m wide, 2 m tall, and 0.12 m deep, 4 m up on a post behind its middle, and on the
    /// panel's front (+z) a picture 3.2 by 1.8 m (16:9) whose material (<see cref="ScreenMaterial"/>) shows an image view:
    /// dimly lit, and glowing with it.
    /// </summary>
    private static MeshAsset ScreenMesh()
    {
        var builder = new Builder();
        builder.Box(new Vector3(1.6f, 0, -0.1f), new Vector3(1.8f, 4, -0.02f));
        builder.Box(new Vector3(0, 4, -0.12f), new Vector3(3.4f, 6, 0));
        builder.NextMaterial();
        builder.Picture(s_pictureCorner, s_pictureSize);
        var frame = MeshMaterialData.Default with
        {
            Name = "screen-frame",
            BaseColour = new Vector4(0.08f, 0.08f, 0.09f, 1),
        };
        var picture = MeshMaterialData.Default with
        {
            Name = ScreenMaterial,
            BaseColour = new Vector4(0.2f, 0.2f, 0.2f, 1),
            Emission = new Vector3(0.8f),
        };
        return builder.Build(Screen, [frame, picture], []);
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
        private readonly List<MeshPrimitive> _primitives = [];
        private int _primitiveStart;

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

        /// <summary>
        /// A quad in the plane of x and y facing +z, from <paramref name="corner"/> (its bottom left) across
        /// <paramref name="size"/>, its texture coordinates 0 to 1 across it, v 0 at its top (an image's first row).
        /// </summary>
        public void Picture(Vector3 corner, Vector2 size)
        {
            var first = (uint)_vertices.Count;
            var normal = Vector3.UnitZ;
            Vector3 a = corner,
                b = corner + new Vector3(size.X, 0, 0),
                c = corner + new Vector3(size.X, size.Y, 0),
                d = corner + new Vector3(0, size.Y, 0);
            foreach (
                var (point, uv) in (ReadOnlySpan<(Vector3, Vector2)>)
                    [(a, new(0, 1)), (b, new(1, 1)), (c, new(1, 0)), (d, new(0, 0))]
            )
            {
                _vertices.Add(
                    MeshAssetVertex.Pack(point, normal, new Vector4(1, 0, 0, 1), uv, Vector2.Zero, Vector4.One)
                );
            }

            _indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        /// <summary>Ends the triangles so far as one primitive of the next material; the rest are the one after.</summary>
        public void NextMaterial()
        {
            _primitives.Add(new MeshPrimitive(_primitiveStart, _indices.Count - _primitiveStart, _primitives.Count));
            _primitiveStart = _indices.Count;
        }

        public MeshAsset Build(string name, MeshMaterialData material, MeshTextureData[] textures) =>
            Build(name, [material], textures);

        /// <summary>The asset: a primitive a material, in order (<see cref="NextMaterial"/> ends each but the last).</summary>
        public MeshAsset Build(string name, MeshMaterialData[] materials, MeshTextureData[] textures)
        {
            NextMaterial();
            return new(name, [.. _vertices], [.. _indices], [.. _primitives], materials, textures);
        }
    }
}
