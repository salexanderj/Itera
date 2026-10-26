using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Rendering.Meshes;

/// <summary>
/// The voxel objects in the world's meshes (<c>WorldMeshes.Objects.cs</c>): a tree's models are drawn in a pass's lists
/// exactly where the pass split a level-K column under its reach below K (the camera's for the camera and near cascade,
/// the far cascade's own), at the level its distance gives, turned by its heading; and frames with objects allocate
/// nothing.
/// </summary>
public sealed partial class WorldMeshesModelsTests
{
    private const byte Bark = 30;
    private static readonly VoxelGrid s_objectGrid = new(10);

    /// <summary>
    /// Trees 4 m apart along x from the middle out to 60 m, the camera 25 m over the middle looking down wide: each tree's
    /// models are in the camera's list when a level-2 column under its reach is split below level 2 or it is nearer than
    /// the hand-over's fade's end (every one here), and in the far cascade's just when the far pass split one, and every
    /// drawn part lies where the tree's anchor and heading put it. Frames of the settled scene allocate nothing.
    /// </summary>
    [Fact]
    public void Objects_are_drawn_as_models_where_a_pass_split_their_column_below_the_hand_over_and_allocate_nothing()
    {
        using var scene = new ObjectScene(this);
        var at = new Vector3(0, 25, 0);
        scene.Settle(at, shadows: true);

        var slot = scene.Frame(at, shadows: true);

        var camera = scene.Corners(slot, 0);
        var far = scene.Corners(slot, 2);
        Assert.NotEmpty(camera);
        foreach (var placement in scene.Placements)
        {
            var parts = scene.ExpectedCorners(placement);
            var inCamera =
                scene.SplitUnder(placement, scene.Meshes.CameraSplit)
                || Vector3.Distance(at, ObjectScene.Local(placement.Anchor)) < scene.Meshes.HandOverFade().End;
            var inFar = scene.SplitUnder(placement, scene.Meshes.FarSplit);
            // Only trees within the view's wide frustum can be in the camera's list; each part's corner is exact.
            foreach (var corner in parts)
            {
                if (!inCamera)
                {
                    Assert.DoesNotContain(camera, c => Vector3.Distance(c, corner) < 1e-3f);
                }

                if (!inFar)
                {
                    Assert.DoesNotContain(far, c => Vector3.Distance(c, corner) < 1e-3f);
                }
            }
        }

        // Every tree under a split column or within the fade is drawn (the view, 25 m up and wide, sees them all).
        var drawn = 0;
        foreach (var placement in scene.Placements)
        {
            if (
                scene.SplitUnder(placement, scene.Meshes.CameraSplit)
                || Vector3.Distance(at, ObjectScene.Local(placement.Anchor)) < scene.Meshes.HandOverFade().End
            )
            {
                foreach (var corner in scene.ExpectedCorners(placement))
                {
                    Assert.Contains(camera, c => Vector3.Distance(c, corner) < 1e-3f);
                }

                drawn++;
            }
        }

        Assert.True(scene.SplitUnder(scene.Placements[0], scene.Meshes.CameraSplit));
        Assert.True(drawn > 3, $"Only {drawn} trees lay under split columns.");
        // The far cascade casts the split trees' shadows from their models too.
        Assert.NotEmpty(scene.Meshes.FarSplit);
        Assert.NotEmpty(far);

        // Frames of the same scene allocate nothing.
        var before = Itera.TestSupport.SystemAllocation.StartMeasurement();
        for (var i = 0; i < 5; i++)
        {
            scene.Frame(at, shadows: true);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// A level-2 column near the camera splits below level 2 only once the areas whose objects reach into it are filled:
    /// before, the camera draws the level-2 columns whole, with the objects' copies in their voxels, so no tree goes
    /// undrawn between its copy and its model; after, they split.
    /// </summary>
    [Fact]
    public void A_column_splits_below_the_hand_over_once_its_objects_are_known()
    {
        using var scene = new ObjectScene(this, fill: false);
        var at = new Vector3(0, 25, 0);
        scene.Settle(at, shadows: false);
        Assert.Empty(scene.Meshes.CameraSplit);
        var slot = scene.Frame(at, shadows: false);
        Assert.Empty(scene.Corners(slot, 0));

        scene.FillAreas();
        scene.Frame(at, shadows: false);
        slot = scene.Frame(at, shadows: false);

        Assert.NotEmpty(scene.Meshes.CameraSplit);
        Assert.NotEmpty(scene.Corners(slot, 0));
    }

    /// <summary>
    /// A tree's models are drawn at its own voxels within level 0's reach of the camera, twice them within level 1's, and
    /// four times them past it; within 2 m of either distance both levels are drawn, the farther taking the dither's
    /// thresholds under its share of the band and the nearer those over it, so the two cover every threshold between
    /// them; a tree under no split column (its copy drawn) fades out from the hand-over's fade's start to its end, its
    /// copy showing in the thresholds under its share; the camera's list writes the draws that fade last, for the pass to
    /// draw with its fading shaders alone; and trees lie in each band.
    /// </summary>
    [Fact]
    public void An_objects_levels_fade_into_each_other_by_distance()
    {
        using var scene = new ObjectScene(this);
        // The view is 1440 pixels high and its Up 4 long: a metre a metre away is 180 pixels. The trees' 5 cm voxels are
        // ObjectVoxelPixels (2) wide 4.5 m from a tree's nearest point, their 10 cm level's 9 m from it.
        var levelZero = ObjectScene.Height / (2 * 4f) * 0.05f / new EngineOptions().Renderer.ObjectVoxelPixels;
        var levelOne = 2 * levelZero;
        var radius = scene.TreeRadiusMetres;
        var (fadeStart, fadeEnd) = scene.Meshes.HandOverFade();
        var bands = new HashSet<int>();
        // From 3 m up trees lie in the bands round both levels' distances, and from 100 m up the far trees lie in the
        // hand-over's fade (from 107 m: a level-2 column is 51.2 m wide), their columns not split.
        foreach (var at in new[] { new Vector3(0, 3, 0), new Vector3(0, 25, 0), new Vector3(0, 100, 0) })
        {
            scene.Settle(at, shadows: false);
            var slot = scene.Frame(at, shadows: false);
            var list = scene.Meshes.Frame.ModelCamera;
            var records = Targets.ModelDrawsOf(slot, 0)[..list.Records].ToArray();
            // The draws shown whole first, then those that fade, the last commands counted as faded.
            var firstFaded = Array.FindIndex(records, r => r.Shown != MeshModelDraw.ShowsAll);
            Assert.Equal(firstFaded >= 0, list.Faded > 0);
            if (firstFaded >= 0)
            {
                Assert.All(records[firstFaded..], r => Assert.NotEqual(MeshModelDraw.ShowsAll, r.Shown));
                var command = Targets.ModelCommandsOf(slot, 0)[list.Commands - list.Faded];
                Assert.Equal((uint)(MeshPass.ModelRecordBase(slot, 0) + firstFaded), command.FirstInstance);
            }

            foreach (var placement in scene.Placements)
            {
                var corner = scene.ExpectedCorners(placement)[0];
                var drawn = records.Where(r => Vector3.Distance(r.Corner, corner) < 1e-3f).ToList();
                if (drawn.Count == 0)
                {
                    // Outside the view, which looks straight down.
                    continue;
                }

                var distance = Vector3.Distance(at, ObjectScene.Local(placement.Anchor));
                var nearest = Math.Max(distance - radius, 0);
                var toOne = Math.Clamp((nearest - levelZero + 2) / 4, 0, 1);
                var toTwo = Math.Clamp((nearest - levelOne + 2) / 4, 0, 1);
                var split = scene.SplitUnder(placement, scene.Meshes.CameraSplit);
                var toCopy = split ? 0 : Math.Clamp((distance - fadeStart) / (fadeEnd - fadeStart), 0, 1);
                var expected = new List<(float Metres, uint Shown)>();
                foreach (
                    var (metres, from, to) in new[]
                    {
                        (0.05f, Math.Max(toCopy, toOne), 1f),
                        (0.10f, Math.Max(toCopy, toTwo), toOne),
                        (0.20f, toCopy, toTwo),
                    }
                )
                {
                    var shown = MeshModelDraw.Shows(from, to);
                    if (shown != MeshModelDraw.ShowsNone)
                    {
                        expected.Add((metres, shown));
                    }
                }

                // A record for each block of a level drawn: the levels drawn, each with its share.
                var levels = drawn.Select(r => MathF.Round(r.VoxelMetres, 3)).Distinct().Order().ToList();
                Assert.Equal(expected.Select(e => MathF.Round(e.Metres, 3)).Order(), levels);
                foreach (var (metres, shown) in expected)
                {
                    Assert.All(
                        drawn.Where(r => Math.Abs(r.VoxelMetres - metres) < 1e-4f),
                        r => Assert.Equal(shown, r.Shown)
                    );
                }

                if (toCopy > 0)
                {
                    bands.Add(2);
                }
                else if (levels.Count > 1)
                {
                    bands.Add(nearest < (levelZero + levelOne) / 2 ? 0 : 1);
                }
            }
        }

        Assert.True(
            bands.Count == 3,
            $"Bands {string.Join(", ", bands)}; levels at {levelZero} and {levelOne} m, the fade from {fadeStart} to {fadeEnd} m."
        );
    }

    /// <summary>
    /// A 10 cm world of flat stone with a row of trees (5 cm voxels, two parts tall) every 4 m along +x from 2 m to 58 m,
    /// each turned its own way; its objects and their models; and a world mesh of its own over the test's targets.
    /// </summary>
    private sealed class ObjectScene : IDisposable
    {
        private readonly WorldMeshesModelsTests _test;
        private readonly VoxelObjectKinds _kinds = new();
        private readonly VoxelModels _models;
        private readonly VoxelObjects _objectModels;
        private readonly ObjectWorld _objects;
        private int _frame;

        public ObjectScene(WorldMeshesModelsTests test, bool fill = true)
        {
            _test = test;
            var shape = new VoxelObjectShape(5, new VoxelCoord(40, 150, 40), new VoxelCoord(100, 0, 100));
            shape.FillCapsule(100, 0, 100, 100, 600, 100, 15, Bark);
            shape.FillEllipsoid(130, 640, 100, 60, 90, 60, Bark);
            _kinds.Register("tree", shape);
            var placements = new List<ObjectPlacement>();
            for (var i = 0; i < 15; i++)
            {
                placements.Add(
                    new ObjectPlacement(
                        new ObjectId((ulong)i + 1),
                        default,
                        new VoxelCoord(200 + 400 * i, 0, 37),
                        i * 271 % 4096
                    )
                );
            }

            Placements = placements;
            var generator = new RowGenerator(placements);
            var settings = new VoxelSettings
            {
                // The grid's own: 59.2 m, so the levels split as a 10 cm world's do.
                StreamingRadiusMetres = 0,
                Levels = 4,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 2,
                Seed = 3,
            };
            World = new VoxelWorld(
                settings,
                0,
                generator,
                NullLogger<VoxelWorld>.Instance,
                kinds: _kinds,
                objectHandOverLevel: 2
            );
            World.Initialize();
            World.SetCenters([new Vector3(0, 3, 0)]);
            TestWorlds.WaitUntilLoaded(World);
            _models = new VoxelModels(World);
            _objectModels = new VoxelObjects(
                _kinds,
                World,
                _models,
                new NothingPassable(),
                NullLogger<VoxelObjects>.Instance
            );
            _objectModels.Initialize();
            _objects = new ObjectWorld(
                World,
                _kinds,
                generator,
                Options.Create(new EngineOptions()),
                NullLogger<ObjectWorld>.Instance
            );
            _objects.Initialize();
            if (fill)
            {
                FillAreas();
            }

            Meshes = _test.NewMeshes(_test.Targets, levels: 4);
            Meshes.BeginMatch(World, _models, new NoVoxelTintMap(), _objects, _objectModels);
        }

        public VoxelWorld World { get; }

        public WorldMeshes Meshes { get; }

        /// <summary>The image height every frame's view has, in pixels.</summary>
        public const uint Height = 1440;

        /// <summary>How far a tree's voxels reach from its anchor in any direction, in metres.</summary>
        public float TreeRadiusMetres => WorldMeshes.KindRadiusMetres(_kinds[default]);

        /// <summary>Fills every area of objects the world wants.</summary>
        public void FillAreas()
        {
            for (var i = 0; i < 100 && _objects.WaitingAreas > 0; i++)
            {
                _objects.Update();
            }
        }

        public List<ObjectPlacement> Placements { get; }

        public void Dispose()
        {
            Meshes.Dispose();
            _models.Dispose();
            World.Dispose();
        }

        /// <summary>
        /// Frames until the world's meshes, their stitched seams, and the kinds' models are in, from
        /// <paramref name="at"/>: a stitch that ends later, on a busy machine, is installed in a later frame.
        /// </summary>
        public void Settle(Vector3 at, bool shadows)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var parts = _objectModels.PartsOf(default).ToArray();
            while (!Meshes.IsUpToDate || !Meshes.StitchesSettled || !parts.All(part => _models.IsUploaded(part.Model)))
            {
                Assert.True(DateTime.UtcNow < deadline, "The meshes did not catch up.");
                World.Update(0);
                Frame(at, shadows);
                Thread.Sleep(1);
            }

            // Two more, so the splits the last frame chose are the ones drawn.
            Frame(at, shadows);
            Frame(at, shadows);
        }

        public int Frame(Vector3 at, bool shadows)
        {
            var slot = _frame++ % FrameSync.FramesInFlight;
            var view = new ViewConstants
            {
                Position = at,
                Forward = new Vector3(0, -1, 0),
                Right = new Vector3(4, 0, 0),
                Up = new Vector3(0, 0, -4),
                MaxDistance = 400,
                Width = Height,
                Height = Height,
            };
            if (shadows)
            {
                view.SunDirection = ViewConstants.DefaultSunDirection;
                view.Switches = ViewSwitches.Shadows;
            }

            var count = WorldMeshesTestBase.Frame(Meshes, slot, _frame / 60.0, in view);
            _test.Targets.Apply(Meshes.Regions(slot, count));
            return slot;
        }

        /// <summary>The corners of the model draws in list <paramref name="list"/> of <paramref name="slot"/>.</summary>
        public List<Vector3> Corners(int slot, int list)
        {
            var count = list switch
            {
                0 => Meshes.Frame.ModelCamera.Records,
                1 => Meshes.Frame.ModelNear.Records,
                _ => Meshes.Frame.ModelFar.Records,
            };
            var corners = new List<Vector3>();
            foreach (var record in _test.Targets.ModelDrawsOf(slot, list)[..count])
            {
                corners.Add(record.Corner);
            }

            return corners;
        }

        /// <summary>Where the parts of a placement's tree are drawn from: its anchor, turned by its heading.</summary>
        public Vector3[] ExpectedCorners(ObjectPlacement placement)
        {
            var turn = ObjectTurns.Rotation(placement.Heading);
            return _objectModels
                .PartsOf(placement.Kind)
                .ToArray()
                .Select(part => Local(placement.Anchor) + Vector3.Transform(part.Corner, turn))
                .ToArray();
        }

        public static Vector3 Local(VoxelCoord anchor) => new Vector3(anchor.X, anchor.Y, anchor.Z) / 100;

        /// <summary>True when one of the level-2 columns under the placement's reach is in <paramref name="split"/>.</summary>
        public bool SplitUnder(ObjectPlacement placement, IReadOnlyCollection<(int X, int Z)> split)
        {
            var reach = _kinds[placement.Kind].ReachCentimetres;
            var width = _objects.AreaCentimetres;
            for (
                var z = Math.Floor((placement.Anchor.Z - reach) / (double)width);
                z <= Math.Floor((placement.Anchor.Z + reach) / (double)width);
                z++
            )
            {
                for (
                    var x = Math.Floor((placement.Anchor.X - reach) / (double)width);
                    x <= Math.Floor((placement.Anchor.X + reach) / (double)width);
                    x++
                )
                {
                    if (split.Contains(((int)x, (int)z)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    private sealed class RowGenerator(List<ObjectPlacement> placements) : IChunkGenerator, IObjectPlacer
    {
        private readonly FlatTestGenerator _ground = new(s_objectGrid);

        public VoxelGrid Grid => s_objectGrid;

        public string Id => "test.row";

        public int Version => 1;

        public IObjectPlacer Objects => this;

        public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) =>
            _ground.DescribeColumn(column, seed, layers);

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels) => _ground.Fill(chunk, seed, voxels);

        public void Find(ulong seed, int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found)
        {
            foreach (var placement in placements)
            {
                if (
                    placement.Anchor.X >= minX
                    && placement.Anchor.X < maxX
                    && placement.Anchor.Z >= minZ
                    && placement.Anchor.Z < maxZ
                )
                {
                    found.Add(placement);
                }
            }
        }
    }
}
