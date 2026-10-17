using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// Objects placed and removed whole during play (<see cref="ObjectWorld.TryPlace"/>, <see cref="ObjectWorld.TryRemove"/>:
/// <see cref="VoxelEditOperation.PlaceObject"/>, <see cref="VoxelEditOperation.RemoveObject"/>), on a world whose
/// generator places none: a placed block is kept at once, met by rays, and named to the readers; a removed one goes,
/// its damage with it; a world that loads its area after the edits holds the same objects with the same damage bit for
/// bit, an edit made before a block's placing leaving it whole; a save holds them; a reset takes them; one the
/// generator placed is removed and stays removed when its area fills again; a client places nothing, a kind copied
/// into the coarse levels is neither placed nor removed, and a placing whose kind or box is wrong is refused, by the
/// host's world and by a client's.
/// </summary>
public sealed class ObjectPlacingTests : IDisposable
{
    private const byte Stone = 40;
    private readonly List<Scene> _scenes = [];

    public void Dispose()
    {
        foreach (var scene in _scenes)
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void A_placed_block_is_kept_at_once_met_by_rays_and_named_to_readers_and_a_removed_one_goes()
    {
        var scene = Make();
        var changes = scene.Damage.Version;
        var set = scene.Objects.SetVersion;

        Assert.True(
            scene.Objects.TryPlace(scene.Block, new VoxelCoord(200, 0, 0), 1024, VoxelEdit.NoMaker, out var block)
        );

        var near = new List<ObjectPlacement>();
        Assert.True(scene.Objects.Near(150, 250, -50, 50, near));
        Assert.Equal([block], near);
        Assert.Equal(scene.World.EditCount, block.Since);
        Assert.Equal(1, scene.Objects.Count);
        Assert.True(scene.Damage.Version > changes);
        Assert.True(scene.Objects.SetVersion > set);
        // A ray along x at 50 cm up meets the block's face 50 cm short of its anchor.
        Assert.True(scene.Rays.Raycast(new Vector3(0, 0.5f, 0), Vector3.UnitX, 10, out var hit));
        Assert.Equal(block.Id, hit.Id);
        Assert.Equal(1.5f, hit.DistanceMetres, 0.01f);

        // Dug into, then removed: the object and its damage go.
        scene.World.Apply(VoxelEdit.Sphere(new VoxelCoord(150, 50, 0), 20, VoxelEditOperation.Clear));
        Assert.NotNull(scene.Damage.Find(block.Id));
        Assert.True(scene.Objects.TryRemove(in block, VoxelEdit.NoMaker));

        near.Clear();
        scene.Objects.Near(150, 250, -50, 50, near);
        Assert.Empty(near);
        Assert.Equal(0, scene.Objects.Count);
        Assert.Null(scene.Damage.Find(block.Id));
        Assert.False(scene.Rays.Raycast(new Vector3(0, 0.5f, 0), Vector3.UnitX, 10, out _));
        // Not twice.
        Assert.False(scene.Objects.TryRemove(in block, VoxelEdit.NoMaker));
    }

    [Fact]
    public void A_world_loading_its_area_after_the_edits_holds_the_same_objects_and_damage_and_a_dig_before_a_placing_leaves_it_whole()
    {
        var live = Make();
        // A dig where the first block will stand, before it does; two blocks, one dug into and one removed.
        live.World.Apply(VoxelEdit.Sphere(new VoxelCoord(200, 50, 0), 30, VoxelEditOperation.Clear));
        Assert.True(live.Objects.TryPlace(live.Block, new VoxelCoord(200, 0, 0), 0, VoxelEdit.NoMaker, out var first));
        Assert.True(
            live.Objects.TryPlace(live.Block, new VoxelCoord(-200, 0, 100), 512, VoxelEdit.NoMaker, out var second)
        );
        Assert.True(live.Objects.TryPlace(live.Block, new VoxelCoord(0, 0, -300), 0, VoxelEdit.NoMaker, out var third));
        live.World.Apply(VoxelEdit.Sphere(new VoxelCoord(-250, 60, 100), 25, VoxelEditOperation.Clear));
        Assert.True(live.Objects.TryRemove(in third, VoxelEdit.NoMaker));

        var replayed = Make(fill: false);
        foreach (var edit in Edits(live.World))
        {
            replayed.World.Apply(edit);
        }

        replayed.Fill();

        Assert.Null(live.Damage.Find(first.Id));
        AssertSameObjects(live, replayed);
        Assert.Null(replayed.Damage.Find(first.Id));
        Assert.Equal(Snapshot(live.Damage.Find(second.Id)), Snapshot(replayed.Damage.Find(second.Id)));
    }

    [Fact]
    public void A_save_holds_the_placed_and_removed_objects_and_a_reset_takes_them()
    {
        var live = Make();
        Assert.True(live.Objects.TryPlace(live.Block, new VoxelCoord(200, 0, 0), 0, VoxelEdit.NoMaker, out _));
        Assert.True(
            live.Objects.TryPlace(live.Block, new VoxelCoord(-200, 0, 100), 512, VoxelEdit.NoMaker, out var gone)
        );
        Assert.True(live.Objects.TryRemove(in gone, VoxelEdit.NoMaker));
        using var stream = new MemoryStream();
        live.World.SaveEdits(stream, 60, []);

        stream.Position = 0;
        var loaded = Make(fill: false, load: stream);
        loaded.Fill();

        AssertSameObjects(live, loaded);
        Assert.Equal(1, loaded.Objects.Count);

        live.World.Reset();
        TestWorlds.WaitUntilLoaded(live.World);
        live.Fill();
        Assert.Equal(0, live.Objects.Count);
    }

    [Fact]
    public void An_object_the_generator_placed_is_removed_and_stays_removed_when_its_area_fills_again()
    {
        var scene = Make(generated: true);
        var rock = scene.Generated[0];
        Assert.Equal(1, scene.Objects.Count);

        Assert.True(scene.Objects.TryRemove(in rock, VoxelEdit.NoMaker));
        Assert.Equal(0, scene.Objects.Count);

        // Its area dropped with every column and filled again: the generator's rock, then the removal from the log.
        scene.World.SetCenters([new Vector3(2000, 0, 2000)]);
        TestWorlds.WaitUntilLoaded(scene.World);
        scene.Fill();
        scene.World.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(scene.World);
        scene.Fill();

        var near = new List<ObjectPlacement>();
        Assert.True(scene.Objects.Near(-500, 500, -500, 500, near));
        Assert.Empty(near);
    }

    [Fact]
    public void A_client_places_nothing_a_copied_kind_is_neither_placed_nor_removed_and_wrong_placings_are_refused()
    {
        // Copies come from the generator's placer alone: with none, no kind is copied.
        Assert.True(Make(copied: true).Objects.CanPlaceOrRemove(Make().Copied));
        var scene = Make(copied: true, generated: true);
        var edits = scene.World.EditCount;
        var placement = new ObjectPlacement(new ObjectId(99), scene.Block, new VoxelCoord(200, 0, 0), 0);
        var kind = scene.Kinds[scene.Block];
        var good = VoxelEdit.PlaceObject(
            in placement,
            kind.ReachCentimetres,
            kind.BottomCentimetres,
            kind.TopCentimetres
        );

        // A kind copied into the world's coarse levels.
        Assert.False(scene.Objects.CanPlaceOrRemove(scene.Copied));
        Assert.False(scene.Objects.TryPlace(scene.Copied, new VoxelCoord(0, 0, 200), 0, VoxelEdit.NoMaker, out _));
        var copied = scene.Kinds[scene.Copied];
        Assert.False(
            scene.World.TryApply(
                VoxelEdit.PlaceObject(
                    placement with
                    {
                        Kind = scene.Copied,
                    },
                    copied.ReachCentimetres,
                    copied.BottomCentimetres,
                    copied.TopCentimetres
                ),
                VoxelEdit.NoMaker,
                out _
            )
        );
        // A kind past the run's, and a box not the kind's.
        Assert.False(scene.World.TryApply(good with { Turn = good.Turn | 0xFFFF }, VoxelEdit.NoMaker, out _));
        Assert.False(
            scene.World.TryApply(
                good with
                {
                    Extent = new VoxelCoord(good.Extent.X + 2, good.Extent.Y, good.Extent.Z + 2),
                },
                VoxelEdit.NoMaker,
                out _
            )
        );
        Assert.NotNull((good with { Extent = good.Extent with { X = good.Extent.X + 1 } }).Validate());
        Assert.Equal(edits, scene.World.EditCount);

        // A client's world takes the host's placing and refuses a wrong one; it places nothing itself.
        var client = Make();
        client.World.FollowHost();
        client.World.ApplySequenced(good with { Sequence = 1 });
        Assert.Equal(1, client.Objects.Count);
        Assert.Throws<ArgumentException>(() =>
            client.World.ApplySequenced(good with { Sequence = 2, Turn = good.Turn | 0xFFFF })
        );
        Assert.False(client.Objects.TryPlace(client.Block, new VoxelCoord(-200, 0, 0), 0, VoxelEdit.NoMaker, out _));
        // It may still ask where one could stand, for a preview.
        Assert.True(client.Objects.CanPlaceAt(client.Block, new VoxelCoord(-200, 0, 0)));
    }

    /// <summary>
    /// A placing whose box the log would not index under its area (wholly above or below the map's height) is refused,
    /// and <see cref="ObjectWorld.CanPlaceAt"/> says so first, where it was held while its area stayed filled and lost at
    /// the area's next fill; one on the ground is taken, and a world that loads its area after it holds it.
    /// </summary>
    [Fact]
    public void A_placing_the_log_would_not_find_again_is_refused()
    {
        var scene = Make();
        var edge = scene.World.Grid.Edge(0);
        var above = new VoxelCoord(200, scene.World.Grid.EndVoxelY * edge + 500, 0);
        var below = new VoxelCoord(200, scene.World.Grid.MinVoxelY * edge - 500, 0);
        var ground = new VoxelCoord(200, 0, 0);

        Assert.False(scene.Objects.CanPlaceAt(scene.Block, above));
        Assert.False(scene.Objects.CanPlaceAt(scene.Block, below));
        Assert.True(scene.Objects.CanPlaceAt(scene.Block, ground));
        Assert.False(scene.Objects.TryPlace(scene.Block, above, 0, VoxelEdit.NoMaker, out _));
        Assert.False(scene.Objects.TryPlace(scene.Block, below, 0, VoxelEdit.NoMaker, out _));
        Assert.True(scene.Objects.TryPlace(scene.Block, ground, 0, VoxelEdit.NoMaker, out _));
        var replayed = Make(fill: false);
        foreach (var edit in Edits(scene.World))
        {
            replayed.World.Apply(edit);
        }

        replayed.Fill();

        AssertSameObjects(scene, replayed);
    }

    /// <summary>
    /// A world that loads its area after 2,000 blocks were placed in a 20 by 20 m square and 200 digs made among them
    /// fills it with every block, the dug ones as damaged as they were, in a time that is logged.
    /// </summary>
    [Fact]
    public void An_area_of_two_thousand_placed_blocks_fills_with_their_damage()
    {
        var live = Make();
        var placed = 0;
        for (var z = 0; z < 50 && placed < 2000; z++)
        {
            for (var x = 0; x < 40 && placed < 2000; x++, placed++)
            {
                Assert.True(
                    live.Objects.TryPlace(
                        live.Block,
                        new VoxelCoord(-1000 + 50 * x, 100 * (z % 2), -1000 + 40 * z),
                        0,
                        VoxelEdit.NoMaker,
                        out _
                    )
                );
            }
        }

        for (var dig = 0; dig < 200; dig++)
        {
            live.World.Apply(
                VoxelEdit.Sphere(new VoxelCoord(-950 + 9 * dig, 60, -900 + 7 * dig), 15, VoxelEditOperation.Clear)
            );
        }

        var replayed = Make(fill: false);
        foreach (var edit in Edits(live.World))
        {
            replayed.World.Apply(edit);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        replayed.Fill();
        var elapsed = clock.Elapsed.TotalMilliseconds;

        Assert.Equal(2000, replayed.Objects.Count);
        Assert.Equal(live.Damage.Count, replayed.Damage.Count);
        Assert.True(live.Damage.Count > 50, $"Only {live.Damage.Count} blocks were dug into.");
        Console.WriteLine($"Filled 2,000 placed blocks and {live.Damage.Count} damaged ones in {elapsed:F1} ms.");
    }

    /// <summary>The world's log of edits since its last reset, in order.</summary>
    private static List<VoxelEdit> Edits(VoxelWorld world)
    {
        var edits = new List<VoxelEdit>();
        world.CollectEdits(new VoxelCoord(-5000, 0, -5000), new VoxelCoord(5000, 0, 5000), edits);
        return edits;
    }

    private static void AssertSameObjects(Scene expected, Scene actual)
    {
        var a = new List<ObjectPlacement>();
        var b = new List<ObjectPlacement>();
        Assert.True(expected.Objects.Near(-2000, 2000, -2000, 2000, a));
        Assert.True(actual.Objects.Near(-2000, 2000, -2000, 2000, b));
        Assert.Equal(a, b);
    }

    private static string Snapshot(DamagedObject? damaged)
    {
        Assert.NotNull(damaged);
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

    private Scene Make(bool fill = true, bool generated = false, bool copied = false, Stream? load = null)
    {
        var scene = new Scene(generated, copied, load);
        _scenes.Add(scene);
        if (fill)
        {
            scene.Fill();
        }

        return scene;
    }

    /// <summary>
    /// A world of flat stone on 10 cm voxels with a 1 m block of 5 cm voxels to place (its anchor its bottom's middle),
    /// placing nothing itself, or a rock (with <c>generated</c>); its objects, models, damage, and rays in a match's
    /// order; with <c>copied</c> four levels, and a kind wide enough to be copied from level 2 up.
    /// </summary>
    private sealed class Scene : IDisposable
    {
        public Scene(bool generated, bool copied, Stream? load)
        {
            Kinds = new VoxelObjectKinds();
            // Never copied: its copies' limit is finer than any level's voxels.
            Block = Register(
                "block",
                new VoxelCoord(20, 20, 20),
                new VoxelCoord(50, 0, 50),
                s => s.FillBox(0, 0, 0, 100, 100, 100, Stone),
                copiesUpTo: 1
            );
            Copied = Register(
                "copied",
                new VoxelCoord(40, 4, 40),
                new VoxelCoord(100, 0, 100),
                s => s.FillBox(0, 0, 0, 200, 20, 200, Stone),
                copiesUpTo: copied ? 100 : 0
            );
            Generated = generated ? [new ObjectPlacement(new ObjectId(7), Block, new VoxelCoord(300, 0, 300), 0)] : [];
            IChunkGenerator generator = generated
                ? new PlacingGenerator([.. Generated])
                : new FlatTestGenerator(PlacingGenerator.TenCentimetres);
            World = new VoxelWorld(
                new VoxelSettings
                {
                    StreamingRadiusMetres = 0,
                    Levels = copied ? 4 : 1,
                    MemoryBudgetMegabytes = 256,
                    WorkerThreads = 2,
                    Seed = 17,
                },
                0,
                generator,
                NullLogger<VoxelWorld>.Instance,
                kinds: Kinds,
                objectHandOverLevel: copied ? 2 : 1
            );
            World.Initialize();
            if (load is not null)
            {
                World.LoadEdits(load);
            }

            World.SetCenters([Vector3.Zero]);
            TestWorlds.WaitUntilLoaded(World);
            Models = new VoxelModels(World);
            var options = Options.Create(
                new EngineOptions { Objects = new ObjectSettings { HandOverLevel = copied ? 2 : 1 } }
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
            Rays = new ObjectRays(Objects, Kinds, World, Damage);
            KindModels.Initialize();
            Objects.Initialize();
            Damage.Initialize();
        }

        public VoxelObjectKinds Kinds { get; }

        public VoxelObjectKindId Block { get; }

        public VoxelObjectKindId Copied { get; }

        public ObjectPlacement[] Generated { get; }

        public VoxelWorld World { get; }

        public VoxelModels Models { get; }

        public ObjectWorld Objects { get; }

        public VoxelObjects KindModels { get; }

        public ObjectDamage Damage { get; }

        public ObjectRays Rays { get; }

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
            VoxelCoord size,
            VoxelCoord anchor,
            Action<VoxelObjectShape> build,
            int copiesUpTo = 0
        )
        {
            var shape = new VoxelObjectShape(5, size, anchor);
            build(shape);
            return Kinds.Register(name, shape, copiesUpTo);
        }
    }
}
