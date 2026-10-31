using System.Numerics;
using Itera.Engine.Maths;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// A world on a grid of 10 cm (docs/rendering-redesign.md, phase 5 step 1): <see cref="GridShapesGenerator"/>'s shapes,
/// whose faces lie off the 10 cm lines, read back through the places where centimetres meet voxels: point reads, rays
/// and the boxes they name, edits and what they count, edits replayed into columns that load again across a column's
/// edge, and the sky field. Each is checked against the shapes at each voxel's sample point, worked out here by plain
/// division.
/// </summary>
public sealed class BaseTenWorldTests
{
    private const int Base = 10;

    /// <summary>
    /// The level-0 streaming radius, in metres: much less than a 12.8 m column, so the columns load because the disc
    /// meets them, not because their centres lie in it.
    /// </summary>
    private const double Radius = 3;
    private static readonly VoxelGrid Grid = new(Base);
    private readonly GridShapesGenerator _generator = new(Grid);

    [Fact]
    public void A_point_reads_what_the_shapes_hold_at_its_voxels_sample_point()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius);
        Assert.Equal(Base, world.Grid.BaseCentimetres);

        // The floor's top is the 10 cm line above 137 cm: voxel 13 (130 to 140 cm) is sampled at 135, below it.
        AssertMaterial(world, new VoxelCoord(100, 139, 100), GridShapesGenerator.Dirt);
        AssertMaterial(world, new VoxelCoord(100, 140, 100), VoxelConstants.Air);
        // The box starts at x 223 cm: voxel 22 (220 to 230 cm) is sampled at 225, inside it.
        AssertMaterial(world, new VoxelCoord(221, 200, 500), GridShapesGenerator.Stone);
        AssertMaterial(world, new VoxelCoord(219, 200, 500), VoxelConstants.Air);

        int points = 0,
            unlikeTheShapes = 0;
        for (var z = -260; z <= 640; z += 13)
        {
            for (var y = 40; y <= 360; y += 3)
            {
                for (var x = -420; x <= 520; x += 3)
                {
                    var expected = Expected(Voxel(x), Voxel(y), Voxel(z), level: 0);
                    if (!world.TryGetMaterial(new VoxelCoord(x, y, z), out var material) || material != expected)
                    {
                        Assert.Fail($"({x}, {y}, {z}) cm: {material}, expected {expected}.");
                    }

                    points++;
                    unlikeTheShapes += material != GridShapesGenerator.Material(x, y, z) ? 1 : 0;
                }
            }
        }

        // A world of 1 cm voxels would hold the shapes at every point; this one differs near every face.
        Assert.True(unlikeTheShapes > points / 100, $"{unlikeTheShapes} of {points} points differ from the shapes.");
    }

    [Fact]
    public void A_ray_straight_down_meets_the_floor_on_a_10_cm_line()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius);

        Assert.True(world.Raycast(new Vector3(1.03f, 2.95f, 0.57f), -Vector3.UnitY, 5, out var hit));

        Assert.Equal(new VoxelCoord(100, 130, 50), hit.Voxel);
        Assert.Equal(new VoxelCoord(0, 1, 0), hit.Normal);
        Assert.Equal(GridShapesGenerator.Dirt, hit.Material);
        Assert.Equal(1.55f, hit.DistanceMetres, 3);
        Assert.Equal(0, hit.Level);
        Assert.Equal(hit.Voxel, hit.BoxMin);
        Assert.Equal(Base, hit.BoxCentimetres);
    }

    [Fact]
    public void Random_rays_hit_what_a_walk_through_10_cm_voxels_hits()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius);
        var random = new Random(41);
        int hits = 0,
            same = 0;
        for (var ray = 0; ray < 1500; ray++)
        {
            var origin = new Vector3(
                (float)(random.NextDouble() * 11 - 5),
                (float)(random.NextDouble() * 2.5 + 1.5),
                (float)(random.NextDouble() * 10 - 3)
            );
            var pitch = -Math.Pow(random.NextDouble(), 2) * Math.PI / 2;
            var yaw = random.NextDouble() * Math.PI * 2;
            var direction = new Vector3(
                (float)(Math.Cos(yaw) * Math.Cos(pitch)),
                (float)Math.Sin(pitch),
                (float)(Math.Sin(yaw) * Math.Cos(pitch))
            );

            var found = world.Raycast(origin, direction, 6, out var hit);
            var expected = Walk(
                origin,
                direction,
                6,
                out var voxel,
                out var normal,
                out var material,
                out var distance
            );

            Assert.Equal(expected, found);
            if (!found)
            {
                continue;
            }

            hits++;
            var apart = Math.Max(
                Math.Abs(voxel.X - hit.Voxel.X),
                Math.Max(Math.Abs(voxel.Y - hit.Voxel.Y), Math.Abs(voxel.Z - hit.Voxel.Z))
            );
            // The same voxel, or a neighbour when the ray passes through an edge or a corner.
            Assert.True(apart <= Base, $"Ray {ray}: the walk hit {voxel}, the world's ray {hit.Voxel}.");
            Assert.Equal(0, hit.Level);
            Assert.Equal(hit.Voxel, hit.BoxMin);
            Assert.Equal(Base, hit.BoxCentimetres);
            if (voxel == hit.Voxel)
            {
                same++;
                Assert.Equal(material, hit.Material);
                Assert.Equal(normal, hit.Normal);
                Assert.Equal(distance, hit.DistanceMetres, 3);
            }
        }

        Assert.True(hits > 500, $"{hits} hits.");
        Assert.True(same > hits * 95 / 100, $"{same} of {hits} hits met the walk's voxel.");
    }

    [Fact]
    public void A_sphere_clears_each_voxel_by_the_share_it_covers_and_counts_what_it_takes()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius);
        // Round the box's low corner: stone above the floor, dirt below it.
        var edit = VoxelEdit.Sphere(new VoxelCoord(227, 141, 417), 63, VoxelEditOperation.Clear);
        var expected = ExpectedRemoved(edit, 0);
        var counted = new long[256];

        Assert.Equal(0, world.CountRemoved(edit, counted));

        Assert.True(expected[GridShapesGenerator.Stone] > 0 && expected[GridShapesGenerator.Dirt] > 0);
        Assert.Equal(expected, counted);
        world.Apply(edit);
        AssertEdited(world, edit);
    }

    /// <summary>
    /// A punch's dent, a sphere of 2 cm, marks the floor's top voxel by the share of it it covers and leaves it dirt; a
    /// count before it is what the voxel loses, in cubic centimetres.
    /// </summary>
    [Fact]
    public void A_dent_smaller_than_a_voxel_lowers_its_density_and_counts_what_it_takes()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius);
        // Voxel 13 (130 to 140 cm) is the floor's top: dirt, full, as the generator gives no densities.
        var edit = VoxelEdit.Sphere(new VoxelCoord(105, 139, 105), 2, VoxelEditOperation.Clear);
        var (covered, total) = Share(edit, new VoxelCoord(10, 13, 10), Base);
        Assert.InRange(covered, 10, 33);
        var lost = (VoxelDensity.Full * covered + total / 2) / total;
        var counted = new long[256];

        world.CountRemoved(edit, counted);
        world.Apply(edit);

        Assert.Equal((lost * 1000L + 127) / 255, counted[GridShapesGenerator.Dirt]);
        Assert.True(world.TryGetDensity(new VoxelCoord(105, 139, 105), 0, out var density));
        Assert.Equal(VoxelDensity.Full - lost, density);
        AssertMaterial(world, new VoxelCoord(105, 139, 105), GridShapesGenerator.Dirt);
        AssertEdited(world, edit, turnsToAir: false);
    }

    /// <summary>A sphere filled into the air makes the voxels it covers part of solid, and stone where at least half is.</summary>
    [Fact]
    public void A_fill_in_the_air_gives_each_voxel_the_share_it_covers()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius);
        var edit = VoxelEdit.Sphere(new VoxelCoord(3, 304, -6), 27, VoxelEditOperation.Fill, GridShapesGenerator.Stone);

        world.Apply(edit);

        int partial = 0,
            solid = 0;
        ForEachVoxelNear(
            edit,
            0,
            (voxel, _) =>
            {
                var (covered, total) = Share(edit, voxel, Base);
                var expected = (byte)((VoxelDensity.Full * covered + total / 2) / total);
                var point = new VoxelCoord(voxel.X * Base, voxel.Y * Base, voxel.Z * Base);
                Assert.True(world.TryGetDensity(point, 0, out var density));
                Assert.Equal(expected, density);
                AssertMaterial(
                    world,
                    point,
                    expected >= VoxelDensity.Solid ? GridShapesGenerator.Stone : VoxelConstants.Air
                );
                partial += expected is > 0 and < VoxelDensity.Full ? 1 : 0;
                solid += expected >= VoxelDensity.Solid ? 1 : 0;
            }
        );
        Assert.True(partial > 100 && solid > 50, $"{partial} partial and {solid} solid voxels.");
    }

    /// <summary>
    /// A box on the 10 cm lines (a built cube) fills whole voxels only; a clear of the same box then counts every one
    /// of them, and a sphere's partly filled voxels count as what is solid in them, air ones in entry 0.
    /// </summary>
    [Fact]
    public void A_box_on_the_grids_lines_fills_whole_voxels_and_a_clear_counts_the_solid_in_each()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius);
        var box = VoxelEdit.Box(new VoxelCoord(-100, 200, 60), new VoxelCoord(50, 50, 50), VoxelEditOperation.Fill, 4);
        world.Apply(box);
        var counted = new long[256];
        world.CountRemoved(
            VoxelEdit.Box(new VoxelCoord(-100, 200, 60), new VoxelCoord(50, 50, 50), VoxelEditOperation.Clear),
            counted
        );
        Assert.Equal(50L * 50 * 50, counted[4]);
        for (var x = -110; x < -40; x += 10)
        {
            Assert.True(world.TryGetDensity(new VoxelCoord(x, 220, 80), 0, out var density));
            Assert.Equal(x is >= -100 and < -50 ? VoxelDensity.Full : VoxelDensity.Empty, density);
        }

        var ball = VoxelEdit.Sphere(new VoxelCoord(3, 304, -6), 27, VoxelEditOperation.Fill, GridShapesGenerator.Stone);
        world.Apply(ball);
        var around = VoxelEdit.Box(new VoxelCoord(-40, 260, -40), new VoxelCoord(90, 90, 90), VoxelEditOperation.Clear);
        long solid = 0,
            empty = 0;
        for (var z = -40; z < 50; z += 10)
        {
            for (var y = 260; y < 350; y += 10)
            {
                for (var x = -40; x < 50; x += 10)
                {
                    world.TryGetDensity(new VoxelCoord(x, y, z), 0, out var density);
                    world.TryGetMaterial(new VoxelCoord(x, y, z), out var material);
                    if (material == VoxelConstants.Air)
                    {
                        empty += density;
                    }
                    else
                    {
                        solid += density;
                    }
                }
            }
        }

        world.CountRemoved(around, counted);
        Assert.Equal((solid * 1000 + 127) / 255, counted[GridShapesGenerator.Stone]);
        Assert.Equal((empty * 1000 + 127) / 255, counted[VoxelConstants.Air]);
    }

    [Fact]
    public void An_edit_across_a_columns_edge_is_replayed_into_both_columns_when_they_load_again()
    {
        // A level-0 column of 10 cm voxels is 12.8 m wide: the sphere crosses x = 1280 cm, between columns 0 and 1.
        var near = new Vector3(12.8f, 2, 0.5f);
        using var world = TestWorlds.Load(_generator, near, radiusMetres: Radius);
        var left = new ColumnKey(0, 0, 0);
        var right = new ColumnKey(0, 1, 0);
        Assert.True(world.IsResident(left) && world.IsResident(right));
        var edit = VoxelEdit.Sphere(new VoxelCoord(1281, 120, 33), 23, VoxelEditOperation.Clear);

        world.Apply(edit);
        AssertEdited(world, edit);
        world.SetCenters([new Vector3(-300, 2, 0.5f)]);
        UpdateUntil(world, () => !world.IsResident(left) && !world.IsResident(right));
        TestWorlds.WaitUntilLoaded(world);
        world.SetCenters([near]);
        UpdateUntil(world, () => world.IsResident(left) && world.IsResident(right));
        TestWorlds.WaitUntilLoaded(world);

        AssertEdited(world, edit);
    }

    /// <summary>With levels 0 to 3, a sphere's clear takes from each level's voxels, of 10, 20, 40, and 80 cm, the share of each it covers.</summary>
    [Fact]
    public void A_sphere_clears_each_levels_voxels_by_the_share_it_covers()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius, levels: 4);
        var edit = VoxelEdit.Sphere(new VoxelCoord(227, 141, 417), 63, VoxelEditOperation.Clear);

        world.Apply(edit);

        for (var level = 0; level < 4; level++)
        {
            AssertEdited(world, edit, level);
        }
    }

    /// <summary>
    /// At 30 m from the centre the finest resident level is 2, so a count there reads its 40 cm voxels, each 64,000 cm³
    /// when whole.
    /// </summary>
    [Fact]
    public void A_count_past_level_0_counts_40_cm_voxels()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius, levels: 4);
        var edit = VoxelEdit.Sphere(new VoxelCoord(3000, 100, 50), 63, VoxelEditOperation.Clear);
        var expected = ExpectedRemoved(edit, 2);
        var counted = new long[256];

        Assert.Equal(2, world.CountRemoved(edit, counted));

        Assert.True(expected[GridShapesGenerator.Dirt] > 0);
        Assert.Equal(expected, counted);
    }

    /// <summary>
    /// A large edit (over 128³ voxels, a 12.8 m cube here) waits in the columns it covers and is applied nearest the centre
    /// first: at (8, 8) m that is column (0, 0), 2.3 m from its middle; column (1, 1)'s middle is 15.8 m away. Counted
    /// in 1.28 m columns, as at 1 cm, the order would be the other way round.
    /// </summary>
    [Fact]
    public void A_large_edit_applies_to_the_column_nearest_the_centre_first()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(8, 2, 8), radiusMetres: 20);
        // Through the dirt, across columns 0 and 1 on x and z: 240 x 40 x 240 voxels.
        var edit = VoxelEdit.Box(
            new VoxelCoord(100, -300, 100),
            new VoxelCoord(2400, 400, 2400),
            VoxelEditOperation.Clear
        );

        world.Apply(edit);
        AssertMaterial(world, new VoxelCoord(500, 85, 500), GridShapesGenerator.Dirt);
        world.ApplyPendingEdits(1);

        AssertMaterial(world, new VoxelCoord(500, 85, 500), VoxelConstants.Air);
        AssertMaterial(world, new VoxelCoord(2000, 85, 500), GridShapesGenerator.Dirt);
        AssertMaterial(world, new VoxelCoord(500, 85, 2000), GridShapesGenerator.Dirt);
        AssertMaterial(world, new VoxelCoord(2000, 85, 2000), GridShapesGenerator.Dirt);
        world.ApplyPendingEdits(int.MaxValue);
        AssertMaterial(world, new VoxelCoord(2000, 85, 2000), VoxelConstants.Air);
    }

    /// <summary>
    /// Levels 0 to 3, each level's radius twice the one below's; at 30 m from the centre level 2 (40 cm voxels) is the
    /// finest resident. A coarse ray down there
    /// meets the floor's top at 120 cm, the 40 cm line below 137, and names the level-0 voxel it entered and the 40 cm
    /// box it met.
    /// </summary>
    [Fact]
    public void A_coarse_ray_names_the_40_cm_voxel_it_met()
    {
        using var world = TestWorlds.Load(_generator, new Vector3(0, 2, 0), radiusMetres: Radius, levels: 4);
        Assert.True(world.IsResident(new ColumnKey(2, 0, 0)));
        Assert.False(world.IsResident(new ColumnKey(1, 1, 0)));
        var origin = new Vector3(30.03f, 10, 0.57f);

        Assert.False(world.Raycast(origin, -Vector3.UnitY, 20, out _));
        Assert.True(world.Raycast(origin, -Vector3.UnitY, 20, out var hit, coarse: true));

        Assert.Equal(2, hit.Level);
        Assert.Equal(new VoxelCoord(3000, 110, 50), hit.Voxel);
        Assert.Equal(new VoxelCoord(3000, 80, 40), hit.BoxMin);
        Assert.Equal(40, hit.BoxCentimetres);
        Assert.Equal(new VoxelCoord(0, 1, 0), hit.Normal);
        Assert.Equal(GridShapesGenerator.Dirt, hit.Material);
        Assert.Equal(8.8f, hit.DistanceMetres, 3);
    }

    /// <summary>
    /// The sky field's cells are the level nearest 64 cm: level 3, 80 cm, on this grid. Each cell in a resident column
    /// holds its voxel's material; the rest are air.
    /// </summary>
    [Fact]
    public void The_sky_field_reads_80_cm_cells()
    {
        const int level = 3;
        var centre = new Vector3(0, 2, 0);
        using var world = TestWorlds.Load(_generator, centre, radiusMetres: Radius, levels: level + 1);
        var field = new SkyField();

        field.Read(world, new WorldPoint(centre.X, centre.Y, centre.Z), coarsestLevel: level);

        Assert.Equal(level, field.CellLevel);
        Assert.Equal(0.8f, field.CellMetres, 6);
        var resident = new List<ColumnKey>();
        world.CollectResidentColumns(resident);
        var columns = resident.Where(key => key.Level == level).ToHashSet();
        var corner = new Vector3(
            (float)(field.Corner.X / field.CellMetres),
            (float)(field.Corner.Y / field.CellMetres),
            (float)(field.Corner.Z / field.CellMetres)
        );
        var (x0, y0, z0) = ((int)MathF.Round(corner.X), (int)MathF.Round(corner.Y), (int)MathF.Round(corner.Z));
        var solid = field.Solid;
        int stone = 0,
            dirt = 0;
        for (var z = 0; z < SkyField.SizeXZ; z++)
        {
            for (var x = 0; x < SkyField.SizeXZ; x++)
            {
                var isResident = columns.Contains(
                    new ColumnKey(level, (x0 + x) >> VoxelConstants.ChunkShift, (z0 + z) >> VoxelConstants.ChunkShift)
                );
                for (var y = 0; y < SkyField.SizeY; y++)
                {
                    var expected = isResident ? Expected(x0 + x, y0 + y, z0 + z, level) : VoxelConstants.Air;
                    var actual = solid[x + SkyField.SizeXZ * (y + SkyField.SizeY * z)];
                    if (actual != expected)
                    {
                        Assert.Fail($"Cell ({x}, {y}, {z}): {actual}, expected {expected}.");
                    }

                    stone += actual == GridShapesGenerator.Stone ? 1 : 0;
                    dirt += actual == GridShapesGenerator.Dirt ? 1 : 0;
                }
            }
        }

        Assert.True(stone > 0 && dirt > 0, $"{stone} stone and {dirt} dirt cells.");
    }

    /// <summary>The level-0 voxel that holds centimetre <paramref name="centimetres"/>.</summary>
    private static int Voxel(int centimetres) => (int)Math.Floor(centimetres / (double)Base);

    /// <summary>What the level-<paramref name="level"/> voxel of these indices holds: the shapes at its centre, rounded down.</summary>
    private static byte Expected(int x, int y, int z, int level)
    {
        var edge = Base << level;
        return GridShapesGenerator.Material(x * edge + edge / 2, y * edge + edge / 2, z * edge + edge / 2);
    }

    private static void AssertMaterial(VoxelWorld world, VoxelCoord point, byte expected)
    {
        Assert.True(world.TryGetMaterial(point, out var material));
        Assert.Equal(expected, material);
    }

    /// <summary>
    /// Every voxel of <paramref name="level"/> near a clear holds what the shapes give it less the share of it the edit
    /// covers (<see cref="Share"/>): a density of 255 for a solid voxel less that share of it, rounded, and air below one
    /// half; with <paramref name="turnsToAir"/>, some solid voxel turns to air.
    /// </summary>
    private static void AssertEdited(VoxelWorld world, VoxelEdit edit, int level = 0, bool turnsToAir = true)
    {
        var edge = Base << level;
        int cleared = 0,
            partial = 0;
        ForEachVoxelNear(
            edit,
            level,
            (voxel, sample) =>
            {
                var (material, density) = Cleared(
                    edit,
                    voxel,
                    edge,
                    GridShapesGenerator.Material(sample.X, sample.Y, sample.Z)
                );
                var point = new VoxelCoord(voxel.X * edge, voxel.Y * edge, voxel.Z * edge);
                if (
                    !world.TryGetMaterial(point, level, out var actual)
                    || actual != material
                    || !world.TryGetDensity(point, level, out var actualDensity)
                    || actualDensity != density
                )
                {
                    world.TryGetDensity(point, level, out var found);
                    Assert.Fail(
                        $"Level {level}, voxel at {point} cm: {actual} at {found}, expected {material} at {density}."
                    );
                }

                cleared +=
                    material == VoxelConstants.Air
                    && GridShapesGenerator.Material(sample.X, sample.Y, sample.Z) != VoxelConstants.Air
                        ? 1
                        : 0;
                partial += density is > 0 and < VoxelDensity.Full ? 1 : 0;
            }
        );
        Assert.True(!turnsToAir || cleared > 0, $"The edit made no voxel air at level {level}.");
        Assert.True(partial > 0, $"The edit took part of no voxel at level {level}.");
    }

    /// <summary>
    /// A voxel of <paramref name="shapes"/> at full density, or air, after a clear of the share of it
    /// <paramref name="edit"/> covers: its material and density.
    /// </summary>
    private static (byte Material, byte Density) Cleared(VoxelEdit edit, VoxelCoord voxel, int edge, byte shapes)
    {
        var (covered, total) = Share(edit, voxel, edge);
        var density = (int)VoxelDensity.Default(shapes);
        density -= (density * covered + total / 2) / total;
        return (density < VoxelDensity.Solid ? VoxelConstants.Air : shapes, (byte)density);
    }

    /// <summary>
    /// What a clear takes from the voxels of <paramref name="level"/> near it, by material, in cubic centimetres: each
    /// voxel's lost density times its volume, over 255, rounded for each material.
    /// </summary>
    private static long[] ExpectedRemoved(VoxelEdit edit, int level)
    {
        var edge = Base << level;
        var steps = new long[256];
        ForEachVoxelNear(
            edit,
            level,
            (voxel, sample) =>
            {
                var shapes = GridShapesGenerator.Material(sample.X, sample.Y, sample.Z);
                var (_, density) = Cleared(edit, voxel, edge, shapes);
                steps[shapes] += (VoxelDensity.Default(shapes) - density) * (long)edge * edge * edge;
            }
        );
        var expected = new long[256];
        for (var material = 1; material < 256; material++)
        {
            expected[material] = (steps[material] + 127) / 255;
        }

        return expected;
    }

    /// <summary>
    /// The parts of the voxel <paramref name="edge"/> centimetres across at these indices that an edit covers, found
    /// point by point: on up to 10 sub-rows along y and z, at the middle of each one's share of the edge, every
    /// centimetre along x. Returns those covered and all of them.
    /// </summary>
    private static (int Covered, int Total) Share(VoxelEdit edit, VoxelCoord voxel, int edge)
    {
        var rows = Math.Min(edge, 10);
        var covered = 0;
        for (var k = 0; k < rows; k++)
        {
            var z = voxel.Z * edge + (2 * k + 1) * edge / (2 * rows);
            for (var j = 0; j < rows; j++)
            {
                var y = voxel.Y * edge + (2 * j + 1) * edge / (2 * rows);
                for (var x = voxel.X * edge; x < voxel.X * edge + edge; x++)
                {
                    covered += edit.Contains(x, y, z) ? 1 : 0;
                }
            }
        }

        return (covered, rows * rows * edge);
    }

    /// <summary>Calls <paramref name="visit"/> with each level-0 voxel within two voxels of the edit's bounds and its sample point.</summary>
    private static void ForEachVoxelNear(VoxelEdit edit, int level, Action<VoxelCoord, VoxelCoord> visit)
    {
        var edge = Base << level;
        var half = edge / 2;
        edit.GetBounds(out var min, out var end);
        for (var z = VoxelAt(min.Z, edge) - 2; z <= VoxelAt(end.Z, edge) + 2; z++)
        {
            for (var y = VoxelAt(min.Y, edge) - 2; y <= VoxelAt(end.Y, edge) + 2; y++)
            {
                for (var x = VoxelAt(min.X, edge) - 2; x <= VoxelAt(end.X, edge) + 2; x++)
                {
                    visit(new VoxelCoord(x, y, z), new VoxelCoord(x * edge + half, y * edge + half, z * edge + half));
                }
            }
        }
    }

    /// <summary>The voxel <paramref name="edge"/> centimetres across that holds centimetre <paramref name="centimetres"/>.</summary>
    private static int VoxelAt(int centimetres, int edge) => (int)Math.Floor(centimetres / (double)edge);

    /// <summary>
    /// A walk through 10 cm voxels (Amanatides and Woo) reading the shapes at each voxel's sample point: the first solid
    /// voxel's low corner in centimetres, the face it entered through, its material, and the distance to it in metres.
    /// </summary>
    private static bool Walk(
        Vector3 originMetres,
        Vector3 direction,
        float maxMetres,
        out VoxelCoord hit,
        out VoxelCoord normal,
        out byte material,
        out float distanceMetres
    )
    {
        var d = Vector3.Normalize(direction);
        const double perVoxel = 100.0 / Base;
        double[] origin = [originMetres.X * perVoxel, originMetres.Y * perVoxel, originMetres.Z * perVoxel];
        double[] dir = [d.X, d.Y, d.Z];
        var voxel = new int[3];
        var tMax = new double[3];
        var tDelta = new double[3];
        for (var a = 0; a < 3; a++)
        {
            voxel[a] = (int)Math.Floor(origin[a]);
            var boundary = voxel[a] + (dir[a] >= 0 ? 1 : 0);
            tMax[a] = dir[a] == 0 ? double.PositiveInfinity : (boundary - origin[a]) / dir[a];
            tDelta[a] = dir[a] == 0 ? double.PositiveInfinity : 1 / Math.Abs(dir[a]);
        }

        double t = 0;
        var axis = -1;
        while (t <= maxMetres * perVoxel)
        {
            material = Expected(voxel[0], voxel[1], voxel[2], level: 0);
            if (material != VoxelConstants.Air)
            {
                hit = new VoxelCoord(voxel[0] * Base, voxel[1] * Base, voxel[2] * Base);
                normal = axis switch
                {
                    0 => new VoxelCoord(dir[0] > 0 ? -1 : 1, 0, 0),
                    1 => new VoxelCoord(0, dir[1] > 0 ? -1 : 1, 0),
                    2 => new VoxelCoord(0, 0, dir[2] > 0 ? -1 : 1),
                    _ => default,
                };
                distanceMetres = (float)(t / perVoxel);
                return true;
            }

            axis =
                tMax[0] < tMax[1] ? (tMax[0] < tMax[2] ? 0 : 2)
                : tMax[1] < tMax[2] ? 1
                : 2;
            t = tMax[axis];
            tMax[axis] += tDelta[axis];
            voxel[axis] += dir[axis] >= 0 ? 1 : -1;
        }

        hit = default;
        normal = default;
        material = VoxelConstants.Air;
        distanceMetres = 0;
        return false;
    }

    /// <summary>Updates <paramref name="world"/> until <paramref name="done"/> holds, sleeping 1 ms between updates. Fails after 60 s.</summary>
    private static void UpdateUntil(VoxelWorld world, Func<bool> done)
    {
        var deadline = TestTime.Deadline(60);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The world did not reach the state.");
            world.Update(0);
            Thread.Sleep(1);
        }
    }
}
