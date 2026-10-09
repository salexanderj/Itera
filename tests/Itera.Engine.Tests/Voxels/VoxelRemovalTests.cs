using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// What players' clears remove (<see cref="VoxelWorld.CountRemoved"/>, <see cref="IVoxelRemovals"/>) and who made each
/// edit (<see cref="VoxelWorld.MakerOf"/>): the count matches a voxel-by-voxel walk of the generator at level 0 and at a
/// coarse level, counts only what large edits still waiting left, gives a shape too small for a coarse voxel its volume,
/// and the hook hears each made clear once, before it changes anything, and never a replay, an edit without a maker, or
/// a loaded log. A clear of some materials (<see cref="VoxelEditOperation.ClearMaterials"/>) counts and removes only
/// those, at level 0 and past it.
/// </summary>
public sealed class VoxelRemovalTests : IDisposable
{
    private const int Floor = 200;
    private const byte Wood = 5;

    private static readonly BoxesGenerator s_generator = new(
        Floor,
        new VoxelBox(-60, Floor, -40, 40, Floor + 90, 50, Wood),
        new VoxelBox(50, Floor - 30, -60, 120, Floor + 40, 10, BoxesGenerator.Pattern),
        new VoxelBox(-120, Floor - 50, 30, -70, Floor - 10, 90, VoxelConstants.Air),
        new VoxelBox(560, Floor, -40, 640, Floor + 90, 50, Wood),
        new VoxelBox(650, Floor - 30, -60, 720, Floor + 40, 10, BoxesGenerator.Pattern)
    );

    private readonly List<VoxelWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    public static TheoryData<string> Shapes => ["sphere", "capsule", "box", "pit", "large"];

    public static TheoryData<string> CoarseShapes => ["sphere", "capsule", "box"];

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_count_at_level_0_is_what_a_voxel_walk_of_the_generator_finds(string shape)
    {
        var world = Load(new Vector3(0, 2, 0), levels: 1);
        var edit = Shape(shape, default);
        var counts = new long[256];

        var level = world.CountRemoved(in edit, counts);

        Assert.Equal(0, level);
        Assert.Equal(Walk(in edit, 0), counts);
        Assert.True(counts.Sum() > 0);
    }

    [Theory]
    [MemberData(nameof(CoarseShapes))]
    public void Past_level_0_the_count_is_the_coarse_voxels_each_as_8_to_the_level(string shape)
    {
        // Level 0 reaches 4 m and level 1 8 m: the shapes 5.5 to 7 m out lie in level 1 only.
        var world = Load(new Vector3(0, 2, 0), levels: 2, radiusMetres: 4);
        var edit = Shape(shape, new VoxelCoord(600, 0, 0));
        var counts = new long[256];

        var level = world.CountRemoved(in edit, counts);

        Assert.Equal(1, level);
        Assert.Equal(Walk(in edit, 1), counts);
        Assert.True(counts.Sum() > 0);
    }

    /// <summary>
    /// A sphere of radius 1 holds 7 whole centimetres; at level 1 they fall in several 2 cm voxels, and each loses the
    /// share of it they cover, so the count is about the shape's own volume.
    /// </summary>
    [Fact]
    public void A_shape_smaller_than_a_coarse_voxel_counts_the_share_of_each_voxel_it_covers()
    {
        var world = Load(new Vector3(0, 2, 0), levels: 2, radiusMetres: 4);
        var edit = VoxelEdit.Sphere(new VoxelCoord(600, Floor - 40, 0), 1, VoxelEditOperation.Clear);
        var counts = new long[256];

        Assert.Equal(1, world.CountRemoved(in edit, counts));

        Assert.Equal(Walk(in edit, 1), counts);
        Assert.InRange(counts[BoxesGenerator.Dirt], 6, 8);
    }

    /// <summary>
    /// A coarse box whose bottom centimetre lies in the solid floor counts that slab, 8 x 8 x 1 cm, though its 4 cm
    /// voxels' sample points under it are solid and those inside it air: each voxel loses the share the box covers.
    /// </summary>
    [Fact]
    public void A_coarse_box_counts_the_share_of_the_solid_it_covers()
    {
        // Level 0 reaches 4 m, level 1 8 m, level 2 16 m: at 12 m the box lies in level 2 only.
        var world = Load(new Vector3(0, 2, 0), levels: 3, radiusMetres: 4);
        var edit = VoxelEdit.Box(new VoxelCoord(1200, Floor - 1, 0), new VoxelCoord(8, 7, 8), VoxelEditOperation.Clear);
        var counts = new long[256];

        Assert.Equal(2, world.CountRemoved(in edit, counts));

        Assert.Equal(Walk(in edit, 2), counts);
        Assert.Equal(8 * 8, counts.Sum());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_clear_of_some_materials_counts_and_removes_only_those(int level)
    {
        // Level 0 reaches 4 m and level 1 8 m: at 6 m the box lies in level 1 only.
        var world = Load(new Vector3(0, 2, 0), levels: 2, radiusMetres: 4);
        var at = level == 0 ? default : new VoxelCoord(600, 0, 0);
        var box = Shape("box", at);
        byte[] set = [Wood, BoxesGenerator.Dirt, BoxesGenerator.Leaves, 100];
        var clear = VoxelEdit.MaterialsBox(box.Origin, box.Extent, set);
        var all = Walk(in box, level);
        var counts = new long[256];

        Assert.Equal(level, world.CountRemoved(in clear, counts));

        for (var material = 0; material < 256; material++)
        {
            Assert.Equal(Array.IndexOf(set, (byte)material) >= 0 ? all[material] : 0, counts[material]);
        }

        Assert.True(counts[Wood] > 0 && counts[BoxesGenerator.Dirt] > 0 && counts[BoxesGenerator.Leaves] > 0);
        Assert.True(all.Sum() > counts.Sum());

        // Applied, the set's voxels are air and every other one is as it was.
        world.Apply(in clear);
        world.ApplyPendingEdits(int.MaxValue);
        box.GetBounds(out var min, out var end);
        for (var z = (min.Z >> level) - 1; z <= (end.Z >> level) + 1; z++)
        {
            for (var y = (min.Y >> level) - 1; y <= (end.Y >> level) + 1; y++)
            {
                for (var x = (min.X >> level) - 1; x <= (end.X >> level) + 1; x++)
                {
                    var sample = new VoxelCoord(
                        VoxelConstants.SampleAt(x, level),
                        VoxelConstants.SampleAt(y, level),
                        VoxelConstants.SampleAt(z, level)
                    );
                    var generated = s_generator.Material(sample.X, sample.Y, sample.Z);
                    var expected =
                        clear.Contains(sample.X, sample.Y, sample.Z) && clear.ClearsMaterial(generated)
                            ? VoxelConstants.Air
                            : generated;
                    Assert.True(world.TryGetMaterial(sample, level, out var now));
                    Assert.Equal(expected, now);
                }
            }
        }
    }

    [Fact]
    public void The_hook_hears_a_made_clear_of_some_materials_with_only_those()
    {
        var world = Load(new Vector3(0, 2, 0), levels: 1);
        var heard = new Heard(world);
        world.Removals = heard;
        var box = Shape("box", default);
        var clear = VoxelEdit.MaterialsBox(box.Origin, box.Extent, [Wood]);

        world.Apply(clear, maker: 4);

        Assert.Equal(1, heard.Calls);
        Assert.Equal(4, heard.Makers[0]);
        Assert.Equal(Walk(in box, 0)[Wood], heard.Totals[0]);
        Assert.Equal(heard.Totals[0], heard.WoodCounts[0]);
    }

    [Fact]
    public void A_large_edit_still_waiting_there_is_applied_first_so_only_what_it_left_counts()
    {
        var world = Load(new Vector3(0, 2, 0), levels: 1);
        var centre = new VoxelCoord(0, Floor + 20, 0);
        world.Apply(VoxelEdit.Sphere(centre, 140, VoxelEditOperation.Clear));
        Assert.True(world.PendingEditColumns > 0);
        var counts = new long[256];

        world.CountRemoved(VoxelEdit.Sphere(centre, 60, VoxelEditOperation.Clear), counts);

        Assert.Equal(0, counts.Sum());
        world.TryGetMaterial(centre, out var dug);
        Assert.Equal(VoxelConstants.Air, dug);
    }

    [Fact]
    public void Nothing_counts_where_no_level_is_resident()
    {
        var world = Load(new Vector3(0, 2, 0), levels: 1);
        var counts = new long[256];
        counts[3] = 9;

        Assert.Equal(
            -1,
            world.CountRemoved(VoxelEdit.Sphere(new VoxelCoord(5000, Floor, 0), 20, VoxelEditOperation.Clear), counts)
        );

        Assert.Equal(0, counts.Sum());
    }

    [Fact]
    public void The_hook_hears_each_made_clear_once_before_it_changes_anything()
    {
        var world = Load(new Vector3(0, 2, 0), levels: 1);
        var heard = new Heard(world);
        world.Removals = heard;
        var dig = VoxelEdit.Sphere(new VoxelCoord(0, Floor + 10, 0), 30, VoxelEditOperation.Clear);
        var expected = Walk(in dig, 0);

        var sequence = world.Apply(dig, maker: 3);
        world.Apply(dig, maker: 3);
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(-30, Floor + 10, 0), 30, VoxelEditOperation.Clear));
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(30, Floor + 10, 0), 30, VoxelEditOperation.Set, 3), maker: 2);

        Assert.Equal(2, heard.Calls);
        Assert.Equal([sequence, sequence + 1], heard.Sequences);
        Assert.Equal([3, 3], heard.Makers);
        Assert.Equal(expected.Sum(), heard.Totals[0]);
        Assert.Equal(expected[Wood], heard.WoodCounts[0]);
        // The second dig is in the same place: the first already took it all.
        Assert.Equal(0, heard.Totals[1]);
        // Heard before the log grew: the edit was not in it yet.
        Assert.Equal([sequence - 1, sequence], heard.LogCounts);
        Assert.Equal(3, world.MakerOf(sequence));
        Assert.Equal(VoxelEdit.NoMaker, world.MakerOf(sequence + 2));
        Assert.Equal(2, world.MakerOf(sequence + 3));
    }

    [Fact]
    public void Replays_columns_loaded_again_and_a_loaded_log_are_never_heard()
    {
        var world = Load(new Vector3(0, 2, 0), levels: 1);
        var heard = new Heard(world);
        world.Removals = heard;
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, Floor + 10, 0), 150, VoxelEditOperation.Clear), maker: 0);
        world.ApplyPendingEdits(int.MaxValue);
        world.SetCenters([new Vector3(50, 2, 50)]);
        TestWorlds.WaitUntilLoaded(world);
        world.SetCenters([new Vector3(0, 2, 0)]);
        TestWorlds.WaitUntilLoaded(world);
        using var save = new MemoryStream();
        world.SaveEdits(save, 60, []);

        var loaded = Load(new Vector3(0, 2, 0), levels: 1);
        var loadedHeard = new Heard(loaded);
        loaded.Removals = loadedHeard;
        save.Position = 0;
        loaded.LoadEdits(save);

        Assert.Equal(1, heard.Calls);
        Assert.Equal(0, loadedHeard.Calls);
        Assert.Equal(VoxelEdit.NoMaker, loaded.MakerOf(1));
    }

    [Fact]
    public void A_maker_outside_the_slots_is_refused_and_changes_nothing()
    {
        var world = Load(new Vector3(0, 2, 0), levels: 1);
        var dig = VoxelEdit.Sphere(new VoxelCoord(0, Floor + 10, 0), 10, VoxelEditOperation.Clear);

        Assert.Throws<ArgumentOutOfRangeException>(() => world.Apply(dig, maker: 255));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.Apply(dig, maker: -2));

        Assert.Equal(0, world.EditCount);
    }

    [Fact]
    public void A_made_clear_with_a_hook_allocates_nothing()
    {
        var world = Load(new Vector3(0, 2, 0), levels: 1);
        var heard = new Heard(world, keep: false);
        world.Removals = heard;
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, Floor + 10, 0), 30, VoxelEditOperation.Clear), maker: 1);
        world.Apply(
            VoxelEdit.Capsule(new(-50, Floor, 0), new(50, Floor + 20, 0), 6, VoxelEditOperation.Clear),
            maker: 1
        );

        var start = SystemAllocation.StartMeasurement();
        world.Apply(VoxelEdit.Sphere(new VoxelCoord(10, Floor + 30, 5), 30, VoxelEditOperation.Clear), maker: 1);
        world.Apply(
            VoxelEdit.Capsule(new(-40, Floor + 5, 8), new(60, Floor + 25, 8), 6, VoxelEditOperation.Clear),
            maker: 2
        );

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
        Assert.Equal(4, heard.Calls);
    }

    /// <summary>A shape round <paramref name="at"/> (a level-0 voxel at the floor's height), through wood, dirt, the mix, and a pit.</summary>
    private static VoxelEdit Shape(string shape, VoxelCoord at)
    {
        var centre = new VoxelCoord(at.X, Floor, at.Z);
        return shape switch
        {
            "sphere" => VoxelEdit.Sphere(centre, 45, VoxelEditOperation.Clear),
            "capsule" => VoxelEdit.Capsule(
                centre with
                {
                    X = centre.X - 40,
                    Y = Floor + 30,
                },
                centre with
                {
                    X = centre.X + 90,
                    Y = Floor - 25,
                    Z = centre.Z - 30,
                },
                9,
                VoxelEditOperation.Clear
            ),
            "box" => VoxelEdit.Box(
                new VoxelCoord(centre.X + 20, Floor - 40, centre.Z - 50),
                new VoxelCoord(70, 90, 40),
                VoxelEditOperation.Clear
            ),
            // Whole chunks and 32-voxel cells of one material, counted at once.
            "large" => VoxelEdit.Box(
                new VoxelCoord(centre.X - 150, Floor - 130, centre.Z - 150),
                new VoxelCoord(300, 260, 300),
                VoxelEditOperation.Clear
            ),
            _ => VoxelEdit.Sphere(
                centre with
                {
                    X = centre.X - 95,
                    Y = Floor - 30,
                    Z = 60,
                },
                35,
                VoxelEditOperation.Clear
            ),
        };
    }

    /// <summary>
    /// What a clear takes from the solid voxels of <paramref name="level"/> (2^level cm on this grid), by material, in
    /// cubic centimetres: each voxel, at full density, loses the share of it the edit covers, found point by point (every
    /// centimetre, as these voxels are at most 8 cm), times its volume; the sum over 255, rounded for each material. Read
    /// from the generator one voxel at a time.
    /// </summary>
    private static long[] Walk(in VoxelEdit edit, int level)
    {
        var steps = new long[256];
        var edge = 1 << level;
        edit.GetBounds(out var min, out var end);
        for (var z = (min.Z >> level) - 1; z <= (end.Z >> level) + 1; z++)
        {
            for (var y = (min.Y >> level) - 1; y <= (end.Y >> level) + 1; y++)
            {
                for (var x = (min.X >> level) - 1; x <= (end.X >> level) + 1; x++)
                {
                    var material = s_generator.Material(
                        VoxelConstants.SampleAt(x, level),
                        VoxelConstants.SampleAt(y, level),
                        VoxelConstants.SampleAt(z, level)
                    );
                    if (material == VoxelConstants.Air)
                    {
                        continue;
                    }

                    var covered = 0;
                    for (var k = 0; k < edge; k++)
                    {
                        for (var j = 0; j < edge; j++)
                        {
                            for (var i = 0; i < edge; i++)
                            {
                                covered += edit.Contains((x << level) + i, (y << level) + j, (z << level) + k) ? 1 : 0;
                            }
                        }
                    }

                    var total = edge * edge * edge;
                    steps[material] += (VoxelDensity.Full * covered + total / 2) / total * (long)total;
                }
            }
        }

        var counts = new long[256];
        for (var material = 1; material < 256; material++)
        {
            counts[material] = (steps[material] + 127) / 255;
        }

        return counts;
    }

    private VoxelWorld Load(Vector3 centre, int levels, double radiusMetres = 3)
    {
        var world = TestWorlds.Load(s_generator, centre, radiusMetres, levels: levels);
        _worlds.Add(world);
        return world;
    }

    /// <summary>Keeps what the hook heard; with <c>keep</c> false only counts calls, allocating nothing.</summary>
    private sealed class Heard(VoxelWorld world, bool keep = true) : IVoxelRemovals
    {
        public int Calls { get; private set; }

        public List<long> Sequences { get; } = [];

        public List<int> Makers { get; } = [];

        public List<long> Totals { get; } = [];

        public List<long> WoodCounts { get; } = [];

        public List<long> LogCounts { get; } = [];

        public void Removed(in VoxelEdit edit, int maker, ReadOnlySpan<long> byMaterial)
        {
            Calls++;
            if (!keep)
            {
                return;
            }

            var total = 0L;
            foreach (var count in byMaterial)
            {
                total += count;
            }

            Sequences.Add(edit.Sequence);
            Makers.Add(maker);
            Totals.Add(total);
            WoodCounts.Add(byMaterial[Wood]);
            LogCounts.Add(world.EditCount);
        }
    }
}
