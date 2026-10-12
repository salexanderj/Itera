using Game.WorldGeneration;
using Itera.Engine.Objects;

namespace Game.Tests;

/// <summary>
/// Where the starter's objects stand (<see cref="GameForests"/>): any tiling of a box finds the same objects, in a fixed
/// order; none within the clear space round the middle, and nothing but a boulder on a boulder; no bush or rock against a
/// trunk; and forest over about 40 % of the map.
/// </summary>
public sealed class ForestTests
{
    private static readonly GameForests s_forests = new();

    [Fact]
    public void A_box_finds_what_its_quarters_find()
    {
        var whole = Find(-6_400, 6_400, 20_000, 32_800);
        var quarters = new List<ObjectPlacement>();
        foreach (var (x0, x1) in new[] { (-6_400, 0), (0, 6_400) })
        {
            foreach (var (z0, z1) in new[] { (20_000, 26_400), (26_400, 32_800) })
            {
                quarters.AddRange(Find(x0, x1, z0, z1));
            }
        }

        Assert.NotEmpty(whole);
        Assert.Equal(whole.OrderBy(p => p.Id.Value), quarters.OrderBy(p => p.Id.Value));
        Assert.Equal(whole, Find(-6_400, 6_400, 20_000, 32_800));
        Assert.Equal(whole.Count, whole.Select(p => p.Id).Distinct().Count());
    }

    /// <summary>
    /// Each object is found in the box from its anchor to a centimetre past it, and not in the boxes that end at its anchor
    /// or start a centimetre past it (min in, max out); every anchor lies in the box asked for.
    /// </summary>
    [Fact]
    public void A_box_holds_its_low_edges_and_not_its_high_ones()
    {
        var found = Find(-6_400, 6_400, 20_000, 32_800);
        Assert.All(
            found,
            p => Assert.True(p.Anchor.X is >= -6_400 and < 6_400 && p.Anchor.Z is >= 20_000 and < 32_800)
        );
        foreach (var placement in found.Take(20))
        {
            var (x, z) = (placement.Anchor.X, placement.Anchor.Z);
            Assert.Contains(placement, Find(x, x + 1, z, z + 1));
            Assert.DoesNotContain(placement, Find(x - 1, x, z, z + 1));
            Assert.DoesNotContain(placement, Find(x + 1, x + 2, z, z + 1));
            Assert.DoesNotContain(placement, Find(x, x + 1, z - 1, z));
            Assert.DoesNotContain(placement, Find(x, x + 1, z + 1, z + 2));
        }
    }

    [Fact]
    public void No_object_stands_near_the_middle_on_a_boulder_or_against_a_trunk()
    {
        var all = Find(-30_000, 30_000, -30_000, 30_000);
        var boulders = all.Where(p => GameObjects.IsBoulder(p.Kind, out _, out _)).ToList();
        foreach (var boulder in boulders)
        {
            GameObjects.IsBoulder(boulder.Kind, out var size, out _);
            var spread = GameObjects.BoulderSpread(GameObjects.BoulderRadii[size]);
            var (bx, bz) = (boulder.Anchor.X, boulder.Anchor.Z);
            Assert.True(Math.Sqrt((double)bx * bx + (double)bz * bz) >= GameForests.ClearCentimetres + spread);
        }

        var found = all.Except(boulders).ToList();
        Assert.True(boulders.Count > 100 && found.Count > 1_000, $"{boulders.Count} boulders, {found.Count} others.");
        var trunks = found.Where(p => KindOf(p) is GameObjectKind.Broadleaf or GameObjectKind.Young).ToList();
        foreach (var placement in found)
        {
            var kind = KindOf(placement);
            var spread = GameObjects.SpreadCentimetres(kind);
            var (x, z) = (placement.Anchor.X, placement.Anchor.Z);
            Assert.True(Math.Sqrt((double)x * x + (double)z * z) >= GameForests.ClearCentimetres + spread);
            Assert.False(GameForests.NearBoulder(x, z, spread, 1), $"{kind} at ({x}, {z}) on a boulder.");
            Assert.Equal((int)Math.Floor(GameGenerator.Height(x, z, 1)), placement.Anchor.Y);
            if (kind is GameObjectKind.Bush or GameObjectKind.Rock)
            {
                Assert.DoesNotContain(
                    trunks,
                    tree => Math.Sqrt(Math.Pow(tree.Anchor.X - x, 2) + Math.Pow(tree.Anchor.Z - z, 2)) < spread + 60
                );
            }
        }
    }

    [Fact]
    public void Forest_covers_about_two_fifths_of_the_map()
    {
        double cover = 0;
        var samples = 0;
        for (var x = -400_000; x < 400_000; x += 5_000)
        {
            for (var z = -400_000; z < 400_000; z += 5_000)
            {
                cover += GameForests.Cover(x, z, 1);
                samples++;
            }
        }

        Assert.InRange(cover / samples, 0.33, 0.47);
    }

    private static List<ObjectPlacement> Find(int minX, int maxX, int minZ, int maxZ)
    {
        var found = new List<ObjectPlacement>();
        s_forests.Find(1, minX, maxX, minZ, maxZ, found);
        return found;
    }

    private static GameObjectKind KindOf(ObjectPlacement placement) =>
        (GameObjectKind)(placement.Kind.Value / GameObjects.Shapes);
}
