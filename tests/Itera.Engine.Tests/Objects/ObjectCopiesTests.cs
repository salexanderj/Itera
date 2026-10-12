using Itera.Engine.Matches;
using Itera.Engine.Objects;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// The objects' far copies in the world's voxels (<see cref="ObjectCopies"/>): a copy holds every world voxel its object
/// touches at any heading and nothing far from it, of the commonest material near; a column job at a copied level writes
/// the copies into its air (layers of air they reach too) and leaves the ground; finer than the hand-over level and
/// coarser than a kind's reach, none; the match's identity carries the kinds; and a pinned hash of copied columns.
/// </summary>
public sealed class ObjectCopiesTests : IDisposable
{
    private const byte Bark = 30;
    private const byte Leaves = 31;
    private static readonly VoxelGrid s_grid = new(10);
    private readonly VoxelObjectShape _shape;
    private readonly VoxelObjectKinds _kinds = new();
    private readonly VoxelMemory _memory = new(256 << 20);

    public ObjectCopiesTests()
    {
        // A tree 3 m across and 9 m tall of 5 cm voxels, its trunk's foot the anchor and its crown leaning towards +x, so
        // a heading shows.
        _shape = new VoxelObjectShape(5, new VoxelCoord(60, 180, 60), new VoxelCoord(150, 0, 150));
        _shape.FillCapsule(150, 0, 150, 150, 650, 150, 22, Bark);
        _shape.FillEllipsoid(200, 700, 150, 90, 120, 90, Leaves);
        _kinds.Register("tree", _shape);
    }

    public void Dispose() => _memory.Dispose();

    /// <summary>
    /// For world voxels of 40, 80, and 160 cm at four headings, every world voxel whose cube holds a point of the
    /// object's solid voxels is solid in the copy, and a world voxel more than a voxel past the object's reach is air.
    /// </summary>
    [Theory]
    [InlineData(40, 7, 3, -11)]
    [InlineData(40, 23, -17, 31)]
    [InlineData(80, 7, 3, -11)]
    [InlineData(80, 61, 29, -47)]
    [InlineData(160, 7, 3, -11)]
    [InlineData(160, 113, -71, 37)]
    public void A_copy_holds_every_world_voxel_its_object_touches_and_nothing_far(int edge, int ox, int oy, int oz)
    {
        var kind = _kinds[new VoxelObjectKindId(0)];
        var copy = kind.CopyFor(edge);
        foreach (var heading in (int[])[0, 300, 1024, 2900])
        {
            var touched = 0;
            for (var z = -6 * edge; z <= 6 * edge; z += edge)
            {
                for (var y = -2 * edge; y <= 1_000 + 2 * edge; y += edge)
                {
                    for (var x = -6 * edge; x <= 6 * edge; x += edge)
                    {
                        // A world voxel whose sample point is (x, y, z) from the anchor (an odd offset off the grid).
                        var copied = CopyAt(copy, kind, heading, x + ox, y + oy, z + oz);
                        var touches = Touches(kind, heading, x + ox, y + oy, z + oz, edge);
                        touched += touches ? 1 : 0;
                        if (touches)
                        {
                            Assert.True(copied != VoxelConstants.Air, $"Heading {heading}, ({x}, {y}, {z}) is air.");
                        }

                        var across = Math.Sqrt((double)x * x + (double)z * z);
                        if (
                            across > kind.ReachCentimetres + 2 * edge
                            || y > kind.TopCentimetres + 2 * edge
                            || y < -2 * edge
                        )
                        {
                            Assert.Equal(VoxelConstants.Air, copied);
                        }
                    }
                }
            }

            Assert.True(touched > 10, $"Heading {heading} touched only {touched} voxels.");
        }
    }

    /// <summary>The copy of the trunk is bark and of the crown leaves: the commonest material a world voxel there could touch.</summary>
    [Fact]
    public void A_copys_voxels_take_the_commonest_material_near()
    {
        var kind = _kinds[new VoxelObjectKindId(0)];
        var copy = kind.CopyFor(40);

        Assert.Equal(Bark, CopyAt(copy, kind, 0, 0, 300, 0));
        Assert.Equal(Leaves, CopyAt(copy, kind, 0, 50, 700, 0));
        // Turned a quarter, its own +x is the world's +z: the crown leans that way.
        Assert.Equal(Leaves, CopyAt(copy, kind, 1024, 0, 700, 50));
        Assert.Equal(VoxelConstants.Air, CopyAt(copy, kind, 1024, 0, 700, -120));
    }

    /// <summary>
    /// A level-2 column holding three trees (one turned, one with its foot half a metre under the ground): its job writes
    /// exactly the copies into the air above the flat ground, whole, in the layers the trees reach, and the stone below
    /// stays stone.
    /// </summary>
    [Fact]
    public void A_column_job_writes_the_copies_into_its_air_and_leaves_the_ground()
    {
        var placements = new[]
        {
            new ObjectPlacement(new ObjectId(1), new VoxelObjectKindId(0), new VoxelCoord(1_000, 0, 1_000), 0),
            new ObjectPlacement(new ObjectId(2), new VoxelObjectKindId(0), new VoxelCoord(2_300, 0, 900), 1_500),
            new ObjectPlacement(new ObjectId(3), new VoxelObjectKindId(0), new VoxelCoord(1_600, -50, 2_700), 3_000),
        };
        var generator = new Placing(placements);
        var copies = ObjectCopies.Create(generator.Objects, _kinds, s_grid, 2, 8)!;
        var key = new ColumnKey(2, 0, 0);

        var (layers, first) = Run(generator, copies, key);

        var copied = 0;
        var materials = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        var level = new GridLevel(s_grid, 2);
        var kind = _kinds[new VoxelObjectKindId(0)];
        var copy = kind.CopyFor(40);
        for (var layer = 0; layer < layers.Length; layer++)
        {
            var chunk = key.Chunk(first + layer, s_grid);
            ChunkTree.Decode(_memory, layers[layer], materials);
            ChunkTree.DecodeDensities(_memory, layers[layer], densities);
            var origin = chunk.LevelOrigin;
            for (var z = 0; z < 128; z++)
            {
                for (var y = 0; y < 128; y++)
                {
                    for (var x = 0; x < 128; x++)
                    {
                        int sx = level.SampleAt(origin.X + x),
                            sy = level.SampleAt(origin.Y + y),
                            sz = level.SampleAt(origin.Z + z);
                        var expected = sy < 0 ? FlatTestGenerator.Stone : VoxelConstants.Air;
                        if (expected == VoxelConstants.Air)
                        {
                            foreach (var placement in placements)
                            {
                                var material = CopyAt(
                                    copy,
                                    kind,
                                    placement.Heading,
                                    sx - placement.Anchor.X,
                                    sy - placement.Anchor.Y,
                                    sz - placement.Anchor.Z
                                );
                                if (material != VoxelConstants.Air)
                                {
                                    expected = material;
                                    break;
                                }
                            }
                        }

                        var i = x + 128 * (y + 128 * z);
                        Assert.Equal(expected, materials[i]);
                        if (expected is Bark or Leaves)
                        {
                            copied++;
                            Assert.Equal(VoxelDensity.Full, densities[i]);
                        }
                    }
                }
            }
        }

        Assert.True(copied > 100, $"Only {copied} voxels were copied.");
    }

    /// <summary>
    /// Ground 50 m up, inside a layer the generator alone keeps (above it air, below it stone): the job widens its layers
    /// to the copies that rise into the layer above, writes them into the air of the mixed ground layer and of the layer
    /// above, keeps the ground, and an edit that clears a ball in a crown clears the copy there.
    /// </summary>
    [Fact]
    public void A_column_job_widens_its_layers_to_the_copies_and_its_edits_clear_them()
    {
        var placements = new[]
        {
            new ObjectPlacement(
                new ObjectId(1),
                new VoxelObjectKindId(0),
                new VoxelCoord(1_000, HighGround.Ground, 1_000),
                0
            ),
            new ObjectPlacement(
                new ObjectId(2),
                new VoxelObjectKindId(0),
                new VoxelCoord(2_300, HighGround.Ground, 900),
                700
            ),
        };
        var generator = new HighGround(placements);
        var copies = ObjectCopies.Create(generator, _kinds, s_grid, 2, 8)!;
        var key = new ColumnKey(2, 0, 0);
        var crown = new VoxelCoord(1_050, HighGround.Ground + 700, 1_000);
        var job = new ColumnJob();
        job.Reset(key, s_grid, 7, 0);
        job.Edits.Add(VoxelEdit.Sphere(crown, 120, VoxelEditOperation.Clear, VoxelConstants.Air));
        job.Run(
            generator,
            _memory,
            new byte[VoxelConstants.ChunkVoxels],
            new byte[VoxelConstants.ChunkVoxels],
            copies,
            []
        );
        Assert.Equal(ColumnJobResult.Done, job.Result);

        // The generator keeps the ground's layer alone; the copies reach the layer above.
        var groundLayer = HighGround.Ground / 5_120 - s_grid.MinChunkYAt(2);
        Assert.Equal(groundLayer, job.First);
        Assert.Equal(2, job.Layers.Length);
        var materials = new byte[VoxelConstants.ChunkVoxels];
        var level = new GridLevel(s_grid, 2);
        var kind = _kinds[new VoxelObjectKindId(0)];
        var copy = kind.CopyFor(40);
        int copied = 0,
            cleared = 0;
        for (var layer = 0; layer < job.Layers.Length; layer++)
        {
            ChunkTree.Decode(_memory, job.Layers[layer], materials);
            var origin = key.Chunk(job.First + layer, s_grid).LevelOrigin;
            for (var z = 0; z < 128; z++)
            {
                for (var y = 0; y < 128; y++)
                {
                    for (var x = 0; x < 128; x++)
                    {
                        int sx = level.SampleAt(origin.X + x),
                            sy = level.SampleAt(origin.Y + y),
                            sz = level.SampleAt(origin.Z + z);
                        var actual = materials[x + 128 * (y + 128 * z)];
                        double dx = sx - crown.X,
                            dy = sy - crown.Y,
                            dz = sz - crown.Z;
                        var fromCrown = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (fromCrown + 35 <= 120)
                        {
                            // Wholly inside the cleared ball.
                            Assert.Equal(VoxelConstants.Air, actual);
                            cleared++;
                            continue;
                        }

                        if (fromCrown - 35 <= 120)
                        {
                            continue;
                        }

                        var expected = sy < HighGround.Ground ? FlatTestGenerator.Stone : VoxelConstants.Air;
                        if (expected == VoxelConstants.Air)
                        {
                            foreach (var placement in placements)
                            {
                                var material = CopyAt(
                                    copy,
                                    kind,
                                    placement.Heading,
                                    sx - placement.Anchor.X,
                                    sy - placement.Anchor.Y,
                                    sz - placement.Anchor.Z
                                );
                                if (material != VoxelConstants.Air)
                                {
                                    expected = material;
                                    break;
                                }
                            }
                        }

                        Assert.Equal(expected, actual);
                        copied += expected is Bark or Leaves && layer == 1 ? 1 : 0;
                    }
                }
            }
        }

        Assert.True(copied > 50, $"Only {copied} voxels were copied into the layer above the ground's.");
        Assert.True(cleared > 10, $"Only {cleared} voxels lay wholly in the cleared ball.");
    }

    /// <summary>
    /// A world made with the kinds writes the copies as its columns load: a level-2 voxel in a tree's trunk is bark, and
    /// the levels below the hand-over hold none.
    /// </summary>
    [Fact]
    public void A_world_with_kinds_writes_the_copies_as_its_columns_load()
    {
        var placements = new[]
        {
            new ObjectPlacement(new ObjectId(1), new VoxelObjectKindId(0), new VoxelCoord(500, 0, 400), 0),
        };
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = 6,
            Levels = 4,
            MemoryBudgetMegabytes = 256,
            WorkerThreads = 2,
            Seed = 7,
        };
        using var world = new VoxelWorld(
            settings,
            0,
            new Placing(placements),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VoxelWorld>.Instance,
            kinds: _kinds,
            objectHandOverLevel: 2
        );
        world.Initialize();
        world.SetCenters([System.Numerics.Vector3.Zero]);
        Voxels.TestWorlds.WaitUntilLoaded(world);

        Assert.NotNull(world.Copies);
        var trunk = new VoxelCoord(500, 300, 400);
        Assert.True(world.TryGetMaterial(trunk, 2, out var coarse));
        Assert.Equal(Bark, coarse);
        Assert.True(world.TryGetMaterial(trunk, 1, out var fine));
        Assert.Equal(VoxelConstants.Air, fine);
        Assert.True(world.TryGetMaterial(trunk, 0, out var finest));
        Assert.Equal(VoxelConstants.Air, finest);
    }

    /// <summary>
    /// Finer than the hand-over level, and coarser than a kind's copies reach (its widest side's half, 4.5 m: level 6's
    /// 6.4 m voxels), a column's job writes no copy: its layers are those of a job with no objects.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Columns_outside_the_copied_levels_get_no_copies(int levelOfColumn)
    {
        var placements = new[]
        {
            new ObjectPlacement(new ObjectId(1), new VoxelObjectKindId(0), new VoxelCoord(500, 0, 500), 0),
        };
        var generator = new Placing(placements);
        var copies = ObjectCopies.Create(generator.Objects, _kinds, s_grid, 2, 8)!;
        Assert.Equal(2, copies.FirstLevel);
        Assert.Equal(5, copies.LastLevel);
        var key = new ColumnKey(levelOfColumn, 0, 0);

        var (with, _) = Run(generator, copies, key);
        var (without, _) = Run(generator, null, key);

        Assert.Equal(without.Length, with.Length);
        var a = new byte[VoxelConstants.ChunkVoxels];
        var b = new byte[VoxelConstants.ChunkVoxels];
        for (var i = 0; i < with.Length; i++)
        {
            ChunkTree.Decode(_memory, with[i], a);
            ChunkTree.Decode(_memory, without[i], b);
            Assert.True(a.AsSpan().SequenceEqual(b), $"Layer {i} differs.");
        }
    }

    /// <summary>A world that places objects is another match for each set of kinds: the identity carries their fingerprint.</summary>
    [Fact]
    public void The_match_identity_carries_the_kinds_fingerprint_where_the_world_places_objects()
    {
        var placing = new Placing([]);
        var identity = new GeneratorMatchIdentity(placing, _kinds);
        Assert.Equal($"test.copies+objects-{_kinds.Fingerprint:x16}", identity.Id);
        Assert.Equal(1, identity.Version);
        Assert.Equal("test.flat", new GeneratorMatchIdentity(new FlatTestGenerator(s_grid), _kinds).Id);
        Assert.Equal("test.copies", new GeneratorMatchIdentity(placing, new VoxelObjectKinds()).Id);
    }

    /// <summary>The copied columns' voxels are pinned: a change to the copies' rule or the turns must show here.</summary>
    [Fact]
    public void Copied_columns_are_pinned()
    {
        var placements = new[]
        {
            new ObjectPlacement(new ObjectId(1), new VoxelObjectKindId(0), new VoxelCoord(1_000, 0, 1_000), 0),
            new ObjectPlacement(new ObjectId(2), new VoxelObjectKindId(0), new VoxelCoord(2_300, 0, 900), 1_500),
            new ObjectPlacement(new ObjectId(3), new VoxelObjectKindId(0), new VoxelCoord(1_600, -50, 2_700), 3_000),
        };
        var generator = new Placing(placements);
        var copies = ObjectCopies.Create(generator.Objects, _kinds, s_grid, 2, 8)!;
        var hash = 14695981039346656037UL;
        var materials = new byte[VoxelConstants.ChunkVoxels];
        foreach (var level in (int[])[2, 3, 4, 5])
        {
            var (layers, _) = Run(generator, copies, new ColumnKey(level, 0, 0));
            foreach (var layer in layers)
            {
                ChunkTree.Decode(_memory, layer, materials);
                foreach (var material in materials)
                {
                    hash = (hash ^ material) * 1099511628211UL;
                }
            }
        }

        Assert.Equal(2670481203557003466UL, hash);
    }

    private (ChildState[] Layers, int First) Run(IChunkGenerator generator, ObjectCopies? copies, ColumnKey key)
    {
        var job = new ColumnJob();
        job.Reset(key, s_grid, 7, 0);
        job.Run(
            generator,
            _memory,
            new byte[VoxelConstants.ChunkVoxels],
            new byte[VoxelConstants.ChunkVoxels],
            copies,
            copies is null ? null : []
        );
        Assert.Equal(ColumnJobResult.Done, job.Result);
        return (job.Layers.ToArray(), job.First);
    }

    /// <summary>The copy's material at (x, y, z) centimetres from a placement's anchor at <paramref name="heading"/>.</summary>
    private static byte CopyAt(ObjectCopyGrid copy, VoxelObjectKind kind, int heading, long x, long y, long z)
    {
        var (ox, oz) = ObjectTurns.ToObject(heading, x, z);
        return copy.At(ox + kind.AnchorCentimetres.X, y + kind.AnchorCentimetres.Y, oz + kind.AnchorCentimetres.Z);
    }

    /// <summary>
    /// True when the world voxel <paramref name="edge"/> across whose sample point is (x, y, z) from the anchor holds a
    /// point of a solid voxel of the object: its cube sampled at 5 points along each edge, each turned into the object.
    /// </summary>
    private bool Touches(VoxelObjectKind kind, int heading, long x, long y, long z, int edge)
    {
        for (var k = 0; k < 125; k++)
        {
            var px = x - edge / 2.0 + edge * (k % 5) / 4.0;
            var py = y - edge / 2.0 + edge * (k / 5 % 5) / 4.0;
            var pz = z - edge / 2.0 + edge * (k / 25) / 4.0;
            var (ox, oz) = ObjectTurns.ToObject(heading, (long)Math.Floor(px), (long)Math.Floor(pz));
            var vx = (int)Math.Floor((ox + kind.AnchorCentimetres.X) / 5.0);
            var vy = (int)Math.Floor((py + kind.AnchorCentimetres.Y) / 5.0);
            var vz = (int)Math.Floor((oz + kind.AnchorCentimetres.Z) / 5.0);
            if (_shape.MaterialAt(vx, vy, vz) != VoxelConstants.Air)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Stone below <see cref="Ground"/> centimetres and air above, in 10 cm voxels, its range the ground's layer alone (as
    /// a heightfield's is), placing the given objects.
    /// </summary>
    private sealed class HighGround(ObjectPlacement[] placements) : IChunkGenerator, IObjectPlacer
    {
        public const int Ground = 5_000;
        private readonly Placing _placing = new(placements);

        public VoxelGrid Grid => s_grid;

        public string Id => "test.high-ground";

        public int Version => 1;

        public IObjectPlacer Objects => this;

        public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers)
        {
            var height = s_grid.ColumnCentimetres(column.Level);
            var floor = s_grid.MinChunkYAt(column.Level);
            for (var i = 0; i < layers.Length; i++)
            {
                var bottom = (floor + i) * height;
                layers[i] =
                    bottom + height <= Ground ? ChunkFill.Uniform(FlatTestGenerator.Stone)
                    : bottom >= Ground ? ChunkFill.Air
                    : ChunkFill.Mixed;
            }
        }

        public void DescribeRange(
            ColumnKey column,
            ulong seed,
            out int first,
            out int last,
            out ChunkFill below,
            out ChunkFill above
        )
        {
            first = last = (int)(Ground / s_grid.ColumnCentimetres(column.Level)) - s_grid.MinChunkYAt(column.Level);
            below = ChunkFill.Uniform(FlatTestGenerator.Stone);
            above = ChunkFill.Air;
        }

        public void Fill(ChunkKey chunk, ulong seed, Span<byte> voxels)
        {
            var level = new GridLevel(s_grid, chunk.Level);
            var origin = chunk.LevelOrigin;
            for (var y = 0; y < 128; y++)
            {
                var material = level.SampleAt(origin.Y + y) < Ground ? FlatTestGenerator.Stone : VoxelConstants.Air;
                for (var z = 0; z < 128; z++)
                {
                    voxels.Slice(128 * (y + 128 * z), 128).Fill(material);
                }
            }
        }

        public void Find(ulong seed, int minX, int maxX, int minZ, int maxZ, List<ObjectPlacement> found) =>
            _placing.Find(seed, minX, maxX, minZ, maxZ, found);
    }

    /// <summary>The flat 10 cm test ground, placing the given objects.</summary>
    private sealed class Placing(ObjectPlacement[] placements) : IChunkGenerator, IObjectPlacer
    {
        private readonly FlatTestGenerator _ground = new(s_grid);

        public VoxelGrid Grid => s_grid;

        public string Id => "test.copies";

        public int Version => 1;

        public IObjectPlacer Objects => this;

        public void DescribeColumn(ColumnKey column, ulong seed, Span<ChunkFill> layers) =>
            _ground.DescribeColumn(column, seed, layers);

        public void DescribeRange(
            ColumnKey column,
            ulong seed,
            out int first,
            out int last,
            out ChunkFill below,
            out ChunkFill above
        ) => ((IChunkGenerator)_ground).DescribeRange(column, seed, out first, out last, out below, out above);

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
