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
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The raster pipeline's world on the GPU, over a draw target and staging in memory: it meshes the slope world's
/// columns, uploads the meshes and the materials where the mesh pass reads them, draws the blocks in view, meshes an
/// edit again, follows columns that load and unload, and allocates nothing per frame once up to date. The levels'
/// tests are in <see cref="WorldMeshesLevelsTests"/>, the models' and objects' in <see cref="WorldMeshesModelsTests"/>.
/// </summary>
[RunsLong]
public sealed class WorldMeshesTests(ITestOutputHelper output) : WorldMeshesTestBase(output)
{
    [Fact]
    public void Without_a_match_an_upload_throws_and_the_world_is_not_up_to_date()
    {
        Assert.Throws<InvalidOperationException>(() => Meshes.Upload(0, 0, default));

        Begin();
        RunUntilUpToDate();
        Meshes.EndMatch();

        Assert.False(Meshes.IsUpToDate);
        Assert.Throws<InvalidOperationException>(() => Meshes.Upload(0, 0, default));
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
        Assert.False(Meshes.SetTexture(0, null, 0));
        Assert.False(Meshes.SetTexture(1, "no-such-set", 0));
        Assert.Empty(Meshes.TextureSetNames);
        var heapMaterials = MemoryMarshal.Cast<byte, MeshMaterial>(
            Targets.Heap.AsSpan((int)MeshPass.MaterialsOffset, (int)MeshPass.MaterialsBytes)
        );
        heapMaterials[1].Glow = 0.75f;

        Assert.True(Meshes.SetTexture(1, null, 0.5f));
        Frame();

        Assert.Equal(Palette.Look(1).Glow, heapMaterials[1].Glow);
        Assert.Equal(0u, heapMaterials[1].Texture);
    }

    [Fact]
    public void The_ground_is_meshed_uploaded_and_drawn_with_the_materials_in_place()
    {
        Begin();
        RunUntilUpToDate();
        var slot = Frame();

        Assert.True(Meshes.DrawCount > 0);
        var materials = MemoryMarshal.Cast<byte, MeshMaterial>(
            Targets.Heap.AsSpan((int)MeshPass.MaterialsOffset, (int)MeshPass.MaterialsBytes)
        );
        Assert.Equal(Palette.Look(1).Colour, materials[1].Colour);
        Assert.Equal(Palette.Look(1).PackLook(), materials[1].Look);
        Assert.Equal(Palette.Look(1).PackAmounts(), materials[1].Amounts);
        var words = MemoryMarshal.Cast<byte, uint>(Targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(Targets.Heap.AsSpan());
        for (var i = 0; i < Meshes.DrawCount; i++)
        {
            var command = Targets.CommandsOf(slot, 0)[i];
            var draw = Targets.DrawsOf(slot)[RecordOf(command, slot)];
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

        Assert.True(Meshes.DrawCount > 4);
        var last = 0f;
        for (var i = 0; i < Meshes.DrawCount; i++)
        {
            var draw = Targets.DrawsOf(slot)[RecordOf(Targets.CommandsOf(slot, 0)[i], slot)];
            var centre = draw.Origin + new Vector3(16 * draw.VoxelMetres);
            var distance = Vector3.DistanceSquared(centre, at);
            Assert.True(distance >= last, $"Draw {i} is nearer than the one before it.");
            last = distance;
        }
    }

    /// <summary>
    /// The camera's commands are also kept where the CPU can read them, as the slot's list holds them, each with its
    /// block's squared distance from the view, for a game's pass that works on the ground in view.
    /// </summary>
    [Fact]
    public void The_camera_commands_and_their_distances_can_be_read_on_the_cpu()
    {
        Begin();
        RunUntilUpToDate();
        var at = Centre + new Vector3(1.3f, 0, -0.7f);
        var slot = Frame(at);

        var commands = Meshes.CameraCommands;
        var keys = Meshes.CameraKeys;
        Assert.True(Meshes.DrawCount > 4);
        Assert.Equal(Meshes.DrawCount, commands.Length);
        Assert.Equal(Meshes.DrawCount, keys.Length);
        for (var i = 0; i < commands.Length; i++)
        {
            Assert.Equal(Targets.CommandsOf(slot, 0)[i], commands[i]);
            var draw = Targets.DrawsOf(slot)[RecordOf(commands[i], slot)];
            var centre = draw.Origin + new Vector3(16 * draw.VoxelMetres);
            Assert.Equal(Vector3.DistanceSquared(centre, at), keys[i], 4);
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
        var sky = Targets.LightingOf(first).SkyCorner;
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

        if (Meshes.HasSkyField)
        {
            Assert.True(Vector3.Distance(sky - by, Targets.LightingOf(slot).SkyCorner) < 1e-4f);
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
            Targets.Heap.AsSpan((int)MeshPass.MaterialsOffset, (int)MeshPass.MaterialsBytes)
        );
        Assert.Equal(MeshMaterial.TilePhase(origin2, MeshMaterial.AllMaterialsTileMetres), heapMaterials[1].TileOrigin);
    }

    /// <summary>The camera's draws of <paramref name="slot"/> in their order: each one's record and command.</summary>
    private List<(Vector3 Origin, float VoxelMetres, uint VertexBase, (uint, int, uint) Command)> Drawn(int slot)
    {
        var drawn = new List<(Vector3, float, uint, (uint, int, uint))>();
        for (var i = 0; i < Meshes.DrawCount; i++)
        {
            var command = Targets.CommandsOf(slot, 0)[i];
            var draw = Targets.DrawsOf(slot)[RecordOf(command, slot)];
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
        RunUntil(() => Models.IsUploaded(cube));
        RunUntilUpToDate();
        var lists = new FakeViewLists();
        var slot = _frame++ % FrameSync.FramesInFlight;
        var view = LookingDown(Centre);
        var middle = Centre + new Vector3(0, 5, 0);
        var viewConstants = FakeViewLists.LookingDown(middle);
        ModelDraw[] models = [new(cube, 0, new Vector3(10, 1.8f, 0), Quaternion.Identity, 0)];

        Meshes.View.Begin(slot, in view);
        lists.View.Begin(slot, in viewConstants);
        Meshes.Upload(slot, _frame / 60.0, in view, models, [lists]);
        Meshes.View.End(slot, default);
        lists.View.End(slot, default);

        ref readonly var frame = ref lists.FrameOf(slot);
        Assert.True(
            frame.Draws > Meshes.DrawCount,
            $"{frame.Draws} drawn in the view, {Meshes.DrawCount} by the camera."
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

        Assert.Equal(0, Meshes.Frame.ModelCamera.Records);
        Assert.Equal(1, frame.Models.Records);
        Assert.Equal(Targets.LightingOf(slot).SkyCorner, lists.LightingOf(slot).SkyCorner);
        Assert.Equal(Targets.LightingOf(slot).SkyCellMetres, lists.LightingOf(slot).SkyCellMetres);
    }

    [Fact]
    public void An_edit_is_meshed_again()
    {
        Begin();
        RunUntilUpToDate();
        var before = DrawnIndices();

        var ground = SlopeGenerator.Height(0, 0, 17);
        World.Apply(VoxelEdit.Sphere(new VoxelCoord(0, ground, 0), 60, VoxelEditOperation.Clear));
        World.Update(0);
        Frame();
        Assert.False(Meshes.IsUpToDate);
        Assert.True(Meshes.EditedBlocks > 0);
        RunUntilUpToDate();

        Assert.NotEqual(before, DrawnIndices());
        // The edits' latency counts the blocks the edit marked, every one of them meshed and staged.
        Assert.Equal(0, Meshes.EditedBlocks);
        Assert.True(Meshes.LastEdit.Blocks > 0 && Meshes.LastEdit.Frames > 0);
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
        Assert.True(Meshes.PendingBlocks > 0);
        Assert.Equal(0, Meshes.EditedBlocks);
        RunUntilUpToDate();
        Assert.Equal(0, Meshes.EditedBlocks);
        Assert.Equal(default, Meshes.LastEdit);

        // Made again from empty, the world loads; an edit made meanwhile is reported.
        Begin();
        Frame();
        Assert.True(Meshes.PendingBlocks > 0);
        World.Apply(
            VoxelEdit.Sphere(new VoxelCoord(0, SlopeGenerator.Height(0, 0, 17), 0), 30, VoxelEditOperation.Clear)
        );
        World.Update(0);
        RunUntil(() => Meshes.LastEdit.Blocks > 0);
        Assert.Equal(0, Meshes.EditedBlocks);
        RunUntilUpToDate();

        // An edit is reported once its blocks are in, with every block its changes marked (some come a frame later).
        var ground = SlopeGenerator.Height(0, 0, 17);
        World.Apply(VoxelEdit.Sphere(new VoxelCoord(0, ground, 0), 30, VoxelEditOperation.Clear));
        World.Update(0);
        Frame();
        var marked = Meshes.EditedBlocks;
        Assert.True(marked > 0);
        RunUntil(() => Meshes.EditedBlocks == 0);
        Assert.True(Meshes.LastEdit.Blocks >= marked);
    }

    [Fact]
    public void Columns_that_unload_and_load_are_followed()
    {
        Begin();
        RunUntilUpToDate();

        // 1.5 m above the slope there, which rises 1 cm for every 16 cm.
        var away = new Vector3(40, SlopeGenerator.Height(4000, 0, 17) / 100f + 1.5f, 0);
        World.SetCenters([away]);
        TestWorlds.WaitUntilLoaded(World);
        RunUntilUpToDate(away);

        var resident = new List<ColumnKey>();
        World.CollectResidentColumns(resident);
        Assert.Equal(resident.Count, Meshes.ColumnCount);
        Assert.True(Meshes.DrawCount > 0);
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
            Meshes.EndMatch();
        }

        Begin();
        RunUntilUpToDate();

        Assert.Equal(0, Meshes.PendingBlocks);
        Assert.True(Meshes.DrawCount > 0);
    }

    [Fact]
    public void Without_shadows_the_cascades_list_nothing_and_their_record_says_so()
    {
        Begin();
        RunUntilUpToDate();
        var slot = Frame();

        var frame = Meshes.Frame;
        Assert.False(frame.Shadows);
        Assert.Equal(0, frame.NearShadows);
        Assert.Equal(0, frame.FarShadows);
        Assert.Equal(frame.Draws, frame.Records);
        Assert.Equal(0u, Targets.LightingOf(slot).Shadows);
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
        Targets.Apply(Meshes.Regions(slot, Frame(Meshes, slot, _frame / 60.0, in view)));

        var frame = Meshes.Frame;
        Assert.True(frame.Shadows);
        Assert.True(frame.Draws > 0);
        Assert.True(frame.NearShadows > 0);
        Assert.InRange(frame.Records, frame.Draws, frame.Draws + frame.NearShadows + frame.FarShadows);
        var seen = new HashSet<int>();
        for (var i = 0; i < frame.Draws; i++)
        {
            Assert.True(seen.Add(RecordOf(Targets.CommandsOf(slot, 0)[i], slot)));
        }

        var nearOnly = 0;
        var listed = new HashSet<int>();
        for (var i = 0; i < frame.NearShadows; i++)
        {
            var command = Targets.CommandsOf(slot, 1)[i];
            var record = RecordOf(command, slot);
            Assert.True(command.IndexCount > 0);
            Assert.InRange(record, 0, frame.Records - 1);
            Assert.True(listed.Add(record));
            if (seen.Contains(record))
            {
                continue;
            }

            // The view meets the ground about 1.5 m either side of the camera.
            var draw = Targets.DrawsOf(slot)[record];
            var centreX = draw.Origin.X + 16 * draw.VoxelMetres;
            Assert.True(centreX > -2.75f, $"A block at x = {centreX} m, away from the sun, cannot shade the view.");
            if (centreX > 1.75f)
            {
                nearOnly++;
            }
        }

        Assert.True(nearOnly > 0);
        ref readonly var shadows = ref Targets.LightingOf(slot);
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
        using var targets = new FakeWorldTargets();
        using var meshes = NewMeshes(targets, distantShadowMetres: 2000);
        meshes.BeginMatch(World, Models, new NoVoxelTintMap());
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
            World.Update(0);
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
    /// With the slope's dirt glowing, the blocks round the view each offer a light at their glowing surface: the slot's
    /// list holds the brightest, near the ground, in the dirt's colour.
    /// </summary>
    [Fact]
    public void Glowing_ground_lights_the_view_with_its_blocks_lights()
    {
        using var targets = new FakeWorldTargets();
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
        meshes.BeginMatch(World, Models, new NoVoxelTintMap());
        var view = LookingDown(Centre);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var frame = 0;
        int slot;
        do
        {
            Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
            World.Update(0);
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
        using var targets = new FakeWorldTargets();
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings { StreamingRadiusMetres = RadiusMetres, Levels = Levels },
            Renderer = new RendererSettings { MeshFinestLevel = 0, MostLights = 2 },
        };
        using var meshes = new WorldMeshes(
            Palette,
            targets,
            new MeshView(targets, Options.Create(options)),
            targets,
            Options.Create(options),
            NullLogger<WorldMeshes>.Instance
        );
        meshes.Initialize();
        meshes.BeginMatch(World, Models, new NoVoxelTintMap());
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
        Assert.Equal(drawn, Meshes.StitchedDraws);

        var stitched = OpenEdges(Frame(Above));
        var plain = OpenEdges(Frame(Above, plain: true));
        Assert.Equal(0, Meshes.StitchedDraws);
        // The debug view of plain meshes keeps the stitched ones: back from it, they are drawn at once.
        var started = Meshes.StitchesStarted;
        Frame(Above);
        Assert.Equal(drawn, Meshes.StitchedDraws);
        Assert.Equal(started, Meshes.StitchesStarted);
        Output.WriteLine($"{drawn} blocks stitched: {stitched} open edges along the seams, {plain} plain");
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
        var started = Meshes.StitchesStarted;
        var ground = SlopeGenerator.Height(256, 0, 17);
        World.Apply(VoxelEdit.Sphere(new VoxelCoord(256, ground, 0), 40, VoxelEditOperation.Clear));
        World.Update(0);
        Frame(Above);
        RunUntilUpToDate(Above);
        RunUntil(() => Meshes.StitchesSettled, at: Above);
        var stitched = OpenEdges(Frame(Above));
        var after = Meshes.StitchedDraws;
        var plain = OpenEdges(Frame(Above, plain: true));
        Output.WriteLine(
            $"{Meshes.StitchesStarted - started} stitched meshes made again; {drawn} blocks stitched before, {after} after: {stitched} open edges, {plain} plain"
        );
        Assert.True(Meshes.StitchesStarted > started);
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
        using var targets = new FakeWorldTargets((int)(MeshPass.TintMapOffset + MeshPass.TintMapBytes) + (256 << 10));
        using var meshes = NewMeshes(targets);
        meshes.BeginMatch(World, Models, new NoVoxelTintMap());
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
            World.Update(0);
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

    /// <summary>Begins the match, meshes the world seen from <see cref="WorldMeshesTestBase.Above"/>, and its seams' stitched meshes; returns how many are drawn.</summary>
    private int StitchFromAbove()
    {
        Begin();
        RunUntilUpToDate(Above);
        RunUntil(() => Meshes.StitchedDraws > 0 && Meshes.StitchesSettled, at: Above);
        return Meshes.StitchedDraws;
    }

    /// <summary>
    /// The edges of the world's triangles the camera drew in <paramref name="slot"/> that one triangle alone uses, their
    /// ends rounded to a sixty-fourth of a centimetre.
    /// </summary>
    private int OpenEdges(int slot)
    {
        var words = MemoryMarshal.Cast<byte, uint>(Targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(Targets.Heap.AsSpan());
        var edges = new Dictionary<((int, int, int), (int, int, int)), int>();
        var corners = new (int, int, int)[3];
        for (var i = 0; i < Meshes.DrawCount; i++)
        {
            var command = Targets.CommandsOf(slot, 0)[i];
            var record = Targets.DrawsOf(slot)[RecordOf(command, slot)];
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

    /// <summary>Every index count drawn last frame, in order.</summary>
    private List<uint> DrawnIndices()
    {
        Frame();
        var slot = (_frame - 1) % FrameSync.FramesInFlight;
        var counts = new List<uint>();
        for (var i = 0; i < Meshes.DrawCount; i++)
        {
            counts.Add(Targets.CommandsOf(slot, 0)[i].IndexCount);
        }

        counts.Sort();
        return counts;
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
}
