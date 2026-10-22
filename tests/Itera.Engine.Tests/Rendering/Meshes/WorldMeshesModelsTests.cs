using System.Numerics;
using System.Runtime.InteropServices;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Voxels;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The voxel models in the world's meshes: they are meshed, uploaded, drawn in the lists their shadow mark picks,
/// replaced, shared, and removed, and an upload with them allocates nothing. The voxel objects' tests are in
/// <c>WorldMeshesObjectsTests.cs</c>.
/// </summary>
public sealed partial class WorldMeshesModelsTests(ITestOutputHelper output) : WorldMeshesTestBase(output)
{
    [Fact]
    public void Initialize_prepares_the_palette_once()
    {
        // The meshes are made when first asked for.
        Assert.NotNull(Meshes);
        Assert.Equal(1, Palette.Prepared);
    }

    [Fact]
    public void Models_count_as_uploaded_once_meshed_and_retired_ones_go_once_the_world_is_meshed()
    {
        var first = AddCube(10);
        var second = AddCube(20);
        Begin();
        Assert.False(Models.IsUploaded(first));

        RunUntil(() => Models.IsUploaded(first) && Models.IsUploaded(second));
        RunUntilUpToDate();
        Models.Retire(second, 0, Vector3.Zero, Quaternion.Identity, 0);
        Frame();

        Assert.Equal(1, Models.Count);
        Assert.True(Models.Retired.IsEmpty);
    }

    /// <summary>
    /// Without voxel terrain the meshes follow the match's models alone: a model is meshed and counts as uploaded, a draw
    /// of it goes into the camera's and the cascades' lists, no world column is followed or drawn, the upload is up to
    /// date, and a retired model goes at the next frame.
    /// </summary>
    [Fact]
    public void Without_terrain_the_models_alone_are_meshed_and_drawn()
    {
        using var targets = new FakeWorldTargets();
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
    /// A model that casts no shadow (the first-person arms) goes into the camera's list alone, and one that only casts it
    /// (the player's own body in first person) into the cascades' alone, and into none with the shadows off.
    /// </summary>
    [Fact]
    public void A_models_shadow_mark_picks_its_lists()
    {
        var cube = AddCube(10);
        Begin();
        RunUntil(() => Models.IsUploaded(cube));
        var at = new Vector3(0.3f, 1.8f, 0);

        Frame(shadows: true, draws: [new(cube, 0, at, Quaternion.Identity, 0)]);
        Assert.Equal(1, Meshes.Frame.ModelCamera.Records);
        Assert.True(Meshes.Frame.ModelNear.Records > 0);

        Frame(shadows: true, draws: [new(cube, 0, at, Quaternion.Identity, 0, ModelShadow.None)]);
        Assert.Equal(1, Meshes.Frame.ModelCamera.Records);
        Assert.Equal(0, Meshes.Frame.ModelNear.Records + Meshes.Frame.ModelFar.Records);

        Frame(shadows: true, draws: [new(cube, 0, at, Quaternion.Identity, 0, ModelShadow.Only)]);
        Assert.Equal(0, Meshes.Frame.ModelCamera.Records);
        Assert.True(Meshes.Frame.ModelNear.Records > 0);

        Frame(draws: [new(cube, 0, at, Quaternion.Identity, 0, ModelShadow.Only)]);
        Assert.Equal(
            0,
            Meshes.Frame.ModelCamera.Records + Meshes.Frame.ModelNear.Records + Meshes.Frame.ModelFar.Records
        );
    }

    /// <summary>
    /// Two draws of one model level: one command for its one block, whose two instances are the two draws in order, each
    /// placing and turning the model; the block's vertices lie in the model's box.
    /// </summary>
    [Fact]
    public void A_model_level_drawn_twice_is_one_command_with_two_instances()
    {
        var cube = AddCube(10);
        Begin();
        RunUntil(() => Models.IsUploaded(cube));
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1);
        ModelDraw[] draws =
        [
            new(cube, 0, new Vector3(-0.3f, 1.8f, 0), Quaternion.Identity, 0.5f),
            new(cube, 0, new Vector3(0.2f, 1.8f, 0), turn, 0.25f),
        ];

        var slot = Frame(draws: draws);

        var models = Meshes.Frame.ModelCamera;
        Assert.Equal(new MeshModelList(2, 1), models);
        var command = Targets.ModelCommandsOf(slot, 0)[0];
        Assert.Equal(2u, command.InstanceCount);
        Assert.Equal((uint)MeshPass.ModelRecordBase(slot, 0), command.FirstInstance);
        Assert.True(command.IndexCount > 0 && command.IndexCount % 3 == 0);
        var records = Targets.ModelDrawsOf(slot, 0);
        Assert.Equal(draws[0].Corner, records[0].Corner);
        Assert.Equal(0.5f, records[0].Tint);
        Assert.Equal(draws[1].Corner, records[1].Corner);
        Assert.Equal(turn, records[1].Turn);
        Assert.Equal(0.01f, records[1].VoxelMetres);
        Assert.Equal(0u, records[1].Block);
        Assert.Equal(records[0].VertexBase, records[1].VertexBase);
        var words = MemoryMarshal.Cast<byte, uint>(Targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(Targets.Heap.AsSpan());
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
        Assert.Equal(default, Meshes.Frame.ModelNear);
        Assert.Equal(default, Meshes.Frame.ModelFar);
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
        RunUntil(() => Models.IsUploaded(small) && Models.IsUploaded(large));
        ModelDraw[] draws =
        [
            new(large, 0, new Vector3(-0.2f, 1.6f, -0.2f), Quaternion.Identity, 0),
            new(small, 0, new Vector3(0.3f, 1.8f, 0), Quaternion.Identity, 0),
        ];

        var slot = Frame(draws: draws);

        Assert.Equal(new MeshModelList(9, 9), Meshes.Frame.ModelCamera);
        var blocks = new HashSet<uint>();
        for (var i = 0; i < 9; i++)
        {
            var command = Targets.ModelCommandsOf(slot, 0)[i];
            Assert.Equal(1u, command.InstanceCount);
            var record = Targets.ModelDrawsOf(slot, 0)[(int)command.FirstInstance - MeshPass.ModelRecordBase(slot, 0)];
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
        RunUntil(() => Models.IsUploaded(cube));
        var slot = Frame(draws: [new(cube, 0, new Vector3(-0.2f, 1.6f, -0.2f), Quaternion.Identity, 0)]);

        var words = MemoryMarshal.Cast<byte, uint>(Targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(Targets.Heap.AsSpan());
        var edges = new Dictionary<(Vector3, Vector3), int>();
        var low = new Vector3(float.MaxValue);
        var corners = new Vector3[3];
        for (var i = 0; i < Meshes.Frame.ModelCamera.Commands; i++)
        {
            var command = Targets.ModelCommandsOf(slot, 0)[i];
            var record = Targets.ModelDrawsOf(slot, 0)[(int)command.FirstInstance - MeshPass.ModelRecordBase(slot, 0)];
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
        var smooth = Models.Add([Models.Encode(materials, densities, size)], size, 0, 1, densities: true);
        var stepped = Models.Add([Models.Encode(materials, size)], size);
        Begin();
        RunUntil(() => Models.IsUploaded(smooth) && Models.IsUploaded(stepped));

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
    /// An object kind in three parts, an L across a seam in x and one in y with the corner part between them empty (each
    /// part owning its voxels, with a border of its neighbours' round it), meshes, the parts together, into one closed
    /// surface at every level: each edge between them made once; and a vertex at a seam has the same normal and openness
    /// in both parts, as their borders reach as far as the mesher reads, so no seam shows where they meet.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void An_objects_parts_mesh_into_one_closed_surface(int level)
    {
        var shape = new Itera.Engine.Objects.VoxelObjectShape(1, new VoxelCoord(160, 160, 40), default);
        // An L of two boxes, its faces off the voxels' lines.
        shape.FillBox(4.3, 4.3, 4.3, 150.4, 70.6, 35.7, 1);
        shape.FillBox(4.3, 4.3, 4.3, 70.6, 150.4, 35.7, 1);
        var kinds = new Itera.Engine.Objects.VoxelObjectKinds();
        var kind = kinds[kinds.Register("corner", shape)];
        var parts = Itera.Engine.Objects.VoxelObjects.MakeParts(
            Models,
            kind,
            new byte[VoxelConstants.ChunkVoxels],
            new byte[VoxelConstants.ChunkVoxels]
        );
        Assert.Equal(3, parts.Length);
        Begin();
        RunUntil(() => parts.All(part => Models.IsUploaded(part.Model)));

        var edges = new Dictionary<(Vector3, Vector3), int>();
        // Each vertex's normal and openness, by its place, and the parts it was made in.
        var vertices = new Dictionary<Vector3, (uint Normal, byte Openness, int Part)>();
        var shared = 0;
        var corners = new Vector3[3];
        for (var p = 0; p < parts.Length; p++)
        {
            var part = parts[p];
            // The part's corner in the kind's voxels at this level.
            var corner = part.Corner * 100 / (1 << level);
            var slot = Frame(draws: [new(part.Model, level, Vector3.Zero, Quaternion.Identity, 0)]);
            var words = MemoryMarshal.Cast<byte, uint>(Targets.Heap.AsSpan());
            var indices = MemoryMarshal.Cast<byte, ushort>(Targets.Heap.AsSpan());
            for (var i = 0; i < Meshes.Frame.ModelCamera.Commands; i++)
            {
                var command = Targets.ModelCommandsOf(slot, 0)[i];
                var record = Targets.ModelDrawsOf(slot, 0)[
                    (int)command.FirstInstance - MeshPass.ModelRecordBase(slot, 0)
                ];
                var block = new Vector3(record.Block & 0xFF, (record.Block >> 8) & 0xFF, (record.Block >> 16) & 0xFF);
                for (var t = 0; t < command.IndexCount; t += 3)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        var at = (int)record.VertexBase + MeshVertex.Words * indices[(int)command.FirstIndex + t + c];
                        var vertex = new MeshVertex
                        {
                            PositionXY = words[at],
                            PositionZMaterial = words[at + 1],
                            Normal = words[at + 2],
                        };
                        corners[c] = MeshVertex.DecodePosition(in vertex) + 32 * block - Vector3.One + corner;
                        var openness = MeshVertex.DecodeOpenness(in vertex);
                        if (vertices.TryGetValue(corners[c], out var seen) && seen.Part != p)
                        {
                            Assert.Equal(seen.Normal, vertex.Normal);
                            Assert.Equal(seen.Openness, openness);
                            shared++;
                        }
                        else
                        {
                            vertices[corners[c]] = (vertex.Normal, openness, p);
                        }
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
        Assert.True(shared > 10, $"Only {shared} vertices lie on a seam.");
    }

    /// <summary>
    /// Two models drawn so often that their draws need four times the records the camera's list holds: each takes the same
    /// share, a quarter of its draws, the first gathered first, so neither drops whole.
    /// </summary>
    [Fact]
    public void A_full_model_list_gives_every_model_level_the_same_share()
    {
        var a = AddCube(40);
        var b = AddCube(40);
        Begin();
        RunUntil(() => Models.IsUploaded(a) && Models.IsUploaded(b));
        var at = new Vector3(0.2f, 1.2f, 0.1f);
        var draws = new ModelDraw[16_000];
        for (var i = 0; i < draws.Length; i++)
        {
            draws[i] = new ModelDraw(i % 2 == 0 ? a : b, 0, at, Quaternion.Identity, 0);
        }

        var slot = Frame(draws: draws);

        var records = Targets.ModelDrawsOf(slot, 0)[..Meshes.Frame.ModelCamera.Records];
        Assert.InRange(records.Length, MeshPass.MaxModelRecords - 64, MeshPass.MaxModelRecords);
        var commands = Targets.ModelCommandsOf(slot, 0)[..Meshes.Frame.ModelCamera.Commands];
        // Every block of each cube is drawn with the same number of instances: 16 blocks, half of them each cube's.
        Assert.Equal(16, commands.Length);
        var counts = commands.ToArray().Select(command => command.InstanceCount).Distinct().ToList();
        Assert.Single(counts);
        Assert.InRange(counts[0], 4_000u, 4_096u);
    }

    /// <summary>The vertices of model <paramref name="model"/>'s level 0 as one frame draws them, in its voxels.</summary>
    private List<Vector3> ModelVertices(int model)
    {
        var slot = Frame(draws: [new(model, 0, new Vector3(-0.2f, 1.6f, -0.2f), Quaternion.Identity, 0)]);
        var words = MemoryMarshal.Cast<byte, uint>(Targets.Heap.AsSpan());
        var indices = MemoryMarshal.Cast<byte, ushort>(Targets.Heap.AsSpan());
        var vertices = new List<Vector3>();
        for (var i = 0; i < Meshes.Frame.ModelCamera.Commands; i++)
        {
            var command = Targets.ModelCommandsOf(slot, 0)[i];
            var record = Targets.ModelDrawsOf(slot, 0)[(int)command.FirstInstance - MeshPass.ModelRecordBase(slot, 0)];
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
        var cube = Models.Add([Models.Encode(CubeVoxels(10))], new VoxelCoord(20, 20, 20));
        Begin();
        RunUntil(() => Models.IsUploaded(cube));
        ModelDraw[] draws = [new(cube, 0, new Vector3(0, 1.8f, 0), Quaternion.Identity, 0)];
        var slot = Frame(draws: draws);
        var before = Targets.ModelCommandsOf(slot, 0)[0].IndexCount;

        Models.Replace(cube, [Models.Encode(CubeVoxels(20))]);
        slot = Frame(draws: draws);

        Assert.True(Models.IsUploaded(cube));
        Assert.Equal(before, Targets.ModelCommandsOf(slot, 0)[0].IndexCount);
        RunUntil(() => !Models.Pending.Contains(cube), draws);
        slot = Frame(draws: draws);
        Assert.True(Targets.ModelCommandsOf(slot, 0)[0].IndexCount > before);
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
            Meshes.EndMatch();
        }

        var cube = AddCube(40);
        Begin();
        RunUntilUpToDate();
        RunUntil(() => Models.IsUploaded(cube));

        Assert.Equal(0, Meshes.PendingBlocks);
    }

    [Fact]
    public void A_removed_model_frees_its_meshes()
    {
        Begin();
        RunUntilUpToDate();
        var empty = Meshes.HeapUsedBytes;
        var cube = AddCube(40);
        RunUntil(() => Models.IsUploaded(cube));
        Assert.True(Meshes.HeapUsedBytes > empty);

        Models.RemoveAndFree(cube);
        Frame();

        Assert.Equal(empty, Meshes.HeapUsedBytes);
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

    /// <summary>With shadows on and models drawn, so the cascades' fit, tests, and second pass, and the models' grouping, are measured too.</summary>
    [Fact]
    public void An_upload_allocates_nothing_once_up_to_date()
    {
        var small = AddCube(10);
        var large = AddCube(40);
        Begin();
        RunUntilUpToDate();
        RunUntil(() => Models.IsUploaded(small) && Models.IsUploaded(large));
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

        Assert.True(Meshes.Frame.NearShadows > 0 && Meshes.Frame.FarShadows > 0);
        Assert.True(Meshes.Frame.ModelCamera.Commands > 0 && Meshes.Frame.ModelNear.Commands > 0);
        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 1000; i++)
        {
            Frame(shadows: true, draws: draws);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// A model sharing a base's meshes for the blocks it does not mark (<see cref="VoxelModels.ShareBlocks"/>), a cube of
    /// 80 voxels dented in its first block: it meshes that block alone and draws the base's meshes for the rest, so the
    /// heap grows by that block's mesh; when it goes, the base keeps every mesh it had.
    /// </summary>
    [Fact]
    public void A_model_sharing_a_bases_meshes_meshes_only_its_marked_blocks()
    {
        var size = new VoxelCoord(80, 80, 80);
        var cube = CubeVoxels(80);
        var basis = Models.Add([Models.Encode(cube)], size);
        Begin();
        RunUntilUpToDate();
        RunUntil(() => Models.IsUploaded(basis));
        var heap = Meshes.HeapUsedBytes;
        var baseBlocks = Meshes.ModelBlocksOf(basis, 0).ToArray();

        // A dent in its corner, read by block (0, 0, 0) alone: voxels 0 to 4 lie in no other block's reads.
        var dented = (byte[])cube.Clone();
        for (var z = 0; z < 2; z++)
        {
            for (var y = 0; y < 5; y++)
            {
                for (var x = 0; x < 5; x++)
                {
                    dented[x + (y << VoxelConstants.ChunkShift) + (z << (2 * VoxelConstants.ChunkShift))] = 0;
                }
            }
        }

        var copy = Models.Add([Models.Encode(dented)], size);
        var changed = new[] { new bool[27] };
        changed[0][0] = true;
        Models.ShareBlocks(copy, basis, changed);
        RunUntil(() => Models.IsUploaded(copy));

        var copyBlocks = Meshes.ModelBlocksOf(copy, 0).ToArray();
        Assert.Equal(baseBlocks.Length, copyBlocks.Length);
        var own = Assert.Single(copyBlocks, block => !block.Shared);
        Assert.Equal(MeshModelDraw.PackBlock(0, 0, 0), own.Block);
        Assert.DoesNotContain(baseBlocks, block => block.RangeOffset == own.RangeOffset);
        foreach (var block in copyBlocks.Where(block => block.Shared))
        {
            Assert.Contains(block with { Shared = false }, baseBlocks);
        }

        Assert.InRange(Meshes.HeapUsedBytes - heap, 1, 2L * own.RangeWords * sizeof(uint));

        Models.RemoveAndFree(copy);
        Frame();
        Frame();
        Assert.Equal(heap, Meshes.HeapUsedBytes);
        Assert.Equal(baseBlocks, Meshes.ModelBlocksOf(basis, 0).ToArray());
    }
}
