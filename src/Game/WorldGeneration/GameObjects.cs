using Itera.Engine.Maths;
using Itera.Engine.Objects;

namespace Game.WorldGeneration;

/// <summary>
/// The starter's voxel objects (<c>docs/voxel-objects.md</c>), all of <see cref="UnitCentimetres"/> cm voxels: a broadleaf
/// tree about 9 m tall with a crown 6 m across, a young tree about 5 m tall, a bush about 1.6 m across, and a rock about
/// 70 cm high, each in <see cref="Shapes"/> shapes drawn from the deterministic hash, so neighbours differ; and boulders
/// of <see cref="BoulderRadii"/> (2 to 6 m across), each in <see cref="BoulderShapes"/> shapes, a third of each buried.
/// Trunks, branches, and stems are bark; crowns and bushes leaves, in smooth clumps (soft, not rough); rocks rock; boulders
/// boulder stone, lumpy. Registered at the run's start in a fixed order (<see cref="Register"/>), so the generator's
/// placer (<see cref="GameForests"/>) names each by its number (<see cref="Id"/>, <see cref="BoulderId"/>). Built with + - * /, square roots, and the deterministic series
/// (<see cref="DeterministicMath.CosSin"/>), so every peer builds the same voxels; a change to a shape changes the kinds'
/// fingerprint, so the match's identity.
/// </summary>
public static class GameObjects
{
    /// <summary>The objects' voxels, in centimetres.</summary>
    public const int UnitCentimetres = 5;

    /// <summary>The shapes of each kind but the boulders.</summary>
    public const int Shapes = 3;

    /// <summary>The shapes of each boulder size.</summary>
    public const int BoulderShapes = 2;

    /// <summary>The boulders' sizes: their radii, in centimetres (2, 3, 4.5, and 6 m across).</summary>
    public static IReadOnlyList<int> BoulderRadii { get; } = [100, 150, 225, 300];

    /// <summary>The kinds registered: every shape of the four kinds, then of each boulder size.</summary>
    public static int Count => 4 * Shapes + 4 * BoulderShapes;

    /// <summary>How far a boulder of <paramref name="radius"/> reaches from its anchor across, lumps included, in centimetres.</summary>
    public static int BoulderSpread(int radius) => radius * 23 / 20 + 10;

    /// <summary>
    /// How far each kind spreads from its anchor across, in centimetres: its box's half width, which holds its crown or
    /// itself at every draw.
    /// </summary>
    public static int SpreadCentimetres(GameObjectKind kind) =>
        kind switch
        {
            GameObjectKind.Broadleaf => 320,
            GameObjectKind.Young => 160,
            GameObjectKind.Bush => 100,
            _ => 85,
        };

    /// <summary>The number <see cref="Register"/> gives shape <paramref name="shape"/> of <paramref name="kind"/>.</summary>
    public static VoxelObjectKindId Id(GameObjectKind kind, int shape) => new((ushort)((int)kind * Shapes + shape));

    /// <summary>The number <see cref="Register"/> gives shape <paramref name="shape"/> of boulder size <paramref name="size"/>.</summary>
    public static VoxelObjectKindId BoulderId(int size, int shape) =>
        new((ushort)(4 * Shapes + size * BoulderShapes + shape));

    /// <summary>True when <paramref name="id"/> is one of the boulders' kinds, with its size and shape.</summary>
    public static bool IsBoulder(VoxelObjectKindId id, out int size, out int shape)
    {
        var index = id.Value - 4 * Shapes;
        size = index / BoulderShapes;
        shape = index % BoulderShapes;
        return index >= 0 && size < BoulderRadii.Count;
    }

    /// <summary>
    /// Registers every kind's shapes, kind by kind and shape by shape, as <see cref="Id"/> numbers them. At the run's start
    /// (<c>GameRunStartup</c>), before any other registers a kind. Throws when a kind took another number.
    /// </summary>
    public static void Register(VoxelObjectKinds kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        foreach (var kind in Enum.GetValues<GameObjectKind>())
        {
            for (var shape = 0; shape < Shapes; shape++)
            {
                Check(kinds.Register(Name(kind, shape), Build(kind, shape)), Id(kind, shape), Name(kind, shape));
            }
        }

        for (var size = 0; size < BoulderRadii.Count; size++)
        {
            for (var shape = 0; shape < BoulderShapes; shape++)
            {
                var name = BoulderName(size, shape);
                Check(kinds.Register(name, BuildBoulder(size, shape)), BoulderId(size, shape), name);
            }
        }

        static void Check(VoxelObjectKindId id, VoxelObjectKindId expected, string name)
        {
            if (id != expected)
            {
                throw new InvalidOperationException(
                    $"Object kind {name} took number {id.Value}, not {expected.Value}: the game's kinds are registered first."
                );
            }
        }
    }

    /// <summary>The name a boulder's shape is registered under: <c>boulder-0-0</c> (size, shape) and so on.</summary>
    public static string BoulderName(int size, int shape) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"boulder-{size}-{shape}");

    /// <summary>
    /// Builds shape <paramref name="shape"/> of boulder size <paramref name="size"/>: a broad lump of boulder stone, its
    /// middle a third of its radius over the ground (so a third of its height buried), and one or two smaller lumps
    /// bulging from its sides.
    /// </summary>
    public static VoxelObjectShape BuildBoulder(int size, int shape)
    {
        var radius = BoulderRadii[size];
        var state = DeterministicMath.Hash(shape, size, 0x626F756C646572, 0x73686170);
        var half = BoulderSpread(radius);
        // The box from the ball's lowest point: its middle a third of the radius over the ground, the anchor's height.
        var buried = radius * 2 / 3;
        var height = radius * 2 + radius / 10 + 10;
        var shapeOf = new VoxelObjectShape(
            UnitCentimetres,
            new(
                (2 * half + UnitCentimetres - 1) / UnitCentimetres,
                (height + UnitCentimetres - 1) / UnitCentimetres,
                (2 * half + UnitCentimetres - 1) / UnitCentimetres
            ),
            new(half, buried, half)
        );
        double middle = radius;
        shapeOf.FillEllipsoid(
            half,
            middle,
            half,
            radius * DeterministicMath.NextBetween(ref state, 0.95, 1.05),
            radius * DeterministicMath.NextBetween(ref state, 0.85, 0.95),
            radius * DeterministicMath.NextBetween(ref state, 0.9, 1.0),
            GameMaterials.Boulder
        );
        var lumps = DeterministicMath.NextRange(ref state, 1, 2);
        for (var i = 0; i < lumps; i++)
        {
            var (cos, sin) = DeterministicMath.CosSin(DeterministicMath.NextUnit(ref state));
            var distance = radius * DeterministicMath.NextBetween(ref state, 0.4, 0.55);
            var lump = radius * DeterministicMath.NextBetween(ref state, 0.45, 0.55);
            shapeOf.FillEllipsoid(
                half + cos * distance,
                middle + radius * DeterministicMath.NextBetween(ref state, -0.1, 0.2),
                half + sin * distance,
                lump,
                lump * 0.85,
                lump,
                GameMaterials.Boulder
            );
        }

        return shapeOf;
    }

    /// <summary>
    /// The top of boulder <paramref name="id"/>'s voxels over column (<paramref name="x"/>, <paramref name="z"/>) of its
    /// box, in centimetres from its anchor's height, or <see cref="int.MinValue"/> where the column holds none (or lies
    /// outside the box). Each boulder's tops are worked out at the first read (built again from its shape) and kept; any
    /// thread.
    /// </summary>
    public static int BoulderTop(VoxelObjectKindId id, int x, int z)
    {
        if (!IsBoulder(id, out var size, out var shape))
        {
            return int.MinValue;
        }

        var tops = s_boulderTops[size * BoulderShapes + shape].Value;
        return (uint)x < (uint)tops.SizeX && (uint)z < (uint)tops.SizeZ ? tops.Tops[x + tops.SizeX * z] : int.MinValue;
    }

    /// <summary>The anchor of boulder <paramref name="id"/> in centimetres from its box's corner.</summary>
    public static (int X, int Y, int Z) BoulderAnchor(VoxelObjectKindId id)
    {
        IsBoulder(id, out var size, out _);
        var radius = BoulderRadii[size];
        return (BoulderSpread(radius), radius * 2 / 3, BoulderSpread(radius));
    }

    private static readonly Lazy<BoulderTops>[] s_boulderTops = MakeBoulderTops();

    private static Lazy<BoulderTops>[] MakeBoulderTops()
    {
        var tops = new Lazy<BoulderTops>[4 * BoulderShapes];
        for (var i = 0; i < tops.Length; i++)
        {
            var (size, shape) = (i / BoulderShapes, i % BoulderShapes);
            tops[i] = new Lazy<BoulderTops>(() =>
                BoulderTops.Of(BuildBoulder(size, shape), BoulderRadii[size] * 2 / 3)
            );
        }

        return tops;
    }

    /// <summary>A boulder's tops: for each column of its box, its highest solid voxel's top from the anchor's height.</summary>
    private sealed record BoulderTops(int SizeX, int SizeZ, int[] Tops)
    {
        public static BoulderTops Of(VoxelObjectShape shape, int anchorY)
        {
            var size = shape.Size;
            var tops = new int[size.X * size.Z];
            Array.Fill(tops, int.MinValue);
            for (var z = 0; z < size.Z; z++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    for (var y = size.Y - 1; y >= 0; y--)
                    {
                        if (shape.MaterialAt(x, y, z) != Itera.Engine.Voxels.VoxelConstants.Air)
                        {
                            tops[x + size.X * z] = (y + 1) * UnitCentimetres - anchorY;
                            break;
                        }
                    }
                }
            }

            return new BoulderTops(size.X, size.Z, tops);
        }
    }

    /// <summary>The name a kind's shape is registered under: <c>broadleaf-0</c> and so on.</summary>
    public static string Name(GameObjectKind kind, int shape) =>
        kind switch
        {
            GameObjectKind.Broadleaf => "broadleaf",
            GameObjectKind.Young => "young-tree",
            GameObjectKind.Bush => "bush",
            _ => "rock",
        }
        + "-"
        + shape.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Builds shape <paramref name="shape"/> of <paramref name="kind"/>.</summary>
    public static VoxelObjectShape Build(GameObjectKind kind, int shape)
    {
        var state = DeterministicMath.Hash(shape, (int)kind, 0x6F626A65637473, 0x73686170);
        return kind switch
        {
            GameObjectKind.Broadleaf => Broadleaf(ref state),
            GameObjectKind.Young => Young(ref state),
            GameObjectKind.Bush => Bush(ref state),
            _ => Rock(ref state),
        };
    }

    /// <summary>
    /// A broadleaf tree: a leaning trunk 48 cm across at its foot, buried 30 cm, tapering to 28 cm at about 6.5 m; five
    /// branches from 3.8 to 5.6 m; and a crown of ten clumps round the upper trunk and one on top, about 6 m across and
    /// reaching about 9.2 m.
    /// </summary>
    private static VoxelObjectShape Broadleaf(ref ulong state)
    {
        const double Middle = 320,
            Ground = 30;
        var shape = new VoxelObjectShape(UnitCentimetres, new(128, 196, 128), new(320, 30, 320));
        var leanX = DeterministicMath.NextBetween(ref state, -25, 25);
        var leanZ = DeterministicMath.NextBetween(ref state, -25, 25);
        var top = Ground + 640 + DeterministicMath.NextBetween(ref state, -30, 30);
        Trunk(shape, Middle, Ground, leanX, leanZ, top, [24, 19, 14], [200, 420]);
        for (var i = 0; i < 5; i++)
        {
            var turn = (i + DeterministicMath.NextBetween(ref state, -0.25, 0.25)) / 5;
            var (cos, sin) = DeterministicMath.CosSin(turn);
            var height = Ground + 380 + DeterministicMath.NextBetween(ref state, 0, 180);
            var share = (height - Ground) / (top - Ground);
            double fromX = Middle + leanX * share,
                fromZ = Middle + leanZ * share;
            var reach = DeterministicMath.NextBetween(ref state, 140, 200);
            var rise = DeterministicMath.NextBetween(ref state, 90, 150);
            shape.FillCapsule(
                fromX,
                height,
                fromZ,
                fromX + cos * reach,
                height + rise,
                fromZ + sin * reach,
                8,
                GameMaterials.Bark
            );
        }

        Crown(
            shape,
            ref state,
            Middle + leanX,
            Middle + leanZ,
            Ground + 520,
            Ground + 800,
            10,
            70,
            150,
            110,
            140,
            80,
            105
        );
        shape.FillEllipsoid(Middle + leanX, Ground + 820, Middle + leanZ, 130, 95, 130, GameMaterials.Leaves);
        return shape;
    }

    /// <summary>
    /// A young tree: a trunk 20 cm across at its foot, buried 30 cm, three thin branches, and a crown of five clumps and one
    /// on top, about 3 m across and reaching about 5.4 m.
    /// </summary>
    private static VoxelObjectShape Young(ref ulong state)
    {
        const double Middle = 160,
            Ground = 30;
        var shape = new VoxelObjectShape(UnitCentimetres, new(64, 120, 64), new(160, 30, 160));
        var leanX = DeterministicMath.NextBetween(ref state, -12, 12);
        var leanZ = DeterministicMath.NextBetween(ref state, -12, 12);
        var top = Ground + 400 + DeterministicMath.NextBetween(ref state, -20, 20);
        Trunk(shape, Middle, Ground, leanX, leanZ, top, [10, 8, 6], [150, 280]);
        for (var i = 0; i < 3; i++)
        {
            var turn = (i + DeterministicMath.NextBetween(ref state, -0.3, 0.3)) / 3;
            var (cos, sin) = DeterministicMath.CosSin(turn);
            var height = Ground + 230 + DeterministicMath.NextBetween(ref state, 0, 120);
            var share = (height - Ground) / (top - Ground);
            double fromX = Middle + leanX * share,
                fromZ = Middle + leanZ * share;
            var reach = DeterministicMath.NextBetween(ref state, 60, 90);
            shape.FillCapsule(
                fromX,
                height,
                fromZ,
                fromX + cos * reach,
                height + reach * 0.8,
                fromZ + sin * reach,
                5,
                GameMaterials.Bark
            );
        }

        Crown(shape, ref state, Middle + leanX, Middle + leanZ, Ground + 300, Ground + 440, 5, 30, 65, 60, 80, 50, 62);
        shape.FillEllipsoid(Middle + leanX, Ground + 470, Middle + leanZ, 70, 55, 70, GameMaterials.Leaves);
        return shape;
    }

    /// <summary>A bush: three stems, buried 20 cm, under four clumps of leaves round a middle one, about 1.6 m across.</summary>
    private static VoxelObjectShape Bush(ref ulong state)
    {
        const double Middle = 100,
            Ground = 20;
        var shape = new VoxelObjectShape(UnitCentimetres, new(40, 34, 40), new(100, 20, 100));
        for (var i = 0; i < 3; i++)
        {
            var (cos, sin) = DeterministicMath.CosSin((i + DeterministicMath.NextBetween(ref state, -0.3, 0.3)) / 3);
            shape.FillCapsule(
                Middle + cos * 8,
                0,
                Middle + sin * 8,
                Middle + cos * 35,
                Ground + 65,
                Middle + sin * 35,
                3,
                GameMaterials.Bark
            );
        }

        Crown(shape, ref state, Middle, Middle, Ground + 50, Ground + 85, 4, 20, 40, 40, 52, 34, 42);
        shape.FillEllipsoid(Middle, Ground + 70, Middle, 55, 45, 55, GameMaterials.Leaves);
        return shape;
    }

    /// <summary>A rock: a broad lump and one or two smaller ones, buried 25 cm, about 1.5 m long and 70 cm high.</summary>
    private static VoxelObjectShape Rock(ref ulong state)
    {
        const double MiddleX = 85,
            MiddleZ = 75,
            Ground = 25;
        var shape = new VoxelObjectShape(UnitCentimetres, new(34, 22, 30), new(85, 25, 75));
        shape.FillEllipsoid(
            MiddleX,
            Ground + 20,
            MiddleZ,
            DeterministicMath.NextBetween(ref state, 62, 72),
            DeterministicMath.NextBetween(ref state, 42, 50),
            DeterministicMath.NextBetween(ref state, 52, 62),
            GameMaterials.Rock
        );
        var lumps = DeterministicMath.NextRange(ref state, 1, 2);
        for (var i = 0; i < lumps; i++)
        {
            var (cos, sin) = DeterministicMath.CosSin(DeterministicMath.NextUnit(ref state));
            var radius = DeterministicMath.NextBetween(ref state, 32, 42);
            shape.FillEllipsoid(
                MiddleX + cos * 30,
                Ground + 15,
                MiddleZ + sin * 22,
                radius,
                radius * 0.8,
                radius,
                GameMaterials.Rock
            );
        }

        return shape;
    }

    /// <summary>
    /// A trunk from the box's floor up to <paramref name="top"/>, leaning by (<paramref name="leanX"/>,
    /// <paramref name="leanZ"/>) at its top, in capsules of the radii given, each ending at the next break's height.
    /// </summary>
    private static void Trunk(
        VoxelObjectShape shape,
        double middle,
        double ground,
        double leanX,
        double leanZ,
        double top,
        double[] radii,
        double[] breaks
    )
    {
        double fromY = 0;
        for (var i = 0; i < radii.Length; i++)
        {
            var toY = i < breaks.Length ? ground + breaks[i] : top;
            double fromShare = Math.Max(0, fromY - ground) / (top - ground),
                toShare = (toY - ground) / (top - ground);
            shape.FillCapsule(
                middle + leanX * fromShare,
                fromY,
                middle + leanZ * fromShare,
                middle + leanX * toShare,
                toY,
                middle + leanZ * toShare,
                radii[i],
                GameMaterials.Bark
            );
            fromY = toY;
        }
    }

    /// <summary>
    /// A crown of <paramref name="clumps"/> clumps of leaves round (<paramref name="x"/>, <paramref name="z"/>), each an
    /// upright ellipsoid at its own turn about it, from <paramref name="nearest"/> to <paramref name="farthest"/> out and
    /// from <paramref name="low"/> to <paramref name="high"/> up, its radius across and up drawn between the bounds given.
    /// </summary>
    private static void Crown(
        VoxelObjectShape shape,
        ref ulong state,
        double x,
        double z,
        double low,
        double high,
        int clumps,
        double nearest,
        double farthest,
        double leastAcross,
        double mostAcross,
        double leastUp,
        double mostUp
    )
    {
        for (var i = 0; i < clumps; i++)
        {
            var (cos, sin) = DeterministicMath.CosSin(
                (i + DeterministicMath.NextBetween(ref state, -0.3, 0.3)) / clumps
            );
            var distance = DeterministicMath.NextBetween(ref state, nearest, farthest);
            var across = DeterministicMath.NextBetween(ref state, leastAcross, mostAcross);
            shape.FillEllipsoid(
                x + cos * distance,
                DeterministicMath.NextBetween(ref state, low, high),
                z + sin * distance,
                across,
                DeterministicMath.NextBetween(ref state, leastUp, mostUp),
                across,
                GameMaterials.Leaves
            );
        }
    }
}

/// <summary>The starter's kinds of voxel object (<see cref="GameObjects"/>).</summary>
public enum GameObjectKind
{
    /// <summary>A broadleaf tree about 9 m tall.</summary>
    Broadleaf,

    /// <summary>A young tree about 5 m tall.</summary>
    Young,

    /// <summary>A bush about 1.6 m across.</summary>
    Bush,

    /// <summary>A rock about 70 cm high.</summary>
    Rock,
}
