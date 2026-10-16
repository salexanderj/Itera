using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// What the world's edits do to the voxel objects (<see cref="ObjectDamage"/>): a turned box takes the voxels it holds
/// wholly and leaves those wholly outside it; damage done while an object is loaded equals damage replayed when its area
/// fills, bit for bit; a reset heals every object; past the cap the farthest copy goes and comes back by replay near;
/// an area's copies go with it and come back; a clear with a maker counts what it takes from objects; a paint
/// recolours and a fill changes nothing; damaged parts' models are their voxels at three levels, a part's border with
/// them; and a second dig allocates nothing.
/// </summary>
public sealed class ObjectDamageTests : IDisposable
{
    private const byte Bark = 30;
    private const byte Leaves = 31;
    private static readonly int[] s_copiedLevels = [2, 3];
    private const byte Rock = 40;
    private readonly List<Scene> _scenes = [];

    public void Dispose()
    {
        foreach (var scene in _scenes)
        {
            scene.Dispose();
        }
    }

    /// <summary>
    /// A box cleared across the rock turned 700 steps: each voxel whose cube, turned as the rock is drawn, lies wholly
    /// inside the box (with a centimetre to spare) is air of no density; each wholly outside it is the kind's.
    /// </summary>
    [Fact]
    public void A_turned_box_takes_the_voxels_it_holds_and_leaves_those_outside()
    {
        var scene = Make();
        var box = VoxelEdit.Box(new VoxelCoord(-340, 10, 250), new VoxelCoord(60, 100, 120), VoxelEditOperation.Clear);

        scene.World.Apply(box);

        var rock = scene.Placements[1];
        var damaged = scene.Damage.Find(rock.Id);
        Assert.NotNull(damaged);
        var kind = scene.Kinds[rock.Kind];
        var turn = ObjectTurns.Rotation(rock.Heading);
        int whole = 0,
            kept = 0;
        for (var z = 0; z < kind.Size.Z; z++)
        {
            for (var y = 0; y < kind.Size.Y; y++)
            {
                for (var x = 0; x < kind.Size.X; x++)
                {
                    var (inside, outside) = Corners(rock, kind, turn, x, y, z, box);
                    damaged.Voxel(x, y, z, out var material, out var density);
                    kind.Voxel(x, y, z, out var was, out var wasDensity);
                    if (inside)
                    {
                        Assert.Equal((VoxelConstants.Air, VoxelDensity.Empty), (material, density));
                        whole += was != VoxelConstants.Air ? 1 : 0;
                    }
                    else if (outside)
                    {
                        Assert.Equal((was, wasDensity), (material, density));
                        kept += was != VoxelConstants.Air ? 1 : 0;
                    }
                }
            }
        }

        Assert.True(whole > 100 && kept > 100, $"{whole} voxels taken wholly, {kept} kept.");
    }

    /// <summary>
    /// The same edits made while the objects are loaded, and made before (their areas filled after, the edits replayed),
    /// leave every damaged object's voxels the same, bit for bit.
    /// </summary>
    [Fact]
    public void Damage_made_live_equals_damage_replayed_when_the_area_fills()
    {
        var live = Make();
        var replayed = Make(fill: false);
        foreach (var edit in Edits())
        {
            live.World.Apply(edit);
            replayed.World.Apply(edit);
        }

        replayed.Fill();

        Assert.Equal(3, live.Damage.Count);
        Assert.Equal(live.Damage.Count, replayed.Damage.Count);
        foreach (var placement in live.Placements)
        {
            var expected = live.Damage.Find(placement.Id);
            if (expected is null)
            {
                // The wall: no edit reaches it.
                Assert.Null(replayed.Damage.Find(placement.Id));
                continue;
            }

            AssertSame(expected, replayed.Damage.Find(placement.Id));
        }
    }

    /// <summary>
    /// A dig, a reset, and another dig: live, the reset heals the first and the second stays; replayed after the
    /// objects' areas fill again, only the second counts. The two are the same, bit for bit.
    /// </summary>
    [Fact]
    public void Damage_after_a_reset_equals_its_replay()
    {
        var live = Make();
        var replayed = Make(fill: false);
        var edits = Edits().ToArray();
        foreach (var scene in new[] { live, replayed })
        {
            scene.World.Apply(edits[0]);
            scene.World.Reset();
            scene.World.Apply(edits[1]);
        }

        Assert.Equal(1, live.Damage.Count);
        TestWorlds.WaitUntilLoaded(live.World);
        live.Fill();
        TestWorlds.WaitUntilLoaded(replayed.World);
        replayed.Fill();

        var rock = live.Placements[1];
        Assert.Equal(1, replayed.Damage.Count);
        Assert.Null(replayed.Damage.Find(live.Placements[0].Id));
        AssertSame(live.Damage.Find(rock.Id), replayed.Damage.Find(rock.Id));
    }

    /// <summary>
    /// A wall standing over the map's edge (its middle 1 m inside it): a dig wholly past the edge, which the log indexes
    /// in no column and no replay finds, changes it neither live nor replayed.
    /// </summary>
    [Fact]
    public void A_dig_past_the_maps_edge_changes_no_object()
    {
        var live = Make(atEdge: true);
        var replayed = Make(fill: false, atEdge: true);
        var past = VoxelEdit.Box(new VoxelCoord(51210, 0, -60), new VoxelCoord(60, 60, 120), VoxelEditOperation.Clear);

        live.World.Apply(past);
        replayed.World.Apply(past);
        replayed.Fill();

        Assert.Equal(0, live.Damage.Count);
        Assert.Equal(0, replayed.Damage.Count);
    }

    /// <summary>
    /// The damage the edits do, pinned: a change to the turn, the share, or what changes objects changes it on every peer,
    /// which <see cref="ObjectEditing.RuleVersion"/> must then mark.
    /// </summary>
    [Theory]
    [InlineData(false, "A3315972C4ABFE2E")]
    [InlineData(true, "8AD4AF99D93E8B1E")]
    public void The_damage_is_pinned(bool withRemoval, string pinned)
    {
        var scene = Make();
        foreach (var edit in Edits(withRemoval))
        {
            scene.World.Apply(edit);
        }

        var all = string.Concat(
            scene.Placements.Select(p => scene.Damage.Find(p.Id) is { } damaged ? Snapshot(damaged) : "-")
        );

        Assert.Equal(2, ObjectEditing.RuleVersion);
        Assert.Equal(
            pinned,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(all)))[
                ..16
            ]
        );
    }

    /// <summary>
    /// A part's removal whose box lies over the object's top, its seed in the object, changes it neither live nor
    /// replayed; one whose box lies over the ground under no object leaves the world's voxels as they were, also once
    /// its columns load again.
    /// </summary>
    [Fact]
    public void A_part_removal_is_bound_by_its_box_and_never_changes_the_world()
    {
        var live = Make();
        var replayed = Make(fill: false);
        var above = VoxelEdit.RemovePart(
            1,
            new VoxelCoord(6, 50, 6),
            new VoxelCoord(-25, 1000, -25),
            new VoxelCoord(50, 50, 50)
        );
        live.World.Apply(above);
        replayed.World.Apply(above);
        replayed.Fill();
        Assert.Equal((0, 0), (live.Damage.Count, replayed.Damage.Count));

        var ground = VoxelEdit.RemovePart(
            1,
            new VoxelCoord(6, 50, 6),
            new VoxelCoord(-500, -100, -500),
            new VoxelCoord(300, 100, 300)
        );
        live.World.Apply(ground);
        AssertGround(live.World);
        live.World.SetCenters([new Vector3(400, 0, 400)]);
        TestWorlds.WaitUntilLoaded(live.World);
        live.World.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(live.World);
        AssertGround(live.World);

        static void AssertGround(VoxelWorld world)
        {
            foreach (
                var at in new[] { new Vector3(-4.5f, 1, -4.5f), new Vector3(-3, 1, -3), new Vector3(-2.1f, 1, -2.1f) }
            )
            {
                Assert.True(world.Raycast(at, -Vector3.UnitY, 5, out var hit));
                Assert.Equal(1, hit.DistanceMetres, 1e-3f);
            }
        }
    }

    /// <summary>
    /// A mover's chunks read before more changes than the damage keeps (300 paints) are all read again: its columns are a
    /// fresh mover's.
    /// </summary>
    [Fact]
    public void A_reader_far_behind_the_changes_reads_every_object_again()
    {
        var scene = Make();
        var passability = new NothingPassable();
        var behind = new Itera.Engine.Physics.ObjectColumns(scene.Objects, scene.Kinds, passability, 10, scene.Damage);
        var tops = new int[40 * 40];
        behind.ColumnTops(-5, -20, 40, 40, -5, 40, tops);

        scene.World.Apply(VoxelEdit.Sphere(new VoxelCoord(150, 300, 0), 40, VoxelEditOperation.Clear));
        for (var i = 0; i < ObjectDamage.KeptChanges + 44; i++)
        {
            scene.World.Apply(
                VoxelEdit.Sphere(new VoxelCoord(150, 100, 0), 6, VoxelEditOperation.Paint, i % 2 == 0 ? Rock : Bark)
            );
        }

        var fresh = new int[40 * 40];
        behind.ColumnTops(-5, -20, 40, 40, -5, 40, tops);
        new Itera.Engine.Physics.ObjectColumns(scene.Objects, scene.Kinds, passability, 10, scene.Damage).ColumnTops(
            -5,
            -20,
            40,
            40,
            -5,
            40,
            fresh
        );
        Assert.True(scene.Damage.Version > ObjectDamage.KeptChanges);
        Assert.Equal(fresh, tops);
        Assert.Contains(fresh, top => top != int.MinValue && top < 30);
    }

    /// <summary>
    /// A sphere clearing the ground's stone, reaching the trunk of bark beside it, digs the ground and makes no copy and
    /// names no change; one clearing stone and bark takes from the trunk the solid voxels a plain clear does (the plain
    /// clear also thins the air voxels' densities, which a clear of some materials leaves).
    /// </summary>
    [Fact]
    public void A_sphere_clearing_some_materials_leaves_objects_of_others_whole()
    {
        var stoneOnly = Make();
        var both = Make();
        var plain = Make();
        var centre = new VoxelCoord(60, 0, 0);

        stoneOnly.World.Apply(VoxelEdit.MaterialsSphere(centre, 120, [Systems.FlatTestGenerator.Stone]));
        both.World.Apply(VoxelEdit.MaterialsSphere(centre, 120, [Systems.FlatTestGenerator.Stone, Bark]));
        plain.World.Apply(VoxelEdit.Sphere(centre, 120, VoxelEditOperation.Clear));

        var trunk = stoneOnly.Placements[0];
        Assert.Equal((0, 0L), (stoneOnly.Damage.Count, stoneOnly.Damage.Version));
        Assert.True(stoneOnly.World.Raycast(new Vector3(0.6f, 1, 0), -Vector3.UnitY, 5, out var hit));
        Assert.Equal(2.2f, hit.DistanceMetres, 0.05f);
        var expected = plain.Damage.Find(trunk.Id);
        var actual = both.Damage.Find(trunk.Id);
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        var size = expected.Kind.Size;
        var taken = 0;
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    expected.Voxel(x, y, z, out var material, out var density);
                    actual.Voxel(x, y, z, out var got, out var gotDensity);
                    expected.Kind.Voxel(x, y, z, out var was, out _);
                    Assert.Equal(material, got);
                    if (material != VoxelConstants.Air)
                    {
                        Assert.Equal(density, gotDensity);
                    }

                    taken += was != VoxelConstants.Air && material == VoxelConstants.Air ? 1 : 0;
                }
            }
        }

        Assert.True(taken > 50, $"{taken} voxels taken from the trunk");
    }

    /// <summary>
    /// A mover's columns of 2 cm read over the turned rock before each of a dozen small craters across it read what a
    /// fresh mover's do after it: the change each crater names covers what it changed, though it is a small part of the
    /// rock's reach.
    /// </summary>
    [Fact]
    public void A_mover_reading_before_each_crater_reads_what_a_fresh_one_does()
    {
        var scene = Make();
        var passability = new NothingPassable();
        var behind = new Itera.Engine.Physics.ObjectColumns(scene.Objects, scene.Kinds, passability, 2, scene.Damage);
        const int windowX = -200,
            windowZ = 100,
            width = 100,
            depth = 100;
        var tops = new int[width * depth];
        var fresh = new int[width * depth];
        behind.ColumnTops(windowX, windowZ, width, depth, -10, 40, tops);
        var rock = scene.Placements[1];
        var reach = scene.Kinds[rock.Kind].ReachCentimetres;
        var changes = new ObjectDamageChange[1];
        var random = new Random(5);

        for (var i = 0; i < 12; i++)
        {
            var at = new VoxelCoord(-300 + random.Next(-60, 61), random.Next(0, 50), 300 + random.Next(-60, 61));
            scene.World.Apply(VoxelEdit.Sphere(at, random.Next(6, 16), VoxelEditOperation.Clear));
            if (scene.Damage.Version > 0)
            {
                scene.Damage.ReadChanges(scene.Damage.Version - 1, changes, out _);
                Assert.True(changes[0].MaxX - changes[0].MinX < reach, $"crater {i} named {changes[0]}");
            }

            behind.ColumnTops(windowX, windowZ, width, depth, -10, 40, tops);
            new Itera.Engine.Physics.ObjectColumns(scene.Objects, scene.Kinds, passability, 2, scene.Damage).ColumnTops(
                windowX,
                windowZ,
                width,
                depth,
                -10,
                40,
                fresh
            );
            Assert.Equal(fresh, tops);
        }

        Assert.True(scene.Damage.Version >= 8, $"{scene.Damage.Version} craters reached the rock");
    }

    /// <summary>
    /// The trunk's top, cut off and removed, goes with every voxel joined to it and none below the cut; applied live it
    /// falls (a part for a body), replayed it does not.
    /// </summary>
    [Fact]
    public void A_removed_part_goes_whole_and_falls_only_when_applied_live()
    {
        var live = Make();
        var replayed = Make(fill: false);
        foreach (var edit in Edits())
        {
            live.World.Apply(edit);
            replayed.World.Apply(edit);
        }

        replayed.Fill();

        var trunk = live.Placements[0];
        var damaged = live.Damage.Find(trunk.Id)!;
        var fallen = Assert.Single(live.Damage.Fallen);
        Assert.Equal(trunk.Id, fallen.Placement.Id);
        Assert.Empty(replayed.Damage.Fallen);
        // Every voxel 2.2 m up and above is gone; the stump under the cut keeps its own.
        for (var y = 44; y < 64; y++)
        {
            damaged.Voxel(6, y, 6, out var material, out _);
            Assert.Equal(VoxelConstants.Air, material);
        }

        damaged.Voxel(6, 2, 6, out var stump, out _);
        Assert.Equal(Bark, stump);
        Assert.All(fallen.Voxels, voxel => Assert.True(voxel.Y >= 44, $"Voxel {voxel} lay under the cut."));
        Assert.True(fallen.Voxels.Count > 1000, $"{fallen.Voxels.Count} voxels fell.");
    }

    /// <summary>
    /// The labeller splits a damaged object into pieces joined across faces: the trunk cut through has its top fall (it
    /// holds no voxel at the foot), the stump stays; the plank, standing on nothing (its largest piece stays), cut into
    /// two unequal halves has the smaller fall.
    /// </summary>
    [Fact]
    public void The_labeller_finds_the_pieces_that_fall()
    {
        var scene = Make();
        var edits = Edits().ToArray();
        scene.World.Apply(edits[^2]);
        var labeller = new ObjectLabeller();
        var snapshot = labeller.Rent();
        snapshot.Copy(scene.Damage.Find(scene.Placements[0].Id)!);

        labeller.Label(snapshot);

        var top = Assert.Single(snapshot.Falling);
        Assert.Equal((44, 63), (top.Min.Y, top.Max.Y));
        Assert.True(top.Seed.Y >= 44);

        // A bar of the largest-piece rule, cut through at its seventh voxel: the six-voxel end falls.
        var kinds = new VoxelObjectKinds();
        var shape = new VoxelObjectShape(5, new VoxelCoord(20, 4, 4), new VoxelCoord(50, 0, 10));
        shape.FillBox(0, 0, 0, 100, 20, 20, Rock);
        var bar = kinds.Register("bar", shape, roots: ObjectRoots.Largest);
        var damaged = new DamagedObject(new ObjectPlacement(new ObjectId(9), bar, default, 0), kinds[bar]);
        for (var z = 0; z < 4; z++)
        {
            for (var y = 0; y < 4; y++)
            {
                damaged.Set(6, y, z, VoxelConstants.Air, VoxelDensity.Empty);
            }
        }

        snapshot.Copy(damaged);
        labeller.Label(snapshot);

        var end = Assert.Single(snapshot.Falling);
        Assert.Equal((0, 5), (end.Min.X, end.Max.X));
        labeller.Dispose();
    }

    /// <summary>
    /// On the world's owner a cut through the trunk is all it takes: the labeller finds the top, a removal of it is
    /// logged, and it falls; a client following the host takes the host's edits, the removal among them, and labels
    /// nothing of its own.
    /// </summary>
    [Fact]
    public void A_cut_on_the_host_removes_the_piece_it_cuts_off()
    {
        var host = Make();
        var client = Make();
        client.World.FollowHost();
        var cut = VoxelEdit.Box(new VoxelCoord(110, 200, -40), new VoxelCoord(80, 20, 80), VoxelEditOperation.Clear);

        host.World.Apply(cut);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.World.EditCount == 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "No removal was logged.");
            host.Damage.Update();
            Thread.Sleep(1);
        }

        Assert.Equal(VoxelEditOperation.RemovePart, host.World.GetEdit(2).Operation);
        var fallen = Assert.Single(host.Damage.Fallen);
        Assert.All(fallen.Voxels, voxel => Assert.True(voxel.Y >= 44));
        for (var sequence = 1L; sequence <= host.World.EditCount; sequence++)
        {
            client.World.ApplySequenced(host.World.GetEdit(sequence));
        }

        client.Damage.Update();
        Assert.Equal(0, client.Damage.Labelling);
        AssertSame(host.Damage.Find(host.Placements[0].Id), client.Damage.Find(client.Placements[0].Id));
    }

    /// <summary>
    /// An answer of the labeller for a copy since dropped (by a reset) is not applied to the copy made after it: the cut's
    /// top stays on the trunk bitten after the reset, and nothing is logged but the three edits.
    /// </summary>
    [Fact]
    public void A_labellers_answer_for_a_dropped_copy_changes_nothing()
    {
        var host = Make(manual: true);
        host.World.Apply(
            VoxelEdit.Box(new VoxelCoord(110, 200, -40), new VoxelCoord(80, 20, 80), VoxelEditOperation.Clear)
        );
        host.Damage.Update();
        Assert.Equal(1, host.Damage.Labelling);

        host.World.Reset();
        host.World.Apply(VoxelEdit.Sphere(new VoxelCoord(140, 100, 0), 18, VoxelEditOperation.Clear));
        host.Damage.Labeller.LabelPending();
        host.Damage.Update();
        host.Damage.Labeller.LabelPending();
        host.Damage.Update();

        Assert.Equal(0, host.Damage.Labelling);
        Assert.Equal(3, host.World.EditCount);
        Assert.Empty(host.Damage.Fallen);
        var trunk = host.Damage.Find(host.Placements[0].Id);
        Assert.NotNull(trunk);
        // The trunk's middle 2.5 m up, above the first cut.
        trunk.Voxel(6, 50, 6, out var material, out _);
        Assert.Equal(Bark, material);
    }

    /// <summary>
    /// A cut made before the trunk's area was filled is looked at when the area fills and replays it: the host logs the
    /// top's removal then.
    /// </summary>
    [Fact]
    public void A_cut_replayed_on_the_host_removes_the_piece_it_cuts_off()
    {
        var host = Make(fill: false, manual: true);
        host.World.Apply(
            VoxelEdit.Box(new VoxelCoord(110, 200, -40), new VoxelCoord(80, 20, 80), VoxelEditOperation.Clear)
        );
        host.Fill();
        host.Damage.Labeller.LabelPending();
        host.Damage.Update();

        Assert.Equal(2, host.World.EditCount);
        Assert.Equal(VoxelEditOperation.RemovePart, host.World.GetEdit(2).Operation);
        Assert.Equal(0, host.Damage.Labelling);
    }

    /// <summary>
    /// A cut through the trunk 1 to 1.2 m up, in a world that copies its objects from level 2 up: with the top's removal,
    /// the host clears the far voxels (40 and 80 cm) only the top filled (bark round the trunk's axis 1.8 m up, where the
    /// trunk ran on above and below, then none), and keeps those the stump still fills (bark round it 60 cm up at 40 cm;
    /// at 80 cm the stump is a foot and a cap, which the thin parts' rule keeps nothing of); so they stay once the columns
    /// load again, and a client following the host gets the same.
    /// </summary>
    [Fact]
    public void A_cut_on_the_host_clears_the_far_copies_only_its_piece_filled()
    {
        var host = Make(copies: true);
        var top = new VoxelCoord(150, 180, 0);
        var stump = new VoxelCoord(150, 60, 0);
        Assert.All(s_copiedLevels, level => Assert.True(BarkRound(host.World, top, level)));
        Assert.True(BarkRound(host.World, stump, 2));

        host.World.Apply(
            VoxelEdit.Box(new VoxelCoord(110, 100, -40), new VoxelCoord(80, 20, 80), VoxelEditOperation.Clear)
        );
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.World.EditCount == 1)
        {
            Assert.True(DateTime.UtcNow < deadline, "No removal was logged.");
            host.Damage.Update();
            Thread.Sleep(1);
        }

        Assert.Equal(VoxelEditOperation.RemovePart, host.World.GetEdit(2).Operation);
        Assert.True(host.World.EditCount > 2);
        Assert.All(
            Enumerable.Range(3, (int)host.World.EditCount - 2),
            sequence => Assert.Equal(VoxelEditOperation.ClearCopies, host.World.GetEdit(sequence).Operation)
        );
        AssertFar(host.World);

        host.World.SetCenters([new Vector3(400, 0, 400)]);
        TestWorlds.WaitUntilLoaded(host.World);
        host.World.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(host.World);
        AssertFar(host.World);

        var client = Make(copies: true);
        client.World.FollowHost();
        for (var sequence = 1L; sequence <= host.World.EditCount; sequence++)
        {
            client.World.ApplySequenced(host.World.GetEdit(sequence));
        }

        AssertFar(client.World);

        void AssertFar(VoxelWorld world)
        {
            Assert.All(s_copiedLevels, level => Assert.False(BarkRound(world, top, level)));
            Assert.True(BarkRound(world, stump, 2));
        }

        // True when a world voxel of the level whose cube lies within a voxel of the point, across, is bark.
        static bool BarkRound(VoxelWorld world, VoxelCoord point, int level)
        {
            var edge = world.Grid.Edge(level);
            for (var dz = -edge; dz <= edge; dz += edge)
            {
                for (var dx = -edge; dx <= edge; dx += edge)
                {
                    var at = new VoxelCoord(point.X + dx, point.Y, point.Z + dz);
                    Assert.True(world.TryGetMaterial(at, level, out var material));
                    if (material == Bark)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Removals from a host that name no object of the area, a seed of air, a seed outside the kind, or a box reaching no
    /// object change nothing.
    /// </summary>
    [Fact]
    public void Hostile_part_removals_change_nothing()
    {
        var scene = Make();
        var box = new VoxelCoord(100, 0, -50);
        var size = new VoxelCoord(100, 320, 100);

        scene.World.Apply(VoxelEdit.RemovePart(999, new VoxelCoord(6, 50, 6), box, size));
        scene.World.Apply(VoxelEdit.RemovePart(1, new VoxelCoord(0, 0, 0), box, size));
        scene.World.Apply(VoxelEdit.RemovePart(1, new VoxelCoord(1000, 5, 5), box, size));
        scene.World.Apply(VoxelEdit.RemovePart(1, new VoxelCoord(6, 50, 6), new VoxelCoord(30000, 0, 30000), size));

        Assert.Equal(0, scene.Damage.Count);
        Assert.Empty(scene.Damage.Fallen);
        Assert.Null((VoxelEdit.RemovePart(1, new VoxelCoord(6, 50, 6), box, size) with { Sequence = 1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VoxelEdit.RemovePart(1, new VoxelCoord(1024, 0, 0), box, size)
        );
        // A clear of copies: one level, 1 to 9, of materials other than air; objects never take it.
        var clear = VoxelEdit.ClearCopies(2, box, size, [Bark]);
        Assert.Null(clear.Validate());
        Assert.False(ObjectEditing.Changes(in clear));
        Assert.True(clear.AppliesAtLevel(2) && !clear.AppliesAtLevel(1) && !clear.AppliesAtLevel(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => VoxelEdit.ClearCopies(0, box, size, [Bark]));
        Assert.NotNull((clear with { Material = 10 }).Validate());
        Assert.NotNull((clear with { Piece = clear.Piece | 1 }).Validate());
        Assert.NotNull((clear with { Shape = VoxelEditShape.Sphere }).Validate());
        Assert.NotNull((VoxelEdit.RemovePart(1, default, box, size) with { Turn = 1024 }).Validate());
        Assert.NotNull((VoxelEdit.RemovePart(1, default, box, size) with { Turn = 1L << 48 }).Validate());
        Assert.NotNull((VoxelEdit.RemovePart(1, default, box, size) with { Shape = VoxelEditShape.Sphere }).Validate());
        Assert.NotNull((VoxelEdit.RemovePart(1, default, box, size) with { Material = Bark }).Validate());
    }

    /// <summary>
    /// A client following its host takes each of the host's edits, a part's removal among them, as the session gives them
    /// (<see cref="VoxelWorld.ApplySequenced"/>): its objects are damaged as the host's, and the same part falls.
    /// </summary>
    [Fact]
    public void A_client_following_its_host_gets_the_same_damage()
    {
        var host = Make();
        var client = Make();
        client.World.FollowHost();
        foreach (var edit in Edits())
        {
            host.World.Apply(edit);
        }

        for (var sequence = 1L; sequence <= host.World.EditCount; sequence++)
        {
            client.World.ApplySequenced(host.World.GetEdit(sequence));
        }

        Assert.Equal(host.Damage.Count, client.Damage.Count);
        foreach (var placement in host.Placements)
        {
            if (host.Damage.Find(placement.Id) is { } expected)
            {
                AssertSame(expected, client.Damage.Find(placement.Id));
            }
        }

        Assert.Equal(Assert.Single(host.Damage.Fallen).Voxels, Assert.Single(client.Damage.Fallen).Voxels);
    }

    /// <summary>A world's saved log, a part's removal among its edits, loads into a world whose objects are damaged the same.</summary>
    [Fact]
    public void A_saved_log_with_a_part_removal_loads_the_same_damage()
    {
        var saved = Make();
        foreach (var edit in Edits())
        {
            saved.World.Apply(edit);
        }

        using var stream = new MemoryStream();
        saved.World.SaveEdits(stream, 60, []);
        stream.Position = 0;
        var loaded = Make(fill: false);

        loaded.World.LoadEdits(stream);
        loaded.Fill();

        Assert.Equal(saved.Damage.Count, loaded.Damage.Count);
        foreach (var placement in saved.Placements)
        {
            if (saved.Damage.Find(placement.Id) is { } expected)
            {
                AssertSame(expected, loaded.Damage.Find(placement.Id));
            }
        }
    }

    [Fact]
    public void A_reset_heals_every_object()
    {
        var scene = Make();
        foreach (var edit in Edits())
        {
            scene.World.Apply(edit);
        }

        scene.Damage.Update();
        Assert.True(scene.Damage.Count > 0);
        var models = scene.Models.Count;

        scene.World.Reset();

        Assert.Equal(0, scene.Damage.Count);
        Assert.All(scene.Placements, p => Assert.Null(scene.Damage.Find(p.Id)));
        Assert.True(scene.Models.Count < models, "The damaged parts' models stayed.");
    }

    /// <summary>
    /// With room for two copies, the third object damaged, the farthest from the streaming centre, keeps none; once the
    /// centre moves next to it, its copy is made again by replay, equal to the one it would have had, and the then
    /// farthest's goes.
    /// </summary>
    [Fact]
    public void Past_the_cap_the_farthest_copy_goes_and_comes_back_by_replay_near()
    {
        var scene = Make(mostDamaged: 2);
        var free = Make();
        foreach (var edit in Edits())
        {
            scene.World.Apply(edit);
            free.World.Apply(edit);
        }

        // The slab, 6 m out, is the farthest from the middle.
        var slab = scene.Placements[2];
        Assert.Equal((2, 1), (scene.Damage.Count, scene.Damage.DroppedCount));
        Assert.Null(scene.Damage.Find(slab.Id));

        scene.World.SetCenters([new Vector3(6, 0, 6)]);
        scene.Damage.Update();

        AssertSame(free.Damage.Find(slab.Id), scene.Damage.Find(slab.Id));
        Assert.Equal((2, 1), (scene.Damage.Count, scene.Damage.DroppedCount));
    }

    /// <summary>When the objects' areas drop, their copies go; when they fill again, the damage is replayed.</summary>
    [Fact]
    public void An_areas_copies_go_with_it_and_come_back()
    {
        var scene = Make();
        foreach (var edit in Edits())
        {
            scene.World.Apply(edit);
        }

        var trunk = scene.Placements[0];
        var before = Snapshot(scene.Damage.Find(trunk.Id)!);

        scene.World.SetCenters([new Vector3(400, 0, 400)]);
        TestWorlds.WaitUntilLoaded(scene.World);
        scene.Objects.Update();
        Assert.Equal(0, scene.Damage.Count);

        scene.World.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(scene.World);
        scene.Fill();
        Assert.Equal(before, Snapshot(scene.Damage.Find(trunk.Id)!));
    }

    /// <summary>
    /// A clear a player made counts what it takes from the trunk's bark for the game, in cubic centimetres: the density
    /// its voxels lost times their volume, rounded once.
    /// </summary>
    [Fact]
    public void A_clear_with_a_maker_counts_what_it_takes_from_objects()
    {
        var scene = Make();
        var removals = new Recorder();
        scene.World.Removals = removals;
        var dig = VoxelEdit.Sphere(new VoxelCoord(140, 100, 0), 18, VoxelEditOperation.Clear);

        scene.World.Apply(dig, maker: 0);

        var trunk = scene.Placements[0];
        var kind = scene.Kinds[trunk.Kind];
        var damaged = scene.Damage.Find(trunk.Id)!;
        long steps = 0;
        for (var z = 0; z < kind.Size.Z; z++)
        {
            for (var y = 0; y < kind.Size.Y; y++)
            {
                for (var x = 0; x < kind.Size.X; x++)
                {
                    kind.Voxel(x, y, z, out var was, out var before);
                    damaged.Voxel(x, y, z, out _, out var after);
                    steps += was == Bark ? (before - after) * 125L : 0;
                }
            }
        }

        Assert.Equal(1, removals.Calls);
        Assert.True(steps > 0);
        // The world's ground lies under y = 0, out of the dig's reach: all it counts is bark.
        Assert.Equal((steps + VoxelDensity.Full / 2) / VoxelDensity.Full, removals.Last[Bark]);
    }

    /// <summary>A paint over the trunk turns the voxels whose middles it covers to rock; a fill leaves the objects alone.</summary>
    [Fact]
    public void A_paint_recolours_an_object_and_a_fill_leaves_it()
    {
        var scene = Make();

        scene.World.Apply(VoxelEdit.Sphere(new VoxelCoord(150, 150, 0), 200, VoxelEditOperation.Fill, Rock));
        Assert.Equal(0, scene.Damage.Count);

        scene.World.Apply(VoxelEdit.Sphere(new VoxelCoord(150, 150, 0), 20, VoxelEditOperation.Paint, Rock));
        var trunk = scene.Placements[0];
        var damaged = scene.Damage.Find(trunk.Id)!;
        // The voxel at the axis 1.5 m up: kind voxel (6, 30, 6) holds (150, 150, 0) at the trunk's anchor (150, 0, 0).
        damaged.Voxel(6, 30, 6, out var painted, out var density);
        scene.Kinds[trunk.Kind].Voxel(6, 30, 6, out _, out var was);
        Assert.Equal((Rock, was), (painted, density));
    }

    /// <summary>
    /// A dig across the border of a kind two parts wide makes both parts' models again, one an update
    /// (<see cref="ObjectDamage.PartsPerUpdate"/>): each holds its box's voxels as the damage left them, and its coarser
    /// levels what the kind's rule makes of them.
    /// </summary>
    [Fact]
    public void Damaged_parts_models_are_their_voxels_at_three_levels()
    {
        var scene = Make();
        var wall = scene.Placements[3];
        // The wall's two parts meet 112 voxels (2.24 m) from its corner, at x = 2.24 - 1.3 = 0.94 m from its anchor.
        scene.World.Apply(
            VoxelEdit.Sphere(new VoxelCoord(wall.Anchor.X + 94, 30, wall.Anchor.Z), 15, VoxelEditOperation.Clear)
        );
        scene.Damage.Update();
        var damaged = scene.Damage.Find(wall.Id)!;
        Assert.Equal(1, ObjectDamage.PartsPerUpdate);
        Assert.Equal(1, damaged.Models.Count(model => model >= 0));
        scene.Damage.Update();

        var kind = scene.Kinds[wall.Kind];
        Assert.Equal(2, kind.PartCount);
        var materials = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        var expected = new byte[VoxelConstants.ChunkVoxels];
        var expectedDensities = new byte[VoxelConstants.ChunkVoxels];
        for (var p = 0; p < kind.PartCount; p++)
        {
            var part = kind.Parts[p];
            var model = damaged.Models[p];
            Assert.True(model >= 0, $"Part {p} kept its kind's model.");
            var corner = new VoxelCoord(part.Offset.X - 8, part.Offset.Y - 8, part.Offset.Z - 8);
            for (var level = 0; level < VoxelModels.MaxLevels; level++)
            {
                var size = part.SizeAt(level);
                var tree = new ModelTree(scene.Models.Tree(model, level));
                Array.Clear(materials);
                Array.Clear(densities);
                scene.Models.Decode(tree, materials, size);
                scene.Models.DecodeDensities(tree, densities, size);
                ObjectPart
                    .PartLevel.Build(in damaged, corner, part.Size, level)
                    .Decode(size, expected, expectedDensities);
                for (var z = 0; z < size.Z; z++)
                {
                    for (var y = 0; y < size.Y; y++)
                    {
                        for (var x = 0; x < size.X; x++)
                        {
                            var i = x + 128 * (y + 128 * z);
                            Assert.True(
                                materials[i] == expected[i] && densities[i] == expectedDensities[i],
                                $"Part {p} level {level} voxel ({x}, {y}, {z}): {materials[i]}/{densities[i]}, expected {expected[i]}/{expectedDensities[i]}."
                            );
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// The mover's cells, the rays, and the bodies' statics read the damage: a character standing on the rock and a ball
    /// resting on it fall when the rock under them is dug away, and a ray goes through a hole dug through the trunk.
    /// </summary>
    [Fact]
    public void The_mover_rays_and_statics_meet_an_object_as_dug()
    {
        var scene = Make();
        var passability = new NothingPassable();
        var mover = new Itera.Engine.Physics.VoxelCharacterMover(scene.World, passability)
        {
            Objects = new Itera.Engine.Physics.ObjectColumns(scene.Objects, scene.Kinds, passability, 10, scene.Damage),
        };
        var rays = new ObjectRays(scene.Objects, scene.Kinds, scene.World, scene.Damage);
        using var physics = new Itera.Engine.Physics.PhysicsWorld(
            NullLogger<Itera.Engine.Physics.PhysicsWorld>.Instance
        );
        physics.Initialize();
        using var colliders = new Itera.Engine.Physics.VoxelColliders(
            physics,
            scene.World,
            passability,
            NullLogger<Itera.Engine.Physics.VoxelColliders>.Instance,
            scene.Objects,
            scene.Kinds,
            scene.Damage
        );
        colliders.Initialize();
        var shape = new Itera.Engine.Physics.CharacterShape(1.7f, 0.26f, 0.4f);
        var feet = new Vector3(-3, 1, 3.2f);
        var velocity = Vector3.Zero;
        var sphere = new BepuPhysics.Collidables.Sphere(0.1f);
        var ball = physics.Simulation.Bodies.Add(
            BepuPhysics.BodyDescription.CreateDynamic(
                new BepuPhysics.RigidPose(new Vector3(-3, 1, 2.8f)),
                sphere.ComputeInertia(1),
                new BepuPhysics.Collidables.CollidableDescription(physics.Simulation.Shapes.Add(sphere), 0.1f),
                new BepuPhysics.BodyActivityDescription(0.01f)
            )
        );
        void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                velocity.Y -= 20f / 60;
                mover.Move(ref feet, ref velocity, in shape, 1f / 60);
                colliders.Update(1f / 60);
                physics.Step(1f / 60);
            }
        }

        Run(90);
        Assert.Equal(0.5f, feet.Y, 0.01f);
        Assert.Equal(0.6f, physics.Simulation.Bodies[ball].Pose.Position.Y, 0.01f);
        Assert.True(rays.Raycast(new Vector3(0, 1, 0), Vector3.UnitX, 3, out _));

        // The rock dug away round both, and a hole through the trunk at a metre up.
        scene.World.Apply(
            VoxelEdit.Box(new VoxelCoord(-360, 0, 240), new VoxelCoord(120, 80, 120), VoxelEditOperation.Clear)
        );
        scene.World.Apply(VoxelEdit.Sphere(new VoxelCoord(150, 100, 0), 35, VoxelEditOperation.Clear));
        Run(120);

        Assert.Equal(0, feet.Y, 0.01f);
        Assert.Equal(0.1f, physics.Simulation.Bodies[ball].Pose.Position.Y, 0.01f);
        Assert.False(rays.Raycast(new Vector3(0, 1, 0), Vector3.UnitX, 3, out _));
    }

    /// <summary>
    /// The bush's leaves let a character through; once a paint turns its middle to rock, the character stops at it: the
    /// mover reads a damaged object's passable voxels too.
    /// </summary>
    [Fact]
    public void A_bush_painted_to_rock_stops_a_character()
    {
        var scene = Make();
        var leaves = new LeavesPass();
        var mover = new Itera.Engine.Physics.VoxelCharacterMover(scene.World, leaves)
        {
            Objects = new Itera.Engine.Physics.ObjectColumns(scene.Objects, scene.Kinds, leaves, 10, scene.Damage),
        };
        var shape = new Itera.Engine.Physics.CharacterShape(1.7f, 0.26f, 0.4f);
        float Walk()
        {
            var feet = new Vector3(3, 0, -1.5f);
            var velocity = Vector3.Zero;
            for (var i = 0; i < 60; i++)
            {
                velocity = new Vector3(0, velocity.Y - 0.3f, -3);
                mover.Move(ref feet, ref velocity, in shape, 1f / 60);
            }

            return feet.Z;
        }

        Assert.True(Walk() < -4, "The leaves stopped it.");

        scene.World.Apply(VoxelEdit.Sphere(new VoxelCoord(300, 50, -300), 45, VoxelEditOperation.Paint, Rock));

        Assert.InRange(Walk(), -2.6f, -1.6f);
    }

    /// <summary>
    /// Every block of a damaged part's model that its changes do not mark reads, at every level, the voxels and densities
    /// its kind's part's model does there: it meshes as the kind's does, whose mesh the model shares.
    /// </summary>
    [Fact]
    public void A_damaged_parts_unshared_blocks_are_those_its_changes_reach()
    {
        var scene = Make();
        foreach (var edit in Edits())
        {
            scene.World.Apply(edit);
        }

        var wall = scene.Placements[3];
        scene.World.Apply(
            VoxelEdit.Sphere(new VoxelCoord(wall.Anchor.X + 94, 30, wall.Anchor.Z), 15, VoxelEditOperation.Clear)
        );
        for (var i = 0; i < 4; i++)
        {
            scene.Damage.Update();
        }

        var chunk = VoxelConstants.ChunkVoxels;
        byte[] own = new byte[chunk],
            ownDensities = new byte[chunk],
            kind = new byte[chunk],
            kindDensities = new byte[chunk];
        var samples = new byte[4][];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = new byte[VoxelWorld.BlockSampleEdge * VoxelWorld.BlockSampleEdge * VoxelWorld.BlockSampleEdge];
        }

        int marked = 0,
            shared = 0;
        foreach (var placement in scene.Placements)
        {
            if (scene.Damage.Find(placement.Id) is not { } damaged)
            {
                continue;
            }

            var parts = scene.KindModels.PartsOf(placement.Kind);
            for (var p = 0; p < parts.Length; p++)
            {
                if (damaged.Models[p] < 0)
                {
                    continue;
                }

                var changed = damaged.ChangedBlocks[p]!;
                var part = damaged.Kind.Parts[p];
                for (var level = 0; level < VoxelModels.MaxLevels; level++)
                {
                    var size = part.SizeAt(level);
                    Decode(scene.Models, damaged.Models[p], level, size, own, ownDensities);
                    Decode(scene.Models, parts[p].Model, level, size, kind, kindDensities);
                    var (countX, countY, countZ) = ModelBlocks.Counts(size);
                    for (var b = 0; b < countX * countY * countZ; b++)
                    {
                        if (changed[level][b])
                        {
                            marked++;
                            continue;
                        }

                        int bx = b % countX,
                            by = b / countX % countY,
                            bz = b / (countX * countY);
                        Itera.Engine.Rendering.Meshes.WorldMeshes.ReadModelBlock(own, size, bx, by, bz, samples[0]);
                        Itera.Engine.Rendering.Meshes.WorldMeshes.ReadModelBlock(kind, size, bx, by, bz, samples[1]);
                        Itera.Engine.Rendering.Meshes.WorldMeshes.ReadModelBlock(
                            ownDensities,
                            size,
                            bx,
                            by,
                            bz,
                            samples[2]
                        );
                        Itera.Engine.Rendering.Meshes.WorldMeshes.ReadModelBlock(
                            kindDensities,
                            size,
                            bx,
                            by,
                            bz,
                            samples[3]
                        );
                        Assert.True(
                            samples[0].AsSpan().SequenceEqual(samples[1])
                                && samples[2].AsSpan().SequenceEqual(samples[3]),
                            $"Object {placement.Id.Value} part {p} level {level} block ({bx}, {by}, {bz}) differs from its kind's but shares its mesh."
                        );
                        shared++;
                    }
                }
            }
        }

        Assert.True(marked > 0 && shared > 0, $"{marked} blocks meshed again, {shared} shared.");

        static void Decode(VoxelModels models, int model, int level, VoxelCoord size, byte[] voxels, byte[] densities)
        {
            Array.Clear(voxels);
            Array.Clear(densities);
            var tree = new ModelTree(models.Tree(model, level));
            models.Decode(tree, voxels, size);
            models.DecodeDensities(tree, densities, size);
        }
    }

    /// <summary>A second dig over voxels already damaged, and the models made again, allocate nothing.</summary>
    [Fact]
    public void A_second_dig_on_a_damaged_object_allocates_nothing()
    {
        // The labeller on the test's thread: its first answer is taken, and its snapshot returned, in the measured
        // update, as happens whenever the thread is slow.
        var scene = Make(manual: true);
        var dig = VoxelEdit.Sphere(new VoxelCoord(140, 100, 0), 18, VoxelEditOperation.Clear);
        scene.World.Apply(dig);
        scene.Damage.Update();
        scene.World.Apply(dig);
        scene.Damage.Update();
        scene.Damage.Labeller.LabelPending();

        var start = Itera.TestSupport.SystemAllocation.StartMeasurement();
        scene.World.Apply(dig);
        scene.Damage.Update();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>
    /// A trunk, the rock turned, a slab 6 m out, a wall two parts wide, and a bush of leaves; with
    /// <paramref name="atEdge"/>, another wall 1 m inside the map's edge (x = 512 m), streamed round 508 m out.
    /// </summary>
    private Scene Make(
        bool fill = true,
        int mostDamaged = 256,
        bool atEdge = false,
        bool copies = false,
        bool manual = false
    )
    {
        var scene = new Scene(mostDamaged, atEdge, copies, manual);
        _scenes.Add(scene);
        if (fill)
        {
            scene.Fill();
        }

        return scene;
    }

    private static IEnumerable<VoxelEdit> Edits(bool withRemoval = true)
    {
        yield return VoxelEdit.Sphere(new VoxelCoord(140, 100, 0), 18, VoxelEditOperation.Clear);
        yield return VoxelEdit.Capsule(
            new VoxelCoord(-360, 20, 280),
            new VoxelCoord(-250, 30, 330),
            12,
            VoxelEditOperation.Clear
        );
        yield return VoxelEdit.Box(new VoxelCoord(560, 20, 560), new VoxelCoord(40, 60, 40), VoxelEditOperation.Clear);
        yield return VoxelEdit.Sphere(new VoxelCoord(150, 60, 10), 15, VoxelEditOperation.Paint, Rock);
        yield return VoxelEdit.Sphere(new VoxelCoord(150, 64, 4), 9, VoxelEditOperation.Clear);
        yield return VoxelEdit.MaterialsBox(new VoxelCoord(130, 150, -20), new VoxelCoord(30, 30, 40), [Bark]);
        yield return VoxelEdit.Sphere(new VoxelCoord(-300, 40, 300), 12, VoxelEditOperation.Set, VoxelConstants.Air);
        if (!withRemoval)
        {
            yield break;
        }

        // The trunk cut through from 2 to 2.2 m, and its top, joined to its voxel 2.5 m up, removed.
        yield return VoxelEdit.Box(new VoxelCoord(110, 200, -40), new VoxelCoord(80, 20, 80), VoxelEditOperation.Clear);
        yield return VoxelEdit.RemovePart(
            1,
            new VoxelCoord(6, 50, 6),
            new VoxelCoord(125, 220, -25),
            new VoxelCoord(50, 100, 50)
        );
    }

    /// <summary>Whether voxel (x, y, z)'s cube, turned as drawn, lies wholly inside the box, or wholly outside it, by a centimetre.</summary>
    private static (bool Inside, bool Outside) Corners(
        ObjectPlacement placement,
        VoxelObjectKind kind,
        Quaternion turn,
        int x,
        int y,
        int z,
        VoxelEdit box
    )
    {
        var unit = kind.UnitCentimetres;
        var anchor = new Vector3(placement.Anchor.X, placement.Anchor.Y, placement.Anchor.Z);
        var offset = new Vector3(kind.AnchorCentimetres.X, kind.AnchorCentimetres.Y, kind.AnchorCentimetres.Z);
        var min = new Vector3(box.Origin.X, box.Origin.Y, box.Origin.Z);
        var max = min + new Vector3(box.Extent.X, box.Extent.Y, box.Extent.Z);
        var inside = true;
        var lowest = new Vector3(float.MaxValue);
        var highest = new Vector3(float.MinValue);
        for (var corner = 0; corner < 8; corner++)
        {
            var own = new Vector3(x + (corner & 1), y + ((corner >> 1) & 1), z + (corner >> 2)) * unit - offset;
            var world = anchor + Vector3.Transform(own, turn);
            inside &= world.X > min.X + 1 && world.Y > min.Y + 1 && world.Z > min.Z + 1;
            inside &= world.X < max.X - 1 && world.Y < max.Y - 1 && world.Z < max.Z - 1;
            lowest = Vector3.Min(lowest, world);
            highest = Vector3.Max(highest, world);
        }

        var outside =
            highest.X < min.X - 1
            || highest.Y < min.Y - 1
            || highest.Z < min.Z - 1
            || lowest.X > max.X + 1
            || lowest.Y > max.Y + 1
            || lowest.Z > max.Z + 1;
        return (inside, outside);
    }

    private static void AssertSame(DamagedObject? expected, DamagedObject? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(Snapshot(expected), Snapshot(actual));
    }

    /// <summary>Every voxel of the damaged object, materials then densities, as one string to compare.</summary>
    private static string Snapshot(DamagedObject damaged)
    {
        var size = damaged.Kind.Size;
        var bytes = new byte[2 * size.X * size.Y * size.Z];
        var i = 0;
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    damaged.Voxel(x, y, z, out var material, out var density);
                    bytes[i++] = material;
                    bytes[i++] = density;
                }
            }
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private sealed class LeavesPass : IVoxelPassability
    {
        public bool IsPassable(byte material) => material == Leaves;
    }

    private sealed class Recorder : IVoxelRemovals
    {
        public int Calls;
        public long[] Last = new long[256];

        public void Removed(in VoxelEdit edit, int maker, ReadOnlySpan<long> byMaterial)
        {
            Calls++;
            byMaterial.CopyTo(Last);
        }
    }

    /// <summary>
    /// A world of flat stone placing a trunk, the rock turned, a slab, and a wall two parts wide, its objects, their
    /// models, and their damage, made in a match's order.
    /// </summary>
    private sealed class Scene : IDisposable
    {
        public Scene(int mostDamaged, bool atEdge, bool copies = false, bool manual = false)
        {
            Kinds = new VoxelObjectKinds();
            var trunk = Register(
                "trunk",
                5,
                new VoxelCoord(12, 64, 12),
                new VoxelCoord(30, 0, 30),
                s => s.FillCapsule(30, -10, 30, 30, 300, 30, 25, Bark)
            );
            var rock = Register(
                "rock",
                5,
                new VoxelCoord(26, 12, 22),
                new VoxelCoord(65, 0, 55),
                s => s.FillBox(5, -10, 5, 125, 50, 105, Rock)
            );
            var slab = Register(
                "slab",
                5,
                new VoxelCoord(20, 10, 20),
                new VoxelCoord(50, 0, 50),
                s => s.FillBox(0, 0, 0, 100, 50, 100, Rock)
            );
            var wall = Register(
                "wall",
                2,
                new VoxelCoord(130, 40, 10),
                new VoxelCoord(130, 0, 10),
                s => s.FillBox(0, 0, 0, 260, 80, 20, Rock)
            );
            var bush = Register(
                "bush",
                4,
                new VoxelCoord(32, 30, 32),
                new VoxelCoord(64, 0, 64),
                s => s.FillEllipsoid(64, 50, 64, 60, 50, 60, Leaves)
            );
            Placements =
            [
                new ObjectPlacement(new ObjectId(1), trunk, new VoxelCoord(150, 0, 0), 0),
                new ObjectPlacement(new ObjectId(2), rock, new VoxelCoord(-300, 0, 300), 700),
                new ObjectPlacement(new ObjectId(3), slab, new VoxelCoord(600, 0, 600), 1500),
                new ObjectPlacement(new ObjectId(4), wall, new VoxelCoord(-300, 0, -300), 0),
                new ObjectPlacement(new ObjectId(5), bush, new VoxelCoord(300, 0, -300), 0),
                .. atEdge
                    ? [new ObjectPlacement(new ObjectId(6), wall, new VoxelCoord(51100, 0, 0), 0)]
                    : Array.Empty<ObjectPlacement>(),
            ];
            var generator = new PlacingGenerator([.. Placements]);
            var centre = atEdge ? new Vector3(508, 0, 0) : Vector3.Zero;
            if (copies)
            {
                // Four levels, the objects copied into the world's from level 2 (40 cm) up.
                World = new VoxelWorld(
                    new VoxelSettings
                    {
                        StreamingRadiusMetres = 0,
                        Levels = 4,
                        MemoryBudgetMegabytes = 256,
                        WorkerThreads = 2,
                        Seed = 17,
                    },
                    0,
                    generator,
                    NullLogger<VoxelWorld>.Instance,
                    kinds: Kinds,
                    objectHandOverLevel: 2
                );
                World.Initialize();
                World.SetCenters([centre]);
                TestWorlds.WaitUntilLoaded(World);
            }
            else
            {
                World = TestWorlds.Load(generator, centre, radiusMetres: 0);
            }
            Models = new VoxelModels(World);
            var options = Options.Create(
                new EngineOptions { Objects = new ObjectSettings { MostDamaged = mostDamaged } }
            );
            Objects = new ObjectWorld(World, Kinds, generator, options, NullLogger<ObjectWorld>.Instance);
            KindModels = new VoxelObjects(
                Kinds,
                World,
                Models,
                new NothingPassable(),
                NullLogger<VoxelObjects>.Instance
            );
            Damage = new ObjectDamage(
                World,
                Objects,
                Kinds,
                KindModels,
                Models,
                options,
                NullLogger<ObjectDamage>.Instance
            );
            // Manual: the labeller answers only when the test labels what was asked (LabelPending).
            Damage.Labeller.Manual = manual;
            KindModels.Initialize();
            Objects.Initialize();
            Damage.Initialize();
        }

        public VoxelObjectKinds Kinds { get; }

        public ObjectPlacement[] Placements { get; }

        public VoxelWorld World { get; }

        public VoxelModels Models { get; }

        public ObjectWorld Objects { get; }

        public VoxelObjects KindModels { get; }

        public ObjectDamage Damage { get; }

        /// <summary>Fills the objects' areas round the loaded world.</summary>
        public void Fill()
        {
            for (var i = 0; i < 100 && Objects.WaitingAreas > 0; i++)
            {
                Objects.Update();
            }

            Damage.Update();
        }

        public void Dispose()
        {
            Damage.Dispose();
            Models.Dispose();
            World.Dispose();
        }

        private VoxelObjectKindId Register(
            string name,
            int unit,
            VoxelCoord size,
            VoxelCoord anchor,
            Action<VoxelObjectShape> fill
        )
        {
            var shape = new VoxelObjectShape(unit, size, anchor);
            fill(shape);
            return Kinds.Register(name, shape);
        }
    }
}
