using System.Numerics;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The levels of the world's meshes, each test in a world of its own: which level a column is meshed at, how the view
/// and the heap choose columns, the stitched seams' columns, and the sky field.
/// </summary>
[RunsLong]
public sealed class WorldMeshesLevelsTests(ITestOutputHelper output) : WorldMeshesTestBase(output)
{
    /// <summary>
    /// The far cascade's pass chooses its columns one level coarser than the camera's (<c>FarShadowCutShare</c>) and is
    /// not balanced: only the camera's stitched seams need neighbours within a level, and balancing the far pass split its
    /// columns back to the camera's. On four levels, with the camera off the columns' corners (a camera on one touches
    /// a column of every level, which always splits), it chooses at most three quarters as many columns as the camera's
    /// pass: 52 against 79 (balanced, it chose 73).
    /// </summary>
    [Fact]
    public void The_far_cascades_pass_chooses_coarser_columns_than_the_cameras()
    {
        var at = new Vector3(3.3f, SlopeGenerator.Height(330, 190, 17) / 100f + 0.3f, 1.9f);
        using var world = TestWorlds.Load(new SlopeGenerator(), at, radiusMetres: RadiusMetres, levels: 4);
        using var targets = new FakeWorldTargets();
        using var meshes = NewMeshes(targets, levels: 4);
        meshes.BeginMatch(world, new VoxelModels(world), new NoVoxelTintMap());
        var view = LookingDown(at, AboveSpread);
        view.SunDirection = ViewConstants.DefaultSunDirection;
        view.Switches = ViewSwitches.Shadows;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var frame = 0;
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
            world.Update(0);
            var slot = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
            Thread.Sleep(1);
        } while (!meshes.IsUpToDate);

        Output.WriteLine($"camera {meshes.LastCameraColumns} columns, far cascade {meshes.LastFarColumns}");
        Assert.True(meshes.LastFarColumns > 0);
        Assert.True(4 * meshes.LastFarColumns <= 3 * meshes.LastCameraColumns);
    }

    /// <summary>
    /// A stone box set 40 m up, flush against the first voxels of the column past the middle one's +x side, changes only
    /// that column; the middle column, whose blocks own the faces between its voxels and the box's, is made again with
    /// blocks for that layer.
    /// </summary>
    [Fact]
    public void A_box_flush_with_the_next_column_gives_this_one_blocks_for_its_layer()
    {
        var grid = new VoxelGrid(10);
        var at = new Vector3(6.4f, 2, 0.64f);
        using var world = TestWorlds.Load(new Voxels.BoundaryGenerator(grid), at, radiusMetres: 30);
        using var targets = new FakeWorldTargets();
        using var meshes = NewMeshes(targets, levels: 1);
        meshes.BeginMatch(world, new VoxelModels(world), new NoVoxelTintMap());
        var view = LookingDown(at);
        var middle = new ColumnKey(0, 0, 0);
        var layer = (grid.ToVoxel(4_000) >> VoxelConstants.ChunkShift) - grid.MinChunkY;
        var frame = 0;
        void Step()
        {
            world.Update(0);
            var slot = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
        }

        for (var i = 0; i < 5; i++)
        {
            Step();
        }

        Assert.False(meshes.HasBlocks(middle, layer));

        world.Apply(
            VoxelEdit.Box(new VoxelCoord(1_280, 4_000, 0), new VoxelCoord(100, 100, 100), VoxelEditOperation.Set, 3)
        );
        Step();

        Assert.True(meshes.HasBlocks(middle, layer));
    }

    /// <summary>
    /// A column's distance counts its height (its blocks' layers round its ground): over a heightfield, from 60 m up no
    /// level-0 block is drawn under the camera, where from 2.5 m over the ground they are, the same columns across.
    /// </summary>
    [Fact]
    public void A_camera_high_over_the_ground_draws_no_fine_blocks_under_it()
    {
        var ground = (float)Voxels.Generation.HillsGenerator.Height(64, 64, 17) / 100;
        var low = new Vector3(0.64f, ground + 2.5f, 0.64f);
        var high = low with { Y = ground + 60 };
        using var world = TestWorlds.Load(
            new Voxels.Generation.HillsGenerator(VoxelGrid.Unit, features: false),
            low,
            radiusMetres: RadiusMetres,
            levels: Levels
        );
        using var targets = new FakeWorldTargets();
        using var meshes = NewMeshes(targets);
        meshes.BeginMatch(world, new VoxelModels(world), new NoVoxelTintMap());

        var (lowFine, _) = Settled(low);
        var (highFine, highAll) = Settled(high);

        Assert.True(lowFine > 0, "Nothing at level 0 was drawn from 2.5 m.");
        Assert.Equal(0, highFine);
        Assert.True(highAll > 0, "Nothing was drawn from 60 m.");

        // Frames from <at> until the meshes are up to date: the level-0 blocks the last drew, and all it drew.
        (int Fine, int All) Settled(Vector3 at)
        {
            var view = LookingDown(at);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var frame = 0;
            int slot;
            do
            {
                Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
                world.Update(0);
                slot = frame++ % FrameSync.FramesInFlight;
                targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
                Thread.Sleep(1);
            } while (frame < 3 || !meshes.IsUpToDate);

            var fine = 0;
            for (var i = 0; i < meshes.DrawCount; i++)
            {
                fine +=
                    targets.DrawsOf(slot)[RecordOf(targets.CommandsOf(slot, 0)[i], slot)].VoxelMetres == 0.01f ? 1 : 0;
            }

            return (fine, meshes.DrawCount);
        }
    }

    /// <summary>
    /// The columns of a heightfield store only the layers round their ground: a stone ball set 60 m up, far over them,
    /// widens its column (the world posts it) and its blocks are made, meshed, and drawn, seen from 30 m up.
    /// </summary>
    [Fact]
    public void A_ball_set_far_over_the_ground_widens_its_column_and_is_drawn()
    {
        var at = new Vector3(0.64f, 70, 0.64f);
        using var world = TestWorlds.Load(
            new Voxels.Generation.HillsGenerator(VoxelGrid.Unit, features: false),
            at,
            radiusMetres: RadiusMetres,
            levels: Levels
        );
        using var targets = new FakeWorldTargets();
        using var meshes = NewMeshes(targets);
        meshes.BeginMatch(world, new VoxelModels(world), new NoVoxelTintMap());
        var view = LookingDown(at);
        var key = new ColumnKey(0, 0, 0);
        Assert.True(world.TryGetStoredLayers(key, out var first, out var end));
        var ballLayer = 6_000 / 128 - VoxelGrid.Unit.MinChunkY;
        Assert.True(end <= ballLayer, $"The column stores layers {first} to {end}, the ball's {ballLayer} among them.");

        world.Apply(VoxelEdit.Sphere(new VoxelCoord(64, 6_000, 64), 40, VoxelEditOperation.Set, 3));

        Assert.True(world.TryGetStoredLayers(key, out first, out end));
        Assert.InRange(ballLayer, first, end - 1);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var frame = 0;
        var drawn = false;
        while (!drawn)
        {
            Assert.True(DateTime.UtcNow < deadline, "The ball was not drawn.");
            world.Update(0);
            var slot = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
            for (var i = 0; i < meshes.DrawCount && !drawn; i++)
            {
                var draw = targets.DrawsOf(slot)[RecordOf(targets.CommandsOf(slot, 0)[i], slot)];
                // A block whose 32 cm reach from its corner holds the ball's middle at 60 m.
                drawn = draw.Origin.Y <= 60 && draw.Origin.Y + 32 * draw.VoxelMetres > 60;
            }

            Thread.Sleep(1);
        }

        // Blocks for the layers round the ground and round the ball (two runs), not the 45 or so from one to the other.
        Assert.True(world.TryGetStoredLayers(key, out first, out end));
        Output.WriteLine($"{meshes.BlockLayers(key)} layers with blocks of {end - first} stored");
        Assert.InRange(meshes.BlockLayers(key), 4, (end - first) / 3);
    }

    /// <summary>
    /// A map 3 level-0 columns wide each way (not a whole number of level-1 columns) cuts its level-1 edge columns in
    /// half; their children past the edge are never loaded, and draw nothing, so the edge column under the camera still
    /// splits and its in-map half is drawn from level-0 blocks.
    /// </summary>
    [Fact]
    public void A_column_cut_by_the_maps_edge_still_splits()
    {
        var at = new Vector3(3.2f, SlopeGenerator.Height(320, 64, 17) / 100f + 1.5f, 0.64f);
        // The slope numbers its layers by VoxelGrid.Unit's floor, so the floor and top stay Unit's.
        var grid = new VoxelGrid(1, 3, VoxelGrid.Unit.MinChunkY, VoxelGrid.Unit.MaxChunkY);
        Assert.Equal((2, 1), (grid.MaxChunkXZ, grid.MaxChunkXZAt(1)));

        var level0 = FinestDrawnAround(new SizedGenerator(new SlopeGenerator(), grid), at, maxDistance: 100);

        Assert.True(level0 > 0, "Nothing at level 0 was drawn in the edge column under the camera.");
    }

    /// <summary>
    /// A view that ends before the far children of the column under the camera (one of its four level-0 columns lies
    /// past it, so it is never meshed) still splits that column and draws its near children from level-0 blocks.
    /// </summary>
    [Fact]
    public void A_column_wider_than_the_view_still_splits()
    {
        // In level-0 column (0, 0) of level-1 column (0, 0): its level-0 column (1, 1) is 0.9 m away at the nearest.
        var at = new Vector3(0.64f, SlopeGenerator.Height(64, 64, 17) / 100f + 0.3f, 0.64f);

        var level0 = FinestDrawnAround(new SlopeGenerator(), at, maxDistance: 0.85f);

        Assert.True(level0 > 0, "Nothing at level 0 was drawn under the camera.");
    }

    /// <summary>
    /// Loads a two-level world of <paramref name="generator"/> round <paramref name="at"/>, meshes it seen from there
    /// with a view of <paramref name="maxDistance"/>, and counts the level-0 blocks drawn in level-1 column x 1 or 0 the
    /// camera stands over.
    /// </summary>
    private int FinestDrawnAround(IChunkGenerator generator, Vector3 at, float maxDistance)
    {
        using var world = TestWorlds.Load(generator, at, radiusMetres: RadiusMetres, levels: Levels);
        using var targets = new FakeWorldTargets();
        using var meshes = NewMeshes(targets);
        meshes.BeginMatch(world, new VoxelModels(world), new NoVoxelTintMap());
        var view = LookingDown(at);
        view.MaxDistance = maxDistance;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var frame = 0;
        int slot;
        var level0 = 0;
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "No level-0 block was drawn under the camera.");
            world.Update(0);
            slot = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
            level0 = 0;
            for (var i = 0; i < meshes.DrawCount; i++)
            {
                var draw = targets.DrawsOf(slot)[RecordOf(targets.CommandsOf(slot, 0)[i], slot)];
                var column = (int)MathF.Floor(draw.Origin.X / 2.56f);
                level0 += draw.VoxelMetres == 0.01f && column == (int)MathF.Floor(at.X / 2.56f) ? 1 : 0;
            }

            Thread.Sleep(1);
        } while (level0 == 0 && frame < 3000);

        return level0;
    }

    /// <summary>The inner generator's world on another grid of the same voxel size: smaller bounds cut it.</summary>
    private sealed class SizedGenerator(IChunkGenerator inner, VoxelGrid grid) : IChunkGenerator
    {
        public VoxelGrid Grid => grid;

        public string Id => inner.Id;

        public int Version => inner.Version;

        public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) =>
            inner.DescribeColumn(column, seed, layers);

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) => inner.Fill(chunk, seed, voxels);

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels, Span<byte> densities) =>
            inner.Fill(chunk, seed, voxels, densities);
    }

    /// <summary>
    /// With level 5 among the world's levels the sky field is built round the view on a worker and drawn from: the
    /// lighting record names it, and the air over the slope sees all the sky in the heap's copy.
    /// </summary>
    [Fact]
    public void A_world_with_coarse_levels_gets_a_sky_field()
    {
        const int levels = 6;
        using var world = TestWorlds.Load(new SlopeGenerator(), Centre, radiusMetres: 2, levels: levels);
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = 2, Levels = levels },
            Renderer = new RendererSettings { MeshFinestLevel = 3 },
        };
        using var targets = new FakeWorldTargets();
        using var meshes = new WorldMeshes(
            Palette,
            targets,
            new MeshView(targets, Options.Create(options)),
            targets,
            Options.Create(options),
            NullLogger<WorldMeshes>.Instance
        );
        meshes.Initialize();
        meshes.BeginMatch(world, new VoxelModels(world), new NoVoxelTintMap());
        var view = LookingDown(Centre);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        var frame = 0;
        while (!meshes.HasSkyField)
        {
            Assert.True(DateTime.UtcNow < deadline, "No sky field was built.");
            var at = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(at, Frame(meshes, at, frame / 60.0, in view)));
            Thread.Sleep(1);
        }

        Assert.True(meshes.Frame.SkyUploaded);
        var firstUpload = meshes.Frame.SkyUploadOffset;

        var slot = frame++ % FrameSync.FramesInFlight;
        targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
        var lighting = targets.LightingOf(slot);
        Assert.Equal(SkyField.CellMetresFor(VoxelGrid.Unit), lighting.SkyCellMetres);
        Assert.False(meshes.Frame.SkyUploaded);
        // The frame that drew it first staged it for the image: the cell 2 m above the camera, straight up from the
        // slope, sees all the sky.
        var cell = (Centre + new Vector3(0, 2, 0) - lighting.SkyCorner) / lighting.SkyCellMetres;
        var index = (int)cell.X + SkyField.SizeXZ * ((int)cell.Y + SkyField.SizeY * (int)cell.Z);
        Assert.Equal(255, targets.Staged(firstUpload, index));
    }

    /// <summary>
    /// The field is built again after a climb of 20 m, under the 32 m that builds it again in any direction (the box is
    /// 64 m tall, so a roof 20 m up would leave its top), and not after 20 m across.
    /// </summary>
    [Fact]
    public void Climbing_builds_the_sky_field_again_sooner_than_moving_across()
    {
        const int levels = 6;
        using var world = TestWorlds.Load(new SlopeGenerator(), Centre, radiusMetres: 2, levels: levels);
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = 2, Levels = levels },
            Renderer = new RendererSettings { MeshFinestLevel = 3 },
        };
        using var targets = new FakeWorldTargets();
        using var meshes = new WorldMeshes(
            Palette,
            targets,
            new MeshView(targets, Options.Create(options)),
            targets,
            Options.Create(options),
            NullLogger<WorldMeshes>.Instance
        );
        meshes.Initialize();
        meshes.BeginMatch(world, new VoxelModels(world), new NoVoxelTintMap());
        var frame = 0;
        Assert.True(SkyBuiltWithin(meshes, targets, LookingDown(Centre), ref frame, 3000), "No first field.");
        var raised = Centre + new Vector3(0, 20, 0);

        Assert.True(SkyBuiltWithin(meshes, targets, LookingDown(raised), ref frame, 3000), "No field after the climb.");
        Assert.False(
            SkyBuiltWithin(meshes, targets, LookingDown(raised + new Vector3(20, 0, 0)), ref frame, 400),
            "A field after 20 m across."
        );
    }

    /// <summary>Uploads frames from <paramref name="view"/> until one stages a new sky field, at most <paramref name="frames"/>.</summary>
    private static bool SkyBuiltWithin(
        WorldMeshes meshes,
        FakeWorldTargets targets,
        ViewConstants view,
        ref int frame,
        int frames
    )
    {
        for (var i = 0; i < frames; i++)
        {
            var slot = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
            if (meshes.Frame.SkyUploaded)
            {
                return true;
            }

            // The flood runs on a worker.
            Thread.Sleep(2);
        }

        return false;
    }

    /// <summary>
    /// A heap with room for what the view draws (with shadows) and a quarter more, but not for every column it meshes:
    /// the meshes of columns the view does not draw are freed to make room, so every block it draws has its mesh, as with
    /// a heap of plenty; with three levels, columns between the coarsest and the drawn ones are freed too and the view
    /// still splits through them; moved, the view finds the columns it needs again meshed again.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void A_full_heap_frees_the_columns_the_view_does_not_draw(int levels)
    {
        var moved = Above + new Vector3(2.6f, 0, 1.3f);
        using var world = TestWorlds.Load(new SlopeGenerator(), Centre, radiusMetres: RadiusMetres, levels: levels);
        var models = new VoxelModels(world);

        // The heap of plenty: what the view draws, and what it does not.
        using var plenty = new FakeWorldTargets();
        using var reference = NewMeshes(plenty, levels);
        reference.BeginMatch(world, models, new NoVoxelTintMap());
        var frame = 0;
        void Step(WorldMeshes meshes, FakeWorldTargets targets, Vector3 at)
        {
            var slot = frame++ % FrameSync.FramesInFlight;
            var view = LookingDown(at, AboveSpread);
            view.SunDirection = ViewConstants.DefaultSunDirection;
            view.Switches = ViewSwitches.Shadows;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
        }

        void Settle(WorldMeshes meshes, FakeWorldTargets targets, Vector3 at)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            do
            {
                Assert.True(DateTime.UtcNow < deadline, "The meshes did not come up to date.");
                world.Update(0);
                Step(meshes, targets, at);
                Thread.Sleep(1);
            } while (!meshes.IsUpToDate);

            for (var i = 0; i < 30; i++)
            {
                Step(meshes, targets, at);
            }
        }

        Settle(reference, plenty, Above);
        var used = reference.HeapUsedBytes;
        var undrawn = reference.UndrawnBytes();
        var draws = (reference.DrawCount, reference.Frame.NearShadows, reference.Frame.FarShadows);
        Settle(reference, plenty, moved);
        var movedDraws = (reference.DrawCount, reference.Frame.NearShadows, reference.Frame.FarShadows);
        var fixedBytes = (long)(MeshPass.TintMapOffset + MeshPass.TintMapBytes);
        var drawn = used - fixedBytes - undrawn;
        Output.WriteLine(
            $"{used} bytes used, {undrawn} of them in columns not drawn; draws {draws}, moved {movedDraws}"
        );
        Assert.True(
            undrawn > drawn / 4,
            $"Only {undrawn} bytes the view does not draw: the test sees nothing to free."
        );

        // A heap with room for what the view draws and a quarter more.
        using var targets = new FakeWorldTargets((int)(fixedBytes + drawn + drawn / 4));
        using var meshes = NewMeshes(targets, levels);
        meshes.BeginMatch(world, models, new NoVoxelTintMap());
        Settle(meshes, targets, Above);
        Output.WriteLine($"{meshes.Evictions} columns freed, {meshes.StarvedBlocks} blocks without room");
        Assert.True(meshes.Evictions > 0);
        Assert.Equal(0, meshes.StarvedBlocks);
        Assert.Equal(draws, (meshes.DrawCount, meshes.Frame.NearShadows, meshes.Frame.FarShadows));

        // Everything the view does not need freed at once: the frames after draw the same, no column dropping back to
        // its coarser one while one between it and the drawn ones is meshed again.
        var before = meshes.Evictions;
        meshes.EvictForTests(1 << 28);
        Assert.True(meshes.Evictions > before || levels == 2);
        for (var i = 0; i < 10; i++)
        {
            world.Update(0);
            Step(meshes, targets, Above);
            Assert.Equal(draws, (meshes.DrawCount, meshes.Frame.NearShadows, meshes.Frame.FarShadows));
        }

        Settle(meshes, targets, moved);
        Output.WriteLine(
            $"moved: {meshes.Evictions} columns freed, {meshes.EvictionsWoken} meshed again, {meshes.StarvedBlocks} blocks without room"
        );
        Assert.Equal(0, meshes.StarvedBlocks);
        Assert.Equal(movedDraws, (meshes.DrawCount, meshes.Frame.NearShadows, meshes.Frame.FarShadows));
        Assert.True(meshes.EvictionsWoken > 0);
    }
}
