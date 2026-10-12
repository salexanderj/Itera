using System.Numerics;
using System.Runtime.InteropServices;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Silk.NET.Vulkan;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The raster pipeline's world on the GPU, over a draw target and staging in memory: it meshes the slope world's
/// columns, uploads the meshes and the materials where the mesh pass reads them, draws the blocks in view, meshes an
/// edit again, follows columns that load and unload, does the uploader's other jobs for the models, and allocates
/// nothing per frame once up to date.
/// </summary>
[RunsLong]
public sealed partial class WorldMeshesTests : IDisposable
{
    private const double RadiusMetres = 4;
    private const int Levels = 2;
    private static readonly Vector3 Centre = new(0, 2.5f, 0);

    private readonly VoxelWorld _world;
    private readonly VoxelModels _models;
    private readonly CountingPalette _palette = new();
    private readonly FakeTargets _targets = new();
    private readonly WorldMeshes _meshes;
    private readonly ITestOutputHelper _output;
    private int _frame;

    public WorldMeshesTests(ITestOutputHelper output)
    {
        _output = output;
        _world = TestWorlds.Load(new SlopeGenerator(), Centre, radiusMetres: RadiusMetres, levels: Levels);
        _models = new VoxelModels(_world);
        _meshes = NewMeshes(_targets);
    }

    private WorldMeshes NewMeshes(FakeTargets targets, int levels = Levels, float distantShadowMetres = 0)
    {
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = RadiusMetres, Levels = levels },
            Renderer = new RendererSettings { MeshFinestLevel = 0, DistantShadowMetres = distantShadowMetres },
        };
        var meshes = new WorldMeshes(
            _palette,
            targets,
            new MeshView(targets, Options.Create(options)),
            targets,
            Options.Create(options),
            NullLogger<WorldMeshes>.Instance
        );
        meshes.Initialize();
        return meshes;
    }

    /// <summary>
    /// A frame as GameLoop builds it: the view's start, the world's upload and draws, then the view's end with the
    /// events' <paramref name="lights"/>. Returns the copy regions staged.
    /// </summary>
    private static int Frame(
        WorldMeshes meshes,
        int slot,
        double nowSeconds,
        in ViewConstants view,
        ReadOnlySpan<ModelDraw> models = default,
        ReadOnlySpan<PointLight> lights = default
    )
    {
        meshes.View.Begin(slot, in view);
        var regions = meshes.Upload(slot, nowSeconds, in view, models);
        meshes.View.End(slot, lights);
        return regions;
    }

    public void Dispose()
    {
        _meshes.Dispose();
        _targets.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void Initialize_prepares_the_palette_once()
    {
        Assert.Equal(1, _palette.Prepared);
    }

    [Fact]
    public void Without_a_match_an_upload_throws_and_the_world_is_not_up_to_date()
    {
        Assert.Throws<InvalidOperationException>(() => _meshes.Upload(0, 0, default));

        Begin();
        RunUntilUpToDate();
        _meshes.EndMatch();

        Assert.False(_meshes.IsUpToDate);
        Assert.Throws<InvalidOperationException>(() => _meshes.Upload(0, 0, default));
    }

    /// <summary>
    /// <see cref="WorldMeshes.SetTexture"/> refuses air and a set that was not loaded, and a change goes to the GPU's
    /// materials in the next frame.
    /// </summary>
    [Fact]
    public void A_materials_texture_set_changed_for_the_run_reaches_the_gpu_next_frame()
    {
        Begin();
        RunUntilUpToDate();
        Frame();
        Assert.False(_meshes.SetTexture(0, null, 0));
        Assert.False(_meshes.SetTexture(1, "no-such-set", 0));
        Assert.Empty(_meshes.TextureSetNames);
        var heapMaterials = MemoryMarshal.Cast<byte, MeshMaterial>(
            _targets.Heap.AsSpan((int)MeshPass.MaterialsOffset, (int)MeshPass.MaterialsBytes)
        );
        heapMaterials[1].Glow = 0.75f;

        Assert.True(_meshes.SetTexture(1, null, 0.5f));
        Frame();

        Assert.Equal(_palette.Look(1).Glow, heapMaterials[1].Glow);
        Assert.Equal(0u, heapMaterials[1].Texture);
    }

    [Fact]
    public void The_ground_is_meshed_uploaded_and_drawn_with_the_materials_in_place()
    {
        Begin();
        RunUntilUpToDate();
        var slot = Frame();

        Assert.True(_meshes.DrawCount > 0);
        var materials = MemoryMarshal.Cast<byte, MeshMaterial>(
            _targets.Heap.AsSpan((int)MeshPass.MaterialsOffset, (int)MeshPass.MaterialsBytes)
        );
        Assert.Equal(_palette.Look(1).Colour, materials[1].Colour);
        Assert.Equal(_palette.Look(1).PackLook(), materials[1].Look);
        Assert.Equal(_palette.Look(1).PackAmounts(), materials[1].Amounts);
        var words = MemoryMarshal.Cast<byte, uint>(_targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(_targets.Heap.AsSpan());
        for (var i = 0; i < _meshes.DrawCount; i++)
        {
            var command = _targets.CommandsOf(slot, 0)[i];
            var draw = _targets.DrawsOf(slot)[RecordOf(command, slot)];
            Assert.True(command.IndexCount > 0 && command.IndexCount % 3 == 0);
            Assert.Equal(1u, command.InstanceCount);
            // Level 0 near the view, level 1 farther (a 4 m radius draws level 0 to 1.44 m).
            Assert.Contains(draw.VoxelMetres, (float[])[0.01f, 0.02f]);
            for (var k = 0; k < command.IndexCount; k++)
            {
                var index = indices[(int)command.FirstIndex + k];
                var at = (int)draw.VertexBase + MeshVertex.Words * index;
                var vertex = new MeshVertex
                {
                    PositionXY = words[at],
                    PositionZMaterial = words[at + 1],
                    Normal = words[at + 2],
                };
                var position = MeshVertex.DecodePosition(in vertex);
                Assert.InRange(position.X, -0.5f, 32.5f);
                Assert.InRange(position.Y, -0.5f, 32.5f);
                Assert.InRange(position.Z, -0.5f, 32.5f);
                Assert.NotEqual(0, MeshVertex.DecodeMaterial(in vertex));
                // The slope rises gently: its normals point up.
                Assert.True(MeshVertex.DecodeNormal(in vertex).Y > 0.5f);
            }
        }
    }

    /// <summary>
    /// The camera's blocks are drawn nearest first, whatever order their columns loaded and their meshes arrived in, so
    /// the depth test rejects what they hide before it is shaded.
    /// </summary>
    [Fact]
    public void The_camera_draws_its_blocks_nearest_first()
    {
        Begin();
        RunUntilUpToDate();
        var at = Centre + new Vector3(1.3f, 0, -0.7f);
        var slot = Frame(at);

        Assert.True(_meshes.DrawCount > 4);
        var last = 0f;
        for (var i = 0; i < _meshes.DrawCount; i++)
        {
            var draw = _targets.DrawsOf(slot)[RecordOf(_targets.CommandsOf(slot, 0)[i], slot)];
            var centre = draw.Origin + new Vector3(16 * draw.VoxelMetres);
            var distance = Vector3.DistanceSquared(centre, at);
            Assert.True(distance >= last, $"Draw {i} is nearer than the one before it.");
            last = distance;
        }
    }

    /// <summary>
    /// With the match's origin moved 300 m, 20 m down, and 512 m back, a view at the same place in the world draws the
    /// blocks it draws with the origin at the middle, in the same order and from the same meshes, each block's corner
    /// local to the origin (less it, to float rounding), and the sky field's corner with them.
    /// </summary>
    [Fact]
    public void With_the_origin_moved_the_same_blocks_are_drawn_local_to_it()
    {
        Begin();
        RunUntilUpToDate();
        var at = Centre + new Vector3(1.3f, 0, -0.7f);
        var first = Frame(at, shadows: true);
        var middle = Drawn(first);
        var sky = _targets.LightingOf(first).SkyCorner;
        var origin = new VoxelCoord(30_000, -2_000, -51_200);
        var by = new Vector3(300, -20, -512);

        var slot = Frame(at - by, shadows: true, origin: origin);
        var moved = Drawn(slot);

        Assert.True(middle.Count > 4);
        Assert.Equal(middle.Count, moved.Count);
        for (var i = 0; i < middle.Count; i++)
        {
            Assert.Equal(
                (middle[i].VoxelMetres, middle[i].VertexBase, middle[i].Command),
                (moved[i].VoxelMetres, moved[i].VertexBase, moved[i].Command)
            );
            Assert.True(
                Vector3.Distance(middle[i].Origin - by, moved[i].Origin) < 1e-4f,
                $"Draw {i} at {moved[i].Origin}; at the middle {middle[i].Origin}."
            );
        }

        if (_meshes.HasSkyField)
        {
            Assert.True(Vector3.Distance(sky - by, _targets.LightingOf(slot).SkyCorner) < 1e-4f);
        }
    }

    /// <summary>
    /// A material's tile phase is the origin modulo the tile's width on each axis, in doubles, for any width to the
    /// millimetre (a whole number of tiles from the origin, to a micrometre); and the frame stages the materials again
    /// with their phases for a moved origin (a material without a set, the 3 m width the console's materials all draws).
    /// </summary>
    [Fact]
    public void The_materials_tile_phases_follow_the_origin()
    {
        Assert.Equal(
            new Vector3(1.92f, 0.48f, 1.23f),
            MeshMaterial.TilePhase(new VoxelCoord(15_000_000, -2_000, 123), 2.56)
        );
        Assert.Equal(Vector3.Zero, MeshMaterial.TilePhase(default, 2.56));
        var random = new Random(3);
        for (var i = 0; i < 1000; i++)
        {
            var origin = new VoxelCoord(
                random.Next(-160_000, 160_000) * 100,
                random.Next(-1_000, 1_000) * 100,
                random.Next(-160_000, 160_000) * 100
            );
            var width = random.Next(100, 10_000) / 1000.0;
            var phase = MeshMaterial.TilePhase(origin, width);
            foreach (var (centimetres, part) in new[] { (origin.X, phase.X), (origin.Y, phase.Y), (origin.Z, phase.Z) })
            {
                Assert.InRange(part, 0, width);
                var tiles = (centimetres / 100.0 - part) / width;
                Assert.True(
                    Math.Abs(tiles - Math.Round(tiles)) * width < 1e-6 * Math.Max(1, Math.Abs(tiles) / 1e4),
                    $"{part} for {centimetres} cm in {width} m"
                );
            }
        }

        Begin();
        RunUntilUpToDate();
        var origin2 = new VoxelCoord(30_000, -2_000, -51_200);
        Frame(Centre - new Vector3(300, -20, -512), origin: origin2);
        var heapMaterials = MemoryMarshal.Cast<byte, MeshMaterial>(
            _targets.Heap.AsSpan((int)MeshPass.MaterialsOffset, (int)MeshPass.MaterialsBytes)
        );
        Assert.Equal(MeshMaterial.TilePhase(origin2, MeshMaterial.AllMaterialsTileMetres), heapMaterials[1].TileOrigin);
    }

    /// <summary>The camera's draws of <paramref name="slot"/> in their order: each one's record and command.</summary>
    private List<(Vector3 Origin, float VoxelMetres, uint VertexBase, (uint, int, uint) Command)> Drawn(int slot)
    {
        var drawn = new List<(Vector3, float, uint, (uint, int, uint))>();
        for (var i = 0; i < _meshes.DrawCount; i++)
        {
            var command = _targets.CommandsOf(slot, 0)[i];
            var draw = _targets.DrawsOf(slot)[RecordOf(command, slot)];
            drawn.Add(
                (
                    draw.Origin,
                    draw.VoxelMetres,
                    draw.VertexBase,
                    (command.IndexCount, command.VertexOffset, command.FirstIndex)
                )
            );
        }

        return drawn;
    }

    /// <summary>
    /// An image view lists, in its own records, the camera's levels' blocks its own box holds (more than the camera's
    /// narrow look down sees), nearest first by its depth, and the models in it the camera does not see; its lighting
    /// record has the sky field's place as the frame's.
    /// </summary>
    [Fact]
    public void An_image_view_lists_the_blocks_and_models_its_box_holds()
    {
        var cube = AddCube(10);
        Begin();
        RunUntil(() => _models.IsUploaded(cube));
        RunUntilUpToDate();
        var lists = new FakeViewLists();
        var slot = _frame++ % FrameSync.FramesInFlight;
        var view = LookingDown(Centre);
        var middle = Centre + new Vector3(0, 5, 0);
        var viewConstants = FakeViewLists.LookingDown(middle);
        ModelDraw[] models = [new(cube, 0, new Vector3(10, 1.8f, 0), Quaternion.Identity, 0)];

        _meshes.View.Begin(slot, in view);
        lists.View.Begin(slot, in viewConstants);
        _meshes.Upload(slot, _frame / 60.0, in view, models, [lists]);
        _meshes.View.End(slot, default);
        lists.View.End(slot, default);

        ref readonly var frame = ref lists.FrameOf(slot);
        Assert.True(
            frame.Draws > _meshes.DrawCount,
            $"{frame.Draws} drawn in the view, {_meshes.DrawCount} by the camera."
        );
        Assert.Equal(frame.Draws, frame.Records);
        var records = new HashSet<int>();
        var last = float.MinValue;
        for (var i = 0; i < frame.Draws; i++)
        {
            var command = lists.CommandsOf(slot, 0)[i];
            var record = (int)command.FirstInstance - RenderViewTarget.RecordBase(slot);
            Assert.True(records.Add(record));
            var draw = lists.DrawsOf(slot)[record];
            var centre = draw.Origin + new Vector3(16 * draw.VoxelMetres);
            var reach = 16 * draw.VoxelMetres + draw.VoxelMetres;
            Assert.InRange(centre.X, middle.X - 16 - reach, middle.X + 16 + reach);
            Assert.InRange(centre.Z, middle.Z - 9 - reach, middle.Z + 9 + reach);
            var key = lists.View.NearFirstKey(centre);
            Assert.True(key >= last, $"Draw {i} is nearer than the one before it.");
            last = key;
        }

        Assert.Equal(0, _meshes.Frame.ModelCamera.Records);
        Assert.Equal(1, frame.Models.Records);
        Assert.Equal(_targets.LightingOf(slot).SkyCorner, lists.LightingOf(slot).SkyCorner);
        Assert.Equal(_targets.LightingOf(slot).SkyCellMetres, lists.LightingOf(slot).SkyCellMetres);
    }

    [Fact]
    public void An_edit_is_meshed_again()
    {
        Begin();
        RunUntilUpToDate();
        var before = DrawnIndices();

        var ground = SlopeGenerator.Height(0, 0, 17);
        _world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, ground, 0), 60, VoxelEditOperation.Clear));
        _world.Update(0);
        Frame();
        Assert.False(_meshes.IsUpToDate);
        Assert.True(_meshes.EditedBlocks > 0);
        RunUntilUpToDate();

        Assert.NotEqual(before, DrawnIndices());
        // The edits' latency counts the blocks the edit marked, every one of them meshed and staged.
        Assert.Equal(0, _meshes.EditedBlocks);
        Assert.True(_meshes.LastEdit.Blocks > 0 && _meshes.LastEdit.Frames > 0);
    }

    /// <summary>
    /// Blocks meshed as their columns stream in are not edits: the edits' latency does not wait for them, and an edit
    /// made while they load is reported once its own blocks are in.
    /// </summary>
    [Fact]
    public void Streaming_blocks_do_not_count_as_edits()
    {
        Begin();
        Frame();
        Assert.True(_meshes.PendingBlocks > 0);
        Assert.Equal(0, _meshes.EditedBlocks);
        RunUntilUpToDate();
        Assert.Equal(0, _meshes.EditedBlocks);
        Assert.Equal(default, _meshes.LastEdit);

        // Made again from empty, the world loads; an edit made meanwhile is reported.
        Begin();
        Frame();
        Assert.True(_meshes.PendingBlocks > 0);
        _world.Apply(
            VoxelEdit.Sphere(new VoxelCoord(0, SlopeGenerator.Height(0, 0, 17), 0), 30, VoxelEditOperation.Clear)
        );
        _world.Update(0);
        RunUntil(() => _meshes.LastEdit.Blocks > 0);
        Assert.Equal(0, _meshes.EditedBlocks);
        RunUntilUpToDate();

        // An edit is reported once its blocks are in, with every block its changes marked (some come a frame later).
        var ground = SlopeGenerator.Height(0, 0, 17);
        _world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, ground, 0), 30, VoxelEditOperation.Clear));
        _world.Update(0);
        Frame();
        var marked = _meshes.EditedBlocks;
        Assert.True(marked > 0);
        RunUntil(() => _meshes.EditedBlocks == 0);
        Assert.True(_meshes.LastEdit.Blocks >= marked);
    }

    [Fact]
    public void Columns_that_unload_and_load_are_followed()
    {
        Begin();
        RunUntilUpToDate();

        // 1.5 m above the slope there, which rises 1 cm for every 16 cm.
        var away = new Vector3(40, SlopeGenerator.Height(4000, 0, 17) / 100f + 1.5f, 0);
        _world.SetCenters([away]);
        TestWorlds.WaitUntilLoaded(_world);
        RunUntilUpToDate(away);

        var resident = new List<ColumnKey>();
        _world.CollectResidentColumns(resident);
        Assert.Equal(resident.Count, _meshes.ColumnCount);
        Assert.True(_meshes.DrawCount > 0);
    }

    /// <summary>
    /// A job of an ended match that comes back in the next names a column of the same key, which is another column now:
    /// it is dropped, so the counts settle at 0 and the world catches up.
    /// </summary>
    [Fact]
    public void A_new_match_drops_the_jobs_the_last_one_left_with_the_workers()
    {
        for (var i = 0; i < 5; i++)
        {
            Begin();
            Frame();
            Frame();
            _meshes.EndMatch();
        }

        Begin();
        RunUntilUpToDate();

        Assert.Equal(0, _meshes.PendingBlocks);
        Assert.True(_meshes.DrawCount > 0);
    }

    [Fact]
    public void Without_shadows_the_cascades_list_nothing_and_their_record_says_so()
    {
        Begin();
        RunUntilUpToDate();
        var slot = Frame();

        var frame = _meshes.Frame;
        Assert.False(frame.Shadows);
        Assert.Equal(0, frame.NearShadows);
        Assert.Equal(0, frame.FarShadows);
        Assert.Equal(frame.Draws, frame.Records);
        Assert.Equal(0u, _targets.LightingOf(slot).Shadows);
    }

    /// <summary>
    /// Looking straight down with a low sun to the east (+x): blocks beside the view on the sun's side can shade it, so
    /// the near cascade lists them although the camera does not see them; blocks beside the view on the other side
    /// cannot, so it leaves them out. A block in both lists has one draw.
    /// </summary>
    [Fact]
    public void With_shadows_the_near_cascade_lists_the_blocks_beside_the_view_that_can_shade_it()
    {
        Begin();
        RunUntilUpToDate();
        var slot = _frame % FrameSync.FramesInFlight;
        var view = LookingDown(Centre);
        view.SunDirection = Vector3.Normalize(new Vector3(2, 1, 0));
        view.Switches = ViewSwitches.Shadows;
        _frame++;
        _targets.Apply(_meshes.Regions(slot, Frame(_meshes, slot, _frame / 60.0, in view)));

        var frame = _meshes.Frame;
        Assert.True(frame.Shadows);
        Assert.True(frame.Draws > 0);
        Assert.True(frame.NearShadows > 0);
        Assert.InRange(frame.Records, frame.Draws, frame.Draws + frame.NearShadows + frame.FarShadows);
        var seen = new HashSet<int>();
        for (var i = 0; i < frame.Draws; i++)
        {
            Assert.True(seen.Add(RecordOf(_targets.CommandsOf(slot, 0)[i], slot)));
        }

        var nearOnly = 0;
        var listed = new HashSet<int>();
        for (var i = 0; i < frame.NearShadows; i++)
        {
            var command = _targets.CommandsOf(slot, 1)[i];
            var record = RecordOf(command, slot);
            Assert.True(command.IndexCount > 0);
            Assert.InRange(record, 0, frame.Records - 1);
            Assert.True(listed.Add(record));
            if (seen.Contains(record))
            {
                continue;
            }

            // The view meets the ground about 1.5 m either side of the camera.
            var draw = _targets.DrawsOf(slot)[record];
            var centreX = draw.Origin.X + 16 * draw.VoxelMetres;
            Assert.True(centreX > -2.75f, $"A block at x = {centreX} m, away from the sun, cannot shade the view.");
            if (centreX > 1.75f)
            {
                nearOnly++;
            }
        }

        Assert.True(nearOnly > 0);
        ref readonly var shadows = ref _targets.LightingOf(slot);
        Assert.Equal(1u, shadows.Shadows);
        Assert.Equal(MeshView.NearShadowMetres, shadows.NearEnd);
        // The view reaches 100 m, short of the far cascade's 200 m.
        Assert.Equal(100, shadows.FarEnd);
        Assert.True(shadows.NearTexelMetres > 0 && shadows.NearTexelMetres < shadows.FarTexelMetres);
    }

    /// <summary>
    /// With <c>Engine:Renderer:DistantShadowMetres</c>, a view 1 km long, and a sun 10 degrees up behind the camera, the
    /// camera's pass lists the blocks that can shade the distant cascade's slice (the ground round the camera casts
    /// forwards), each with a draw, and the frame says the cascade is drawn; under the engine's sun, 58 degrees up, it
    /// lists none and its record's strength is 0.
    /// </summary>
    [Fact]
    public void Under_a_low_sun_the_cameras_pass_lists_the_distant_cascades_blocks()
    {
        using var targets = new FakeTargets();
        using var meshes = NewMeshes(targets, distantShadowMetres: 2000);
        meshes.BeginMatch(_world, _models, new NoVoxelTintMap());
        var view = new ViewConstants
        {
            Position = Centre,
            Forward = new Vector3(1, 0, 0),
            Right = new Vector3(0, 0, 1),
            Up = new Vector3(0, 1, 0),
            MaxDistance = 1000,
            SunDirection = SunPath.FromAngles(270, 10),
            Switches = ViewSwitches.Shadows,
        };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var frame = 0;
        int slot;
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
            _world.Update(0);
            slot = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
            Thread.Sleep(1);
        } while (!meshes.IsUpToDate);

        // One frame more, of every block meshed.
        slot = frame++ % FrameSync.FramesInFlight;
        targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
        var drawn = meshes.Frame;
        Assert.True(drawn.Distant);
        Assert.True(drawn.DistantShadows > 0);
        Assert.InRange(
            drawn.Records,
            drawn.Draws,
            drawn.Draws + drawn.NearShadows + drawn.FarShadows + drawn.DistantShadows
        );
        for (var i = 0; i < drawn.DistantShadows; i++)
        {
            var command = targets.CommandsOf(slot, MeshPass.DistantList)[i];
            Assert.True(command.IndexCount > 0);
            Assert.InRange(RecordOf(command, slot), 0, drawn.Records - 1);
        }

        Assert.Equal(1, targets.LightingOf(slot).DistantStrength);
        Assert.Equal(1000, targets.LightingOf(slot).DistantEnd);

        view.SunDirection = ViewConstants.DefaultSunDirection;
        slot = frame++ % FrameSync.FramesInFlight;
        targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));

        Assert.False(meshes.Frame.Distant);
        Assert.Equal(0, meshes.Frame.DistantShadows);
        Assert.Equal(0, targets.LightingOf(slot).DistantStrength);
    }

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
        using var targets = new FakeTargets();
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

        _output.WriteLine($"camera {meshes.LastCameraColumns} columns, far cascade {meshes.LastFarColumns}");
        Assert.True(meshes.LastFarColumns > 0);
        Assert.True(4 * meshes.LastFarColumns <= 3 * meshes.LastCameraColumns);
    }

    [Fact]
    public void Models_count_as_uploaded_once_meshed_and_retired_ones_go_once_the_world_is_meshed()
    {
        var first = AddCube(10);
        var second = AddCube(20);
        Begin();
        Assert.False(_models.IsUploaded(first));

        RunUntil(() => _models.IsUploaded(first) && _models.IsUploaded(second));
        RunUntilUpToDate();
        _models.Retire(second, 0, Vector3.Zero, Quaternion.Identity, 0);
        Frame();

        Assert.Equal(1, _models.Count);
        Assert.True(_models.Retired.IsEmpty);
    }

    /// <summary>
    /// Without voxel terrain the meshes follow the match's models alone: a model is meshed and counts as uploaded, a draw
    /// of it goes into the camera's and the cascades' lists, no world column is followed or drawn, the upload is up to
    /// date, and a retired model goes at the next frame.
    /// </summary>
    [Fact]
    public void Without_terrain_the_models_alone_are_meshed_and_drawn()
    {
        var targets = new FakeTargets();
        using var meshes = NewMeshes(targets);
        using var models = new VoxelModels();
        var cube = models.Add([models.Encode(CubeVoxels(10))], new VoxelCoord(10, 10, 10));
        var other = models.Add([models.Encode(CubeVoxels(20))], new VoxelCoord(20, 20, 20));
        meshes.BeginMatch(null, models, new NoVoxelTintMap());
        var view = LookingDown(Centre);
        view.SunDirection = ViewConstants.DefaultSunDirection;
        view.Switches = ViewSwitches.Shadows;
        ModelDraw[] draws = [new(cube, 0, new Vector3(0.3f, 1.8f, 0), Quaternion.Identity, 0)];
        var frame = 0;
        void Next()
        {
            var slot = frame++ % FrameSync.FramesInFlight;
            var count = Frame(meshes, slot, frame / 60.0, in view, draws);
            targets.Apply(meshes.Regions(slot, count));
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The models were not meshed.");
            Next();
            Thread.Sleep(1);
        } while (!(models.IsUploaded(cube) && models.IsUploaded(other) && meshes.IsUpToDate));

        Next();
        models.Retire(other, 0, Vector3.Zero, Quaternion.Identity, 0);
        Next();

        Assert.Equal(0, meshes.ColumnCount);
        Assert.Equal(0, meshes.DrawCount);
        Assert.Equal(1, meshes.Frame.ModelCamera.Records);
        Assert.True(meshes.Frame.ModelNear.Records > 0);
        Assert.Equal(1, models.Count);
        Assert.True(models.Retired.IsEmpty);
    }

    /// <summary>
    /// Two draws of one model level: one command for its one block, whose two instances are the two draws in order, each
    /// placing and turning the model; the block's vertices lie in the model's box.
    /// </summary>
    /// <summary>
    /// A model that casts no shadow (the first-person arms) goes into the camera's list alone, and one that only casts it
    /// (the player's own body in first person) into the cascades' alone, and into none with the shadows off.
    /// </summary>
    [Fact]
    public void A_models_shadow_mark_picks_its_lists()
    {
        var cube = AddCube(10);
        Begin();
        RunUntil(() => _models.IsUploaded(cube));
        var at = new Vector3(0.3f, 1.8f, 0);

        Frame(shadows: true, draws: [new(cube, 0, at, Quaternion.Identity, 0)]);
        Assert.Equal(1, _meshes.Frame.ModelCamera.Records);
        Assert.True(_meshes.Frame.ModelNear.Records > 0);

        Frame(shadows: true, draws: [new(cube, 0, at, Quaternion.Identity, 0, ModelShadow.None)]);
        Assert.Equal(1, _meshes.Frame.ModelCamera.Records);
        Assert.Equal(0, _meshes.Frame.ModelNear.Records + _meshes.Frame.ModelFar.Records);

        Frame(shadows: true, draws: [new(cube, 0, at, Quaternion.Identity, 0, ModelShadow.Only)]);
        Assert.Equal(0, _meshes.Frame.ModelCamera.Records);
        Assert.True(_meshes.Frame.ModelNear.Records > 0);

        Frame(draws: [new(cube, 0, at, Quaternion.Identity, 0, ModelShadow.Only)]);
        Assert.Equal(
            0,
            _meshes.Frame.ModelCamera.Records + _meshes.Frame.ModelNear.Records + _meshes.Frame.ModelFar.Records
        );
    }

    [Fact]
    public void A_model_level_drawn_twice_is_one_command_with_two_instances()
    {
        var cube = AddCube(10);
        Begin();
        RunUntil(() => _models.IsUploaded(cube));
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1);
        ModelDraw[] draws =
        [
            new(cube, 0, new Vector3(-0.3f, 1.8f, 0), Quaternion.Identity, 0.5f),
            new(cube, 0, new Vector3(0.2f, 1.8f, 0), turn, 0.25f),
        ];

        var slot = Frame(draws: draws);

        var models = _meshes.Frame.ModelCamera;
        Assert.Equal(new MeshModelList(2, 1), models);
        var command = _targets.ModelCommandsOf(slot, 0)[0];
        Assert.Equal(2u, command.InstanceCount);
        Assert.Equal((uint)MeshPass.ModelRecordBase(slot, 0), command.FirstInstance);
        Assert.True(command.IndexCount > 0 && command.IndexCount % 3 == 0);
        var records = _targets.ModelDrawsOf(slot, 0);
        Assert.Equal(draws[0].Corner, records[0].Corner);
        Assert.Equal(0.5f, records[0].Tint);
        Assert.Equal(draws[1].Corner, records[1].Corner);
        Assert.Equal(turn, records[1].Turn);
        Assert.Equal(0.01f, records[1].VoxelMetres);
        Assert.Equal(0u, records[1].Block);
        Assert.Equal(records[0].VertexBase, records[1].VertexBase);
        var words = MemoryMarshal.Cast<byte, uint>(_targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(_targets.Heap.AsSpan());
        for (var k = 0; k < command.IndexCount; k++)
        {
            var at = (int)records[0].VertexBase + MeshVertex.Words * indices[(int)command.FirstIndex + k];
            var vertex = new MeshVertex
            {
                PositionXY = words[at],
                PositionZMaterial = words[at + 1],
                Normal = words[at + 2],
            };
            // In the block's voxels, which start one before the box.
            var position = MeshVertex.DecodePosition(in vertex) - Vector3.One;
            Assert.InRange(position.X, -0.5f, 10.5f);
            Assert.InRange(position.Y, -0.5f, 10.5f);
            Assert.InRange(position.Z, -0.5f, 10.5f);
            Assert.Equal(1, MeshVertex.DecodeMaterial(in vertex));
        }

        // Without shadows the cascades take none.
        Assert.Equal(default, _meshes.Frame.ModelNear);
        Assert.Equal(default, _meshes.Frame.ModelFar);
    }

    /// <summary>
    /// A 40-voxel cube is 2 blocks along each axis (voxels -1 to 39), each with faces: 8 commands, each over the model's
    /// one draw.
    /// </summary>
    [Fact]
    public void Each_block_of_a_model_level_is_a_command_of_its_own()
    {
        var small = AddCube(10);
        var large = AddCube(40);
        Begin();
        RunUntil(() => _models.IsUploaded(small) && _models.IsUploaded(large));
        ModelDraw[] draws =
        [
            new(large, 0, new Vector3(-0.2f, 1.6f, -0.2f), Quaternion.Identity, 0),
            new(small, 0, new Vector3(0.3f, 1.8f, 0), Quaternion.Identity, 0),
        ];

        var slot = Frame(draws: draws);

        Assert.Equal(new MeshModelList(9, 9), _meshes.Frame.ModelCamera);
        var blocks = new HashSet<uint>();
        for (var i = 0; i < 9; i++)
        {
            var command = _targets.ModelCommandsOf(slot, 0)[i];
            Assert.Equal(1u, command.InstanceCount);
            var record = _targets.ModelDrawsOf(slot, 0)[(int)command.FirstInstance - MeshPass.ModelRecordBase(slot, 0)];
            if (record.Corner == draws[0].Corner)
            {
                Assert.True(blocks.Add(record.Block));
            }
        }

        Assert.Equal(8, blocks.Count);
        Assert.Contains(MeshModelDraw.PackBlock(1, 1, 1), blocks);
    }

    /// <summary>
    /// The blocks of a 40-voxel cube together are one closed surface, its low sides too: every edge, placed in the model,
    /// is shared by exactly two triangles, once each way.
    /// </summary>
    [Fact]
    public void A_model_level_meshes_into_a_closed_surface()
    {
        var cube = AddCube(40);
        Begin();
        RunUntil(() => _models.IsUploaded(cube));
        var slot = Frame(draws: [new(cube, 0, new Vector3(-0.2f, 1.6f, -0.2f), Quaternion.Identity, 0)]);

        var words = MemoryMarshal.Cast<byte, uint>(_targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(_targets.Heap.AsSpan());
        var edges = new Dictionary<(Vector3, Vector3), int>();
        var low = new Vector3(float.MaxValue);
        var corners = new Vector3[3];
        for (var i = 0; i < _meshes.Frame.ModelCamera.Commands; i++)
        {
            var command = _targets.ModelCommandsOf(slot, 0)[i];
            var record = _targets.ModelDrawsOf(slot, 0)[(int)command.FirstInstance - MeshPass.ModelRecordBase(slot, 0)];
            var block = new Vector3(record.Block & 0xFF, (record.Block >> 8) & 0xFF, (record.Block >> 16) & 0xFF);
            for (var t = 0; t < command.IndexCount; t += 3)
            {
                for (var c = 0; c < 3; c++)
                {
                    var at = (int)record.VertexBase + MeshVertex.Words * indices[(int)command.FirstIndex + t + c];
                    var vertex = new MeshVertex { PositionXY = words[at], PositionZMaterial = words[at + 1] };
                    corners[c] = MeshVertex.DecodePosition(in vertex) + 32 * block - Vector3.One;
                    low = Vector3.Min(low, corners[c]);
                }

                for (var c = 0; c < 3; c++)
                {
                    var key = (corners[c], corners[(c + 1) % 3]);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            }
        }

        Assert.NotEmpty(edges);
        Assert.All(edges, edge => Assert.Equal(1, edge.Value));
        Assert.All(edges, edge => Assert.True(edges.ContainsKey((edge.Key.Item2, edge.Key.Item1))));
        // The low sides are there: the surface reaches below voxel 0's middle.
        Assert.True(low.X < 0.5f && low.Y < 0.5f && low.Z < 0.5f);
    }

    /// <summary>
    /// A ball whose model keeps its densities (an object kind's, <see cref="VoxelModels.HasDensities"/>) is meshed with
    /// them: its vertices lie on a sphere within a tenth of a voxel, where the same ball's materials alone put them in
    /// voxel steps several times as far off.
    /// </summary>
    [Fact]
    public void A_model_that_keeps_densities_is_meshed_with_them()
    {
        var shape = new Itera.Engine.Objects.VoxelObjectShape(1, new VoxelCoord(40, 40, 40), default);
        shape.FillEllipsoid(20, 20, 20, 15.3, 15.3, 15.3, 1);
        var materials = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < 40; z++)
        {
            for (var y = 0; y < 40; y++)
            {
                for (var x = 0; x < 40; x++)
                {
                    var i = x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z);
                    materials[i] = shape.MaterialAt(x, y, z);
                    densities[i] = shape.DensityAt(x, y, z);
                }
            }
        }

        var size = new VoxelCoord(40, 40, 40);
        var smooth = _models.Add([_models.Encode(materials, densities, size)], size, 0, 1, densities: true);
        var stepped = _models.Add([_models.Encode(materials, size)], size);
        Begin();
        RunUntil(() => _models.IsUploaded(smooth) && _models.IsUploaded(stepped));

        static float Spread(List<Vector3> vertices)
        {
            var centre = vertices.Aggregate(Vector3.Zero, (sum, v) => sum + v) / vertices.Count;
            var radii = vertices.Select(v => Vector3.Distance(v, centre)).ToList();
            var mean = radii.Average();
            return radii.Max(r => MathF.Abs(r - mean));
        }

        var smoothSpread = Spread(ModelVertices(smooth));
        var steppedSpread = Spread(ModelVertices(stepped));
        Assert.True(smoothSpread < 0.1f, $"The smooth ball's vertices are up to {smoothSpread} voxels off its sphere.");
        Assert.True(steppedSpread > 3 * smoothSpread, $"The stepped ball's are up to {steppedSpread}.");
    }

    /// <summary>
    /// An object kind two parts tall (each owning its voxels, with a border of the other's round it) meshes, both parts
    /// together, into one closed surface at every level: each edge between them made once, so no seam shows where they
    /// meet.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void An_objects_parts_mesh_into_one_closed_surface(int level)
    {
        var shape = new Itera.Engine.Objects.VoxelObjectShape(1, new VoxelCoord(40, 200, 40), default);
        shape.FillEllipsoid(20, 100, 20, 15.3, 95.3, 15.3, 1);
        var kinds = new Itera.Engine.Objects.VoxelObjectKinds();
        var kind = kinds[kinds.Register("pillar", shape)];
        var parts = Itera.Engine.Objects.VoxelObjects.MakeParts(
            _models,
            kind,
            new byte[VoxelConstants.ChunkVoxels],
            new byte[VoxelConstants.ChunkVoxels]
        );
        Assert.Equal(2, parts.Length);
        Begin();
        RunUntil(() => parts.All(part => _models.IsUploaded(part.Model)));

        var edges = new Dictionary<(Vector3, Vector3), int>();
        var corners = new Vector3[3];
        foreach (var part in parts)
        {
            // The part's corner in the kind's voxels at this level.
            var corner = part.Corner * 100 / (1 << level);
            var slot = Frame(draws: [new(part.Model, level, Vector3.Zero, Quaternion.Identity, 0)]);
            var words = MemoryMarshal.Cast<byte, uint>(_targets.Heap.AsSpan());
            var indices = MemoryMarshal.Cast<byte, ushort>(_targets.Heap.AsSpan());
            for (var i = 0; i < _meshes.Frame.ModelCamera.Commands; i++)
            {
                var command = _targets.ModelCommandsOf(slot, 0)[i];
                var record = _targets.ModelDrawsOf(slot, 0)[
                    (int)command.FirstInstance - MeshPass.ModelRecordBase(slot, 0)
                ];
                var block = new Vector3(record.Block & 0xFF, (record.Block >> 8) & 0xFF, (record.Block >> 16) & 0xFF);
                for (var t = 0; t < command.IndexCount; t += 3)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        var at = (int)record.VertexBase + MeshVertex.Words * indices[(int)command.FirstIndex + t + c];
                        var vertex = new MeshVertex { PositionXY = words[at], PositionZMaterial = words[at + 1] };
                        corners[c] = MeshVertex.DecodePosition(in vertex) + 32 * block - Vector3.One + corner;
                    }

                    for (var c = 0; c < 3; c++)
                    {
                        var key = (corners[c], corners[(c + 1) % 3]);
                        edges[key] = edges.GetValueOrDefault(key) + 1;
                    }
                }
            }
        }

        Assert.NotEmpty(edges);
        Assert.All(edges, edge => Assert.Equal(1, edge.Value));
        Assert.All(
            edges,
            edge => Assert.True(edges.ContainsKey((edge.Key.Item2, edge.Key.Item1)), $"No twin for {edge.Key}.")
        );
    }

    /// <summary>The vertices of model <paramref name="model"/>'s level 0 as one frame draws them, in its voxels.</summary>
    private List<Vector3> ModelVertices(int model)
    {
        var slot = Frame(draws: [new(model, 0, new Vector3(-0.2f, 1.6f, -0.2f), Quaternion.Identity, 0)]);
        var words = MemoryMarshal.Cast<byte, uint>(_targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(_targets.Heap.AsSpan());
        var vertices = new List<Vector3>();
        for (var i = 0; i < _meshes.Frame.ModelCamera.Commands; i++)
        {
            var command = _targets.ModelCommandsOf(slot, 0)[i];
            var record = _targets.ModelDrawsOf(slot, 0)[(int)command.FirstInstance - MeshPass.ModelRecordBase(slot, 0)];
            var block = new Vector3(record.Block & 0xFF, (record.Block >> 8) & 0xFF, (record.Block >> 16) & 0xFF);
            for (var t = 0; t < command.IndexCount; t++)
            {
                var at = (int)record.VertexBase + MeshVertex.Words * indices[(int)command.FirstIndex + t];
                var vertex = new MeshVertex { PositionXY = words[at], PositionZMaterial = words[at + 1] };
                vertices.Add(MeshVertex.DecodePosition(in vertex) + 32 * block - Vector3.One);
            }
        }

        return vertices;
    }

    /// <summary>A replaced model draws its old mesh until the new one is in, and stays counted as uploaded.</summary>
    [Fact]
    public void A_replaced_model_keeps_its_old_mesh_until_the_new_one_is_in()
    {
        // A 10-voxel cube in a 20-voxel box, then a 20-voxel one: a model keeps its box.
        var cube = _models.Add([_models.Encode(CubeVoxels(10))], new VoxelCoord(20, 20, 20));
        Begin();
        RunUntil(() => _models.IsUploaded(cube));
        ModelDraw[] draws = [new(cube, 0, new Vector3(0, 1.8f, 0), Quaternion.Identity, 0)];
        var slot = Frame(draws: draws);
        var before = _targets.ModelCommandsOf(slot, 0)[0].IndexCount;

        _models.Replace(cube, [_models.Encode(CubeVoxels(20))]);
        slot = Frame(draws: draws);

        Assert.True(_models.IsUploaded(cube));
        Assert.Equal(before, _targets.ModelCommandsOf(slot, 0)[0].IndexCount);
        RunUntil(() => !_models.Pending.Contains(cube), draws);
        slot = Frame(draws: draws);
        Assert.True(_targets.ModelCommandsOf(slot, 0)[0].IndexCount > before);
    }

    /// <summary>
    /// Matches that end with model jobs still with the workers or waiting: the next match's world and models catch up,
    /// so no job comes back to it still naming an old match's model.
    /// </summary>
    [Fact]
    public void A_new_match_takes_up_the_jobs_the_last_left_with_models()
    {
        for (var i = 0; i < 5; i++)
        {
            AddCube(40);
            Begin();
            Frame();
            _meshes.EndMatch();
        }

        var cube = AddCube(40);
        Begin();
        RunUntilUpToDate();
        RunUntil(() => _models.IsUploaded(cube));

        Assert.Equal(0, _meshes.PendingBlocks);
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
        using var targets = new FakeTargets();
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
        using var targets = new FakeTargets();
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
        using var targets = new FakeTargets();
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
        _output.WriteLine($"{meshes.BlockLayers(key)} layers with blocks of {end - first} stored");
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
        using var targets = new FakeTargets();
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
        using var targets = new FakeTargets();
        using var meshes = new WorldMeshes(
            _palette,
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
        using var targets = new FakeTargets();
        using var meshes = new WorldMeshes(
            _palette,
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
        FakeTargets targets,
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
    /// With the slope's dirt glowing, the blocks round the view each offer a light at their glowing surface: the slot's
    /// list holds the brightest, near the ground, in the dirt's colour.
    /// </summary>
    [Fact]
    public void Glowing_ground_lights_the_view_with_its_blocks_lights()
    {
        using var targets = new FakeTargets();
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = RadiusMetres, Levels = Levels },
            Renderer = new RendererSettings { MeshFinestLevel = 0 },
        };
        using var meshes = new WorldMeshes(
            new GlowingPalette(),
            targets,
            new MeshView(targets, Options.Create(options)),
            targets,
            Options.Create(options),
            NullLogger<WorldMeshes>.Instance
        );
        meshes.Initialize();
        meshes.BeginMatch(_world, _models, new NoVoxelTintMap());
        var view = LookingDown(Centre);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var frame = 0;
        int slot;
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
            _world.Update(0);
            slot = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
            Thread.Sleep(1);
        } while (!meshes.IsUpToDate);

        slot = frame++ % FrameSync.FramesInFlight;
        Frame(meshes, slot, frame / 60.0, in view);

        var count = (int)targets.LightingOf(slot).LightCount;
        Assert.InRange(count, 1, MeshPass.MostLights);
        var ground = SlopeGenerator.Height(0, 0, 17) / 100f;
        foreach (var light in targets.LightsOf(slot)[..count])
        {
            Assert.InRange(Vector3.Distance(light.Position, Centre), 0, 8);
            Assert.InRange(light.Position.Y, ground - 1, ground + 1);
            Assert.Equal(GlowingPalette.DirtColour, light.Colour);
            Assert.True(light.Power > 0 && light.Reach > 0);
        }

        // An event's light joins them, ranked with them: a bright one near the camera first; one past the gathering
        // distance or behind the view not at all.
        PointLight[] events =
        [
            new(Centre + new Vector3(0.5f, -1, 0), Vector3.UnitX, 5000),
            new(Centre + new Vector3(200, 0, 0), Vector3.UnitX, 5000),
            new(Centre + new Vector3(0, 40, 0), Vector3.UnitX, 1),
        ];
        slot = frame++ % FrameSync.FramesInFlight;
        Frame(meshes, slot, frame / 60.0, in view, default, events);
        // The glowing ground may fill the budget (128 by default): the event's light then takes the dimmest one's place.
        Assert.Equal(Math.Min(count + 1, new RendererSettings().MostLights), (int)targets.LightingOf(slot).LightCount);
        Assert.Equal(Vector3.UnitX, targets.LightsOf(slot)[0].Colour);
        Assert.Equal(5000, targets.LightsOf(slot)[0].Power);
    }

    /// <summary>The light budget (<c>Engine:Renderer:MostLights</c>) keeps the brightest at the camera.</summary>
    [Fact]
    public void The_light_budget_keeps_the_brightest_lights()
    {
        using var targets = new FakeTargets();
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = RadiusMetres, Levels = Levels },
            Renderer = new RendererSettings { MeshFinestLevel = 0, MostLights = 2 },
        };
        using var meshes = new WorldMeshes(
            _palette,
            targets,
            new MeshView(targets, Options.Create(options)),
            targets,
            Options.Create(options),
            NullLogger<WorldMeshes>.Instance
        );
        meshes.Initialize();
        meshes.BeginMatch(_world, _models, new NoVoxelTintMap());
        PointLight[] events =
        [
            new(Centre + new Vector3(1, -1, 0), new Vector3(1, 0, 0), 10),
            new(Centre + new Vector3(-1, -1, 0), new Vector3(0, 1, 0), 30),
            new(Centre + new Vector3(0, -1, 1), new Vector3(0, 0, 1), 20),
        ];
        var view = LookingDown(Centre);
        Frame(meshes, 0, 0, in view, default, events);

        Assert.Equal(2u, targets.LightingOf(0).LightCount);
        Assert.Equal(30, targets.LightsOf(0)[0].Power);
        Assert.Equal(20, targets.LightsOf(0)[1].Power);
    }

    [Fact]
    public void A_removed_model_frees_its_meshes()
    {
        Begin();
        RunUntilUpToDate();
        var empty = _meshes.HeapUsedBytes;
        var cube = AddCube(40);
        RunUntil(() => _models.IsUploaded(cube));
        Assert.True(_meshes.HeapUsedBytes > empty);

        _models.RemoveAndFree(cube);
        Frame();

        Assert.Equal(empty, _meshes.HeapUsedBytes);
    }

    [Fact]
    public void A_model_block_reads_its_voxels_with_air_round_the_box()
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        var size = new VoxelCoord(40, 5, 3);
        for (var z = 0; z < size.Z; z++)
        {
            for (var y = 0; y < size.Y; y++)
            {
                for (var x = 0; x < size.X; x++)
                {
                    voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = (byte)(1 + x);
                }
            }
        }

        var samples = new byte[VoxelWorld.BlockSamples];
        WorldMeshes.ReadModelBlock(voxels, size, 1, 0, 0, samples);

        const int edge = VoxelWorld.BlockSampleEdge;
        static int At(int x, int y, int z) => x + edge * (y + edge * z);
        // Sample s is voxel 32 + s - 3 along x, s - 3 along y and z.
        Assert.Equal(1 + 29, samples[At(0, 3, 3)]);
        Assert.Equal(1 + 39, samples[At(10, 7, 5)]);
        Assert.Equal(0, samples[At(11, 3, 3)]);
        Assert.Equal(0, samples[At(5, 2, 3)]);
        Assert.Equal(0, samples[At(5, 8, 3)]);
        Assert.Equal(0, samples[At(5, 3, 6)]);
    }

    /// <summary>
    /// The four level-1 columns round the middle are drawn as their level-0 columns (their nearest parts within level 0's
    /// reach times the share), the rest at level 1: the blocks along that square's sides are drawn from stitched meshes,
    /// which leave a tenth of the open edges plain meshes leave there. (The slope is a stair of 1 cm steps without
    /// densities, which the two levels sample apart: what stays open are the steps they disagree on.)
    /// </summary>
    [Fact]
    public void Blocks_beside_a_coarser_column_are_stitched_to_it()
    {
        var drawn = StitchFromAbove();

        // Drawing the stitched meshes allocates nothing.
        for (var i = 0; i < 10; i++)
        {
            Frame(Above, shadows: true);
        }

        Assert.Equal(
            0,
            SystemAllocation.BytesAllocatedWithoutCollection(() =>
            {
                for (var i = 0; i < 100; i++)
                {
                    Frame(Above, shadows: true);
                }
            })
        );
        Assert.Equal(drawn, _meshes.StitchedDraws);

        var stitched = OpenEdges(Frame(Above));
        var plain = OpenEdges(Frame(Above, plain: true));
        Assert.Equal(0, _meshes.StitchedDraws);
        // The debug view of plain meshes keeps the stitched ones: back from it, they are drawn at once.
        var started = _meshes.StitchesStarted;
        Frame(Above);
        Assert.Equal(drawn, _meshes.StitchedDraws);
        Assert.Equal(started, _meshes.StitchesStarted);
        _output.WriteLine($"{drawn} blocks stitched: {stitched} open edges along the seams, {plain} plain");
        Assert.True(plain > 1000, $"Only {plain} open edges along the plain seams: the test does not see them.");
        Assert.True(stitched * 8 <= plain, $"{stitched} open edges along the stitched seams, {plain} plain.");
    }

    /// <summary>
    /// An edit across a seam changes the blocks on both sides, at both levels: the stitched meshes there are made again
    /// from the new voxels, and the seam stays closed.
    /// </summary>
    [Fact]
    public void An_edit_across_a_seam_is_stitched_again()
    {
        var drawn = StitchFromAbove();
        var started = _meshes.StitchesStarted;
        var ground = SlopeGenerator.Height(256, 0, 17);
        _world.Apply(VoxelEdit.Sphere(new VoxelCoord(256, ground, 0), 40, VoxelEditOperation.Clear));
        _world.Update(0);
        Frame(Above);
        RunUntilUpToDate(Above);
        RunUntil(() => _meshes.StitchesSettled, at: Above);
        var stitched = OpenEdges(Frame(Above));
        var after = _meshes.StitchedDraws;
        var plain = OpenEdges(Frame(Above, plain: true));
        _output.WriteLine(
            $"{_meshes.StitchesStarted - started} stitched meshes made again; {drawn} blocks stitched before, {after} after: {stitched} open edges, {plain} plain"
        );
        Assert.True(_meshes.StitchesStarted > started);
        Assert.True(stitched * 8 <= plain, $"{stitched} open edges along the stitched seams, {plain} plain.");
    }

    /// <summary>
    /// A heap too small for the world's meshes: the blocks that find no room no longer count as pending, so the world
    /// comes up to date, and they are not read again while nothing frees a range (before, they were read and meshed
    /// again every frame, and the world never came up to date).
    /// </summary>
    [Fact]
    public void Blocks_a_full_heap_has_no_room_for_are_not_read_again_until_ranges_are_freed()
    {
        using var targets = new FakeTargets((int)(MeshPass.TintMapOffset + MeshPass.TintMapBytes) + (256 << 10));
        using var meshes = NewMeshes(targets);
        meshes.BeginMatch(_world, _models, new NoVoxelTintMap());
        var view = LookingDown(Centre);
        var frame = 0;
        void Step()
        {
            var slot = frame++ % FrameSync.FramesInFlight;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not come up to date with the heap full.");
            _world.Update(0);
            Step();
            Thread.Sleep(1);
        } while (!meshes.IsUpToDate);

        Assert.True(meshes.HeapUsedBytes > targets.Heap.Length / 2);
        // Two seconds for a retry the ranges freed while loading may have called for (once a second at most).
        for (var i = 0; i < 120; i++)
        {
            Step();
        }

        var read = meshes.BlocksRead;
        for (var i = 0; i < 120; i++)
        {
            Step();
        }

        Assert.Equal(read, meshes.BlocksRead);
        Assert.True(meshes.StarvedBlocks > 0, "No block found the heap full: the test does not see a full heap.");
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
        using var plenty = new FakeTargets();
        using var reference = NewMeshes(plenty, levels);
        reference.BeginMatch(world, models, new NoVoxelTintMap());
        var frame = 0;
        void Step(WorldMeshes meshes, FakeTargets targets, Vector3 at)
        {
            var slot = frame++ % FrameSync.FramesInFlight;
            var view = LookingDown(at, AboveSpread);
            view.SunDirection = ViewConstants.DefaultSunDirection;
            view.Switches = ViewSwitches.Shadows;
            targets.Apply(meshes.Regions(slot, Frame(meshes, slot, frame / 60.0, in view)));
        }

        void Settle(WorldMeshes meshes, FakeTargets targets, Vector3 at)
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
        _output.WriteLine(
            $"{used} bytes used, {undrawn} of them in columns not drawn; draws {draws}, moved {movedDraws}"
        );
        Assert.True(
            undrawn > drawn / 4,
            $"Only {undrawn} bytes the view does not draw: the test sees nothing to free."
        );

        // A heap with room for what the view draws and a quarter more.
        using var targets = new FakeTargets((int)(fixedBytes + drawn + drawn / 4));
        using var meshes = NewMeshes(targets, levels);
        meshes.BeginMatch(world, models, new NoVoxelTintMap());
        Settle(meshes, targets, Above);
        _output.WriteLine($"{meshes.Evictions} columns freed, {meshes.StarvedBlocks} blocks without room");
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
        _output.WriteLine(
            $"moved: {meshes.Evictions} columns freed, {meshes.EvictionsWoken} meshed again, {meshes.StarvedBlocks} blocks without room"
        );
        Assert.Equal(0, meshes.StarvedBlocks);
        Assert.Equal(movedDraws, (meshes.DrawCount, meshes.Frame.NearShadows, meshes.Frame.FarShadows));
        Assert.True(meshes.EvictionsWoken > 0);
    }

    /// <summary>With shadows on and models drawn, so the cascades' fit, tests, and second pass, and the models' grouping, are measured too.</summary>
    [Fact]
    public void An_upload_allocates_nothing_once_up_to_date()
    {
        var small = AddCube(10);
        var large = AddCube(40);
        Begin();
        RunUntilUpToDate();
        RunUntil(() => _models.IsUploaded(small) && _models.IsUploaded(large));
        ModelDraw[] draws =
        [
            new(large, 0, new Vector3(-0.2f, 1.6f, -0.2f), Quaternion.Identity, 0),
            new(small, 0, new Vector3(0.3f, 1.8f, 0), Quaternion.Identity, 0),
            new(small, 0, new Vector3(-0.5f, 1.8f, 0), Quaternion.Identity, 0),
        ];
        for (var i = 0; i < 100; i++)
        {
            Frame(shadows: true, draws: draws);
        }

        Assert.True(_meshes.Frame.NearShadows > 0 && _meshes.Frame.FarShadows > 0);
        Assert.True(_meshes.Frame.ModelCamera.Commands > 0 && _meshes.Frame.ModelNear.Commands > 0);
        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            Frame(shadows: true, draws: draws);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private void Begin() => _meshes.BeginMatch(_world, _models, new NoVoxelTintMap());

    /// <summary>
    /// From 3.5 m over the middle (inside the height of the slope's blocks, so the columns' distances are across them) a
    /// wide view down (<see cref="AboveSpread"/>) sees the level-0 columns round the middle and the level-1 ones past
    /// them.
    /// </summary>
    private static readonly Vector3 Above = new(0, 3.5f, 0);

    /// <summary>How far the view from <see cref="Above"/> reaches to each side per metre down: about 85 degrees.</summary>
    private const float AboveSpread = 12;

    /// <summary>Begins the match, meshes the world seen from <see cref="Above"/>, and its seams' stitched meshes; returns how many are drawn.</summary>
    private int StitchFromAbove()
    {
        Begin();
        RunUntilUpToDate(Above);
        RunUntil(() => _meshes.StitchedDraws > 0 && _meshes.StitchesSettled, at: Above);
        return _meshes.StitchedDraws;
    }

    /// <summary>
    /// One frame's upload, its copies applied to the emulated heap, with the sun's shadows when
    /// <paramref name="shadows"/>, and every block from its plain mesh when <paramref name="plain"/>. Returns the slot it
    /// used.
    /// </summary>
    private int Frame(
        Vector3? at = null,
        bool shadows = false,
        ModelDraw[]? draws = null,
        bool plain = false,
        VoxelCoord origin = default
    )
    {
        var slot = _frame++ % FrameSync.FramesInFlight;
        var view = at == Above ? LookingDown(Above, AboveSpread) : LookingDown(at ?? Centre);
        (view.OriginX, view.OriginY, view.OriginZ) = (origin.X, origin.Y, origin.Z);
        if (shadows)
        {
            view.SunDirection = ViewConstants.DefaultSunDirection;
            view.Switches = ViewSwitches.Shadows;
        }

        if (plain)
        {
            view.Switches |= ViewSwitches.Plain;
        }

        var count = Frame(_meshes, slot, _frame / 60.0, in view, draws);
        _targets.Apply(_meshes.Regions(slot, count));
        return slot;
    }

    /// <summary>Frames, with the world updated, until <paramref name="done"/> holds.</summary>
    private void RunUntil(Func<bool> done, ModelDraw[]? draws = null, Vector3? at = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
            _world.Update(0);
            Frame(at, draws: draws);
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// The edges of the world's triangles the camera drew in <paramref name="slot"/> that one triangle alone uses, their
    /// ends rounded to a sixty-fourth of a centimetre.
    /// </summary>
    private int OpenEdges(int slot)
    {
        var words = MemoryMarshal.Cast<byte, uint>(_targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(_targets.Heap.AsSpan());
        var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
        var corners = new (int, int, int)[3];
        for (var i = 0; i < _meshes.DrawCount; i++)
        {
            var command = _targets.CommandsOf(slot, 0)[i];
            var record = _targets.DrawsOf(slot)[RecordOf(command, slot)];
            for (var t = 0; t < command.IndexCount; t += 3)
            {
                for (var c = 0; c < 3; c++)
                {
                    var at = (int)record.VertexBase + MeshVertex.Words * indices[(int)command.FirstIndex + t + c];
                    var vertex = new MeshVertex { PositionXY = words[at], PositionZMaterial = words[at + 1] };
                    var p = (record.Origin + MeshVertex.DecodePosition(in vertex) * record.VoxelMetres) * 6400;
                    corners[c] = ((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));
                }

                for (var c = 0; c < 3; c++)
                {
                    var (a, b) = (corners[c], corners[(c + 1) % 3]);
                    var key = a.CompareTo(b) < 0 ? (a, b) : (b, a);
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            }
        }

        return edges.Count(edge => edge.Value == 1 && OnSeam(edge.Key.Item1) && OnSeam(edge.Key.Item2));
    }

    /// <summary>
    /// True for a point (in sixty-fourths of a centimetre) within 8 cm of the square of 2.56 m half-width round the
    /// middle: the seams between the level-0 columns there and the level-1 ones round them.
    /// </summary>
    private static bool OnSeam((int X, int Y, int Z) point)
    {
        const float Seam = 2.56f;
        const float Near = 0.08f;
        var x = MathF.Abs(point.X / 6400f);
        var z = MathF.Abs(point.Z / 6400f);
        return x < Seam + Near && z < Seam + Near && (MathF.Abs(x - Seam) < Near || MathF.Abs(z - Seam) < Near);
    }

    private void RunUntilUpToDate(Vector3? at = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
            _world.Update(0);
            Frame(at);
            Thread.Sleep(1);
        } while (!_meshes.IsUpToDate);
    }

    /// <summary>Every index count drawn last frame, in order.</summary>
    private List<uint> DrawnIndices()
    {
        Frame();
        var slot = (_frame - 1) % FrameSync.FramesInFlight;
        var counts = new List<uint>();
        for (var i = 0; i < _meshes.DrawCount; i++)
        {
            counts.Add(_targets.CommandsOf(slot, 0)[i].IndexCount);
        }

        counts.Sort();
        return counts;
    }

    /// <summary>The index of the draw a command names in its slot.</summary>
    private static int RecordOf(DrawIndexedIndirectCommand command, int slot) =>
        (int)command.FirstInstance - slot * MeshPass.MaxRecords;

    /// <summary>
    /// A view at <paramref name="at"/> looking straight down, with a 90 degree field of view, or wider: reaching
    /// <paramref name="spread"/> metres to each side per metre down.
    /// </summary>
    private static ViewConstants LookingDown(Vector3 at, float spread = 1) =>
        new()
        {
            Position = at,
            Forward = new Vector3(0, -1, 0),
            Right = new Vector3(spread, 0, 0),
            Up = new Vector3(0, 0, -spread),
            MaxDistance = 100,
        };

    /// <summary>A model of one solid cube of <paramref name="size"/> voxels a side, at level 0 alone.</summary>
    private int AddCube(int size) => _models.Add([_models.Encode(CubeVoxels(size))], new VoxelCoord(size, size, size));

    /// <summary>A chunk's voxels holding a solid cube of material 1, <paramref name="size"/> voxels a side, at its corner.</summary>
    private static byte[] CubeVoxels(int size)
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    voxels[x + (y << VoxelConstants.ChunkShift) + (z << (2 * VoxelConstants.ChunkShift))] = 1;
                }
            }
        }

        return voxels;
    }

    private sealed class CountingPalette : IVoxelPalette
    {
        public int Prepared { get; private set; }

        public void Prepare() => Prepared++;

        public VoxelLook Look(byte material) => VoxelLook.Plain(new Vector3(material / 255f, 0.5f, 0.25f));
    }

    /// <summary>A palette whose dirt glows.</summary>
    private sealed class GlowingPalette : IVoxelPalette
    {
        public static readonly Vector3 DirtColour = new(0.5f, 0.25f, 0.125f);

        public void Prepare() { }

        public VoxelLook Look(byte material) =>
            material == SlopeGenerator.Dirt
                ? new VoxelLook(DirtColour, DirtColour, Patches: 0, Glow: 0.5f)
                : VoxelLook.Plain(new Vector3(0.5f));
    }

    /// <summary>The draw target and staging in memory, and the heap emulated by applying the copy regions.</summary>
    private sealed unsafe class FakeTargets(int heapBytes = 64 << 20) : IMeshDrawTarget, IStagingRegions, IDisposable
    {
        private const ulong RegionSize = 8UL << 20;
        private readonly byte* _staging = (byte*)
            NativeMemory.AllocZeroed((nuint)(RegionSize * FrameSync.FramesInFlight));
        private readonly MeshDraw[][] _draws = [new MeshDraw[MeshPass.MaxRecords], new MeshDraw[MeshPass.MaxRecords]];
        private readonly DrawIndexedIndirectCommand[][] _commands = CommandLists();
        private readonly MeshLighting[] _shadows = new MeshLighting[FrameSync.FramesInFlight];
        private readonly MeshLight[] _lights = new MeshLight[FrameSync.FramesInFlight * MeshPass.MostLights];
        private readonly MeshModelDraw[][] _modelDraws = Lists<MeshModelDraw>(MeshPass.MaxModelRecords);
        private readonly DrawIndexedIndirectCommand[][] _modelCommands = Lists<DrawIndexedIndirectCommand>(
            MeshPass.MaxModelCommands
        );

        public byte[] Heap { get; } = new byte[heapBytes];

        public ulong HeapBytes => (ulong)Heap.Length;

        public ulong RegionBytes => RegionSize;

        public ulong RegionOffset(int slot) => (ulong)slot * RegionSize;

        public byte* Region(int slot) => _staging + RegionOffset(slot);

        /// <summary>
        /// Checks the size with a plain comparison: <c>Assert.InRange</c> makes a comparer and boxes its values in
        /// unoptimised code, which the allocation tests measure as the engine's while the JIT has not optimised it yet.
        /// </summary>
        public void Flush(int slot, ulong bytes) => Assert.True(bytes <= RegionSize);

        /// <summary>Byte <paramref name="index"/> of what was staged at <paramref name="offset"/> of the staging buffer.</summary>
        public byte Staged(ulong offset, int index) => _staging[offset + (ulong)index];

        public Span<MeshDraw> DrawsOf(int slot) => _draws[slot];

        public Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list) =>
            _commands[slot * MeshPass.WorldLists + list];

        public ref MeshLighting LightingOf(int slot) => ref _shadows[slot];

        public Span<MeshLight> LightsOf(int slot) => _lights.AsSpan(slot * MeshPass.MostLights, MeshPass.MostLights);

        public Span<MeshModelDraw> ModelDrawsOf(int slot, int list) => _modelDraws[slot * MeshPass.Lists + list];

        public Span<DrawIndexedIndirectCommand> ModelCommandsOf(int slot, int list) =>
            _modelCommands[slot * MeshPass.Lists + list];

        /// <summary>
        /// Applies a frame's copy regions to the heap, after checking that no two write the same place: one copy
        /// command's destination regions must not overlap (VUID-vkCmdCopyBuffer-pRegions-12482 in the Vulkan spec).
        /// </summary>
        public void Apply(ReadOnlySpan<BufferCopy> regions)
        {
            var sorted = regions.ToArray();
            Array.Sort(sorted, static (a, b) => a.DstOffset.CompareTo(b.DstOffset));
            for (var i = 1; i < sorted.Length; i++)
            {
                Assert.True(
                    sorted[i - 1].DstOffset + sorted[i - 1].Size <= sorted[i].DstOffset,
                    $"Two copies of one frame write heap bytes {sorted[i].DstOffset} on."
                );
            }

            foreach (var region in regions)
            {
                Assert.True(region.DstOffset + region.Size <= HeapBytes);
                new ReadOnlySpan<byte>(_staging + region.SrcOffset, (int)region.Size).CopyTo(
                    Heap.AsSpan((int)region.DstOffset, (int)region.Size)
                );
            }
        }

        public void Dispose() => NativeMemory.Free(_staging);

        private static T[][] Lists<T>(int length)
        {
            var lists = new T[FrameSync.FramesInFlight * MeshPass.Lists][];
            for (var i = 0; i < lists.Length; i++)
            {
                lists[i] = new T[length];
            }

            return lists;
        }

        private static DrawIndexedIndirectCommand[][] CommandLists()
        {
            var lists = new DrawIndexedIndirectCommand[FrameSync.FramesInFlight * MeshPass.WorldLists][];
            for (var i = 0; i < lists.Length; i++)
            {
                lists[i] = new DrawIndexedIndirectCommand[MeshPass.MaxDraws];
            }

            return lists;
        }
    }
}
