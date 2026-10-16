using Itera.Engine.Matches;
using Itera.Engine.Objects;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Objects;

/// <summary>
/// The objects' far copies in the world's voxels (<see cref="ObjectCopies"/>): a copy keeps its object's size (its shares
/// sum to the object's volume, a voxel wholly inside it is solid, nothing far from it is filled) at any heading, keeps a
/// thin trunk a voxel wide, and takes the commonest material near; the share grids made from a kind and from an
/// undamaged object's voxels are the same; a column job at a copied level writes the copies into its air (layers of air
/// they reach too) and leaves the ground; finer than the hand-over level and coarser than a kind's reach, none; the
/// match's identity carries the kinds; and a pinned hash of copied columns.
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
    /// For world voxels of 40, 80, and 160 cm at four headings: the copy's shares sum to the object's volume (within a
    /// tenth), every world voxel wholly inside the object is solid, and no world voxel more than a voxel past the object
    /// holds any share.
    /// </summary>
    [Theory]
    [InlineData(40, 7, 3, -11)]
    [InlineData(40, 23, -17, 31)]
    [InlineData(80, 7, 3, -11)]
    [InlineData(80, 61, 29, -47)]
    [InlineData(160, 7, 3, -11)]
    [InlineData(160, 113, -71, 37)]
    public void A_copy_keeps_its_objects_size(int edge, int ax, int ay, int az)
    {
        var kind = _kinds[new VoxelObjectKindId(0)];
        var copy = GridFor(edge);
        var volume = 0L;
        for (var i = 0; i < _shape.Size.X * _shape.Size.Y * _shape.Size.Z; i++)
        {
            var (x, y, z) = (i % _shape.Size.X, i / _shape.Size.X % _shape.Size.Y, i / (_shape.Size.X * _shape.Size.Y));
            volume += _shape.MaterialAt(x, y, z) != VoxelConstants.Air ? 125 : 0;
        }

        foreach (var heading in (int[])[0, 300, 1024, 2900])
        {
            var placement = new ObjectPlacement(new ObjectId(1), default, new VoxelCoord(ax, ay, az), heading);
            var (x0, y0, z0, sx, sy, sz) = ObjectCopies.RangeOf(copy, in placement, edge);
            var shares = new byte[sx * sy * sz];
            var materials = new byte[shares.Length];
            ObjectCopies.Shares(
                copy,
                kind.AnchorCentimetres,
                in placement,
                edge,
                x0,
                y0,
                z0,
                sx,
                sy,
                sz,
                shares,
                materials
            );

            var sum = 0.0;
            for (var z = 0; z < sz; z++)
            {
                for (var y = 0; y < sy; y++)
                {
                    for (var x = 0; x < sx; x++)
                    {
                        var share = shares[x + sx * (y + sy * z)];
                        sum += share / 255.0 * edge * edge * edge;
                        var (inside, outside) = Fill(kind, in placement, x0 + x, y0 + y, z0 + z, edge);
                        if (inside)
                        {
                            Assert.True(
                                share >= VoxelDensity.Solid,
                                $"Heading {heading}: ({x}, {y}, {z}) holds {share}."
                            );
                        }

                        if (outside && Far(kind, in placement, x0 + x, y0 + y, z0 + z, edge))
                        {
                            Assert.Equal(0, share);
                        }
                    }
                }
            }

            Assert.InRange(sum / volume, 0.9, 1.1);
        }
    }

    /// <summary>
    /// At 160 cm, four times the trunk's 44 cm, the trunk is kept a voxel wide at every height from its foot to the crown:
    /// each row of voxels round its axis holds a solid one, of bark.
    /// </summary>
    [Fact]
    public void A_copy_keeps_a_thin_trunk_a_voxel_wide()
    {
        const int edge = 160;
        var kind = _kinds[new VoxelObjectKindId(0)];
        var copy = GridFor(edge);
        foreach (var heading in (int[])[0, 1024, 2900])
        {
            var placement = new ObjectPlacement(new ObjectId(1), default, new VoxelCoord(70, 0, -30), heading);
            var (x0, y0, z0, sx, sy, sz) = ObjectCopies.RangeOf(copy, in placement, edge);
            var shares = new byte[sx * sy * sz];
            var materials = new byte[shares.Length];
            var marks = new byte[shares.Length];
            ObjectCopies.Shares(
                copy,
                kind.AnchorCentimetres,
                in placement,
                edge,
                x0,
                y0,
                z0,
                sx,
                sy,
                sz,
                shares,
                materials
            );
            ObjectCopies.KeepThin(shares, materials, sx, sy, sz, marks);
            // Rows from the first wholly over the ground to the last under the crown (the crown starts 580 cm up).
            for (var y = 1; y * edge + edge <= 560; y++)
            {
                var held = false;
                for (var z = 0; z < sz; z++)
                {
                    for (var x = 0; x < sx; x++)
                    {
                        var i = x + sx * (y - y0 + sy * z);
                        held |= shares[i] >= VoxelDensity.Solid && materials[i] == Bark;
                    }
                }

                Assert.True(held, $"Heading {heading}: no trunk in row {y}.");
            }
        }
    }

    /// <summary>
    /// The share grids made from the kind's parts and from an undamaged object's voxels read one by one (as the labeller
    /// reads a snapshot) are the same, block for block, at every level.
    /// </summary>
    [Fact]
    public void A_kinds_share_grids_are_those_of_its_voxels()
    {
        var kind = _kinds[new VoxelObjectKindId(0)];
        var fromParts = kind.SharesFrom(40, 3);
        var builder = new ObjectShareBuilder(kind.Size, kind.UnitCentimetres, 40, kind.Materials);
        for (var z = 0; z < kind.Size.Z; z++)
        {
            for (var y = 0; y < kind.Size.Y; y++)
            {
                for (var x = 0; x < kind.Size.X; x++)
                {
                    kind.Voxel(x, y, z, out var material, out var density);
                    builder.Add(x, y, z, material, density);
                }
            }
        }

        var fromVoxels = builder.Finish(3, kind.AnchorCentimetres);
        for (var level = 0; level < 3; level++)
        {
            var block = fromParts[level].BlockCentimetres;
            Assert.Equal(block, fromVoxels[level].BlockCentimetres);
            for (var z = 0L; z < kind.Size.Z * 5; z += block)
            {
                for (var y = 0L; y < kind.Size.Y * 5; y += block)
                {
                    for (var x = 0L; x < kind.Size.X * 5; x += block)
                    {
                        var a = fromParts[level].ShareAt(x, y, z, out var ma);
                        var b = fromVoxels[level].ShareAt(x, y, z, out var mb);
                        Assert.Equal((a, ma), (b, mb));
                    }
                }
            }
        }
    }

    /// <summary>The copy of the trunk is bark and of the crown leaves: the commonest material where it is fullest.</summary>
    [Fact]
    public void A_copys_voxels_take_the_commonest_material_near()
    {
        Assert.Equal(Bark, MaterialAt(0, 0, 300, 0));
        Assert.Equal(Leaves, MaterialAt(0, 50, 700, 0));
        // Turned a quarter, its own +x is the world's +z: the crown leans that way.
        Assert.Equal(Leaves, MaterialAt(1024, 0, 700, 50));
        Assert.Equal(VoxelConstants.Air, MaterialAt(1024, 0, 700, -160));
    }

    /// <summary>
    /// A level-2 column holding three trees (one turned, one with its foot half a metre under the ground): its job writes
    /// exactly the copies' shares into the air above the flat ground (solid from a half, the first tree's where two
    /// meet), in the layers the trees reach, and the stone below stays stone.
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
        var expected = Expected(placements, 40);
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
                        var i = x + 128 * (y + 128 * z);
                        if (level.SampleAt(origin.Y + y) < 0)
                        {
                            Assert.Equal(FlatTestGenerator.Stone, materials[i]);
                            continue;
                        }

                        var (material, density) = expected.GetValueOrDefault(
                            (origin.X + x, origin.Y + y, origin.Z + z),
                            (VoxelConstants.Air, VoxelDensity.Empty)
                        );
                        Assert.Equal((material, density), (materials[i], densities[i]));
                        copied += material is Bark or Leaves ? 1 : 0;
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
        var expected = Expected(placements, 40);
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

                        var material =
                            sy < HighGround.Ground
                                ? FlatTestGenerator.Stone
                                : expected
                                    .GetValueOrDefault(
                                        (origin.X + x, origin.Y + y, origin.Z + z),
                                        (VoxelConstants.Air, VoxelDensity.Empty)
                                    )
                                    .Item1;
                        Assert.Equal(material, actual);
                        copied += material is Bark or Leaves && layer == 1 ? 1 : 0;
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

    /// <summary>
    /// A world with object kinds is another match for each set of them, whether its generator places them or a game
    /// places them during play: the identity carries their fingerprint; with none, the generator's id alone.
    /// </summary>
    [Fact]
    public void The_match_identity_carries_the_kinds_fingerprint_where_the_run_has_kinds()
    {
        var placing = new Placing([]);
        var identity = new GeneratorMatchIdentity(placing, _kinds);
        Assert.Equal($"test.copies+objects-{_kinds.Fingerprint:x16}", identity.Id);
        Assert.Equal(1, identity.Version);
        Assert.Equal(
            $"test.flat+objects-{_kinds.Fingerprint:x16}",
            new GeneratorMatchIdentity(new FlatTestGenerator(s_grid), _kinds).Id
        );
        Assert.Equal("test.copies", new GeneratorMatchIdentity(placing, new VoxelObjectKinds()).Id);
        Assert.Equal("test.flat", new GeneratorMatchIdentity(new FlatTestGenerator(s_grid), new VoxelObjectKinds()).Id);
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
        var densities = new byte[VoxelConstants.ChunkVoxels];
        foreach (var level in (int[])[2, 3, 4, 5])
        {
            var (layers, _) = Run(generator, copies, new ColumnKey(level, 0, 0));
            foreach (var layer in layers)
            {
                ChunkTree.Decode(_memory, layer, materials);
                ChunkTree.DecodeDensities(_memory, layer, densities);
                for (var i = 0; i < materials.Length; i++)
                {
                    hash = (((hash ^ materials[i]) * 1099511628211UL) ^ densities[i]) * 1099511628211UL;
                }
            }
        }

        Assert.Equal(12369600423040893681UL, hash);
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

    /// <summary>The kind's share grid for world voxels <paramref name="edge"/> across.</summary>
    private ObjectShareGrid GridFor(int edge) =>
        _kinds[new VoxelObjectKindId(0)].SharesFrom(40, 3)[
            edge switch
            {
                40 => 0,
                80 => 1,
                _ => 2,
            }
        ];

    /// <summary>
    /// The material of the world voxel of 40 cm holding (x, y, z), centimetres from an anchor turned by
    /// <paramref name="heading"/> at the middle of a voxel (20 cm on along x and z), so the trunk fills the voxel it stands in.
    /// </summary>
    private byte MaterialAt(int heading, int x, int y, int z)
    {
        var kind = _kinds[new VoxelObjectKindId(0)];
        var placement = new ObjectPlacement(new ObjectId(1), default, new VoxelCoord(20, 0, 20), heading);
        (x, z) = (x + 20, z + 20);
        Span<byte> share = stackalloc byte[1];
        Span<byte> material = stackalloc byte[1];
        ObjectCopies.Shares(
            GridFor(40),
            kind.AnchorCentimetres,
            in placement,
            40,
            (int)Math.Floor(x / 40.0),
            (int)Math.Floor(y / 40.0),
            (int)Math.Floor(z / 40.0),
            1,
            1,
            1,
            share,
            material
        );
        return share[0] >= VoxelDensity.Solid ? material[0] : VoxelConstants.Air;
    }

    /// <summary>
    /// Whether the world voxel (x, y, z) of <paramref name="edge"/> lies wholly inside the object (its cube sampled at 5
    /// points along each edge, each turned into the object, all solid) or wholly outside it (none solid).
    /// </summary>
    private (bool Inside, bool Outside) Fill(
        VoxelObjectKind kind,
        in ObjectPlacement placement,
        int x,
        int y,
        int z,
        int edge
    )
    {
        int solid = 0;
        for (var k = 0; k < 125; k++)
        {
            var px = x * (long)edge + edge * (k % 5) / 4 - placement.Anchor.X;
            var py = y * (long)edge + edge * (k / 5 % 5) / 4 - placement.Anchor.Y;
            var pz = z * (long)edge + edge * (k / 25) / 4 - placement.Anchor.Z;
            var (ox, oz) = ObjectTurns.ToObject(placement.Heading, px, pz);
            var vx = (int)Math.Floor((ox + kind.AnchorCentimetres.X) / 5.0);
            var vy = (int)Math.Floor((py + kind.AnchorCentimetres.Y) / 5.0);
            var vz = (int)Math.Floor((oz + kind.AnchorCentimetres.Z) / 5.0);
            solid += _shape.MaterialAt(vx, vy, vz) != VoxelConstants.Air ? 1 : 0;
        }

        return (solid == 125, solid == 0);
    }

    /// <summary>True when no solid voxel of the object lies within a world voxel of the voxel (x, y, z)'s cube.</summary>
    private bool Far(VoxelObjectKind kind, in ObjectPlacement placement, int x, int y, int z, int edge)
    {
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!Fill(kind, in placement, x + dx, y + dy, z + dz, edge).Outside)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The voxels of 40 cm the copies of <paramref name="placements"/> write over flat ground, by the copies' rule: each
    /// placement's shares with its thin parts kept, solid from a half of its material at that density where no earlier
    /// placement made the voxel solid, else air at the largest share under a half; keyed by absolute voxel index.
    /// </summary>
    private Dictionary<(int X, int Y, int Z), (byte Material, byte Density)> Expected(
        ObjectPlacement[] placements,
        int edge
    )
    {
        var expected = new Dictionary<(int X, int Y, int Z), (byte Material, byte Density)>();
        var kind = _kinds[new VoxelObjectKindId(0)];
        var copy = GridFor(edge);
        foreach (var placement in placements)
        {
            var (x0, y0, z0, sx, sy, sz) = ObjectCopies.RangeOf(copy, in placement, edge);
            var shares = new byte[sx * sy * sz];
            var materials = new byte[shares.Length];
            var marks = new byte[shares.Length];
            ObjectCopies.Shares(
                copy,
                kind.AnchorCentimetres,
                in placement,
                edge,
                x0,
                y0,
                z0,
                sx,
                sy,
                sz,
                shares,
                materials
            );
            ObjectCopies.KeepThin(shares, materials, sx, sy, sz, marks);
            for (var i = 0; i < shares.Length; i++)
            {
                var key = (x0 + i % sx, y0 + i / sx % sy, z0 + i / (sx * sy));
                var (had, density) = expected.GetValueOrDefault(key, (VoxelConstants.Air, VoxelDensity.Empty));
                if (shares[i] == 0 || had != VoxelConstants.Air || key.Item2 * edge < 0)
                {
                    continue;
                }

                if (shares[i] >= VoxelDensity.Solid && materials[i] != VoxelConstants.Air)
                {
                    expected[key] = (materials[i], VoxelDensity.Fit(materials[i], shares[i]));
                }
                else if (shares[i] > density)
                {
                    expected[key] = (VoxelConstants.Air, VoxelDensity.Fit(VoxelConstants.Air, shares[i]));
                }
            }
        }

        return expected;
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
