using System.Numerics;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// A world for movers on other threads (<see cref="VoxelCharacterMover.CreateWorker"/>): flat 1 m voxels read in 10 cm
/// cells, with mesh colliders and voxel objects (blocks 1 m square, 30 cm high) with their damage, and crates, blocks,
/// and craters made and taken away between steps (<see cref="Change"/>). A test makes its own.
/// </summary>
internal sealed class WorkersWorld : IDisposable
{
    public const byte Stone = 40;

    private readonly VoxelModels _models;
    private readonly ObjectDamage _damage;
    private readonly PhysicsWorld _physics;
    private readonly VoxelObjectKinds _kinds = new();
    private readonly VoxelObjectKindId _block;
    private readonly List<int> _crates = [];
    private readonly List<ObjectPlacement> _blocks = [];

    public WorkersWorld()
    {
        // 1 m square, 30 cm high, of 10 cm voxels, its anchor its bottom's middle.
        var shape = new VoxelObjectShape(10, new VoxelCoord(10, 3, 10), new VoxelCoord(50, 0, 50));
        shape.FillBox(0, 0, 0, 100, 30, 100, Stone);
        _block = _kinds.Register("block", shape);
        var generator = new FlatTestGenerator(new VoxelGrid(100));
        Voxels = new VoxelWorld(
            new VoxelSettings
            {
                StreamingRadiusMetres = 20,
                Levels = 1,
                MemoryBudgetMegabytes = 256,
                WorkerThreads = 2,
                Seed = 17,
            },
            0,
            generator,
            NullLogger<VoxelWorld>.Instance,
            kinds: _kinds
        );
        Voxels.Initialize();
        Voxels.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(Voxels);
        _models = new VoxelModels(Voxels);
        var options = Options.Create(new EngineOptions());
        Objects = new ObjectWorld(Voxels, _kinds, generator, options, NullLogger<ObjectWorld>.Instance);
        var kindModels = new VoxelObjects(_kinds, Voxels, _models, Passability, NullLogger<VoxelObjects>.Instance);
        _damage = new ObjectDamage(
            Voxels,
            Objects,
            _kinds,
            kindModels,
            _models,
            options,
            NullLogger<ObjectDamage>.Instance
        );
        kindModels.Initialize();
        Objects.Initialize();
        _damage.Initialize();
        for (var i = 0; i == 0 || (i < 100 && Objects.WaitingAreas > 0); i++)
        {
            Objects.Update();
            _damage.Update();
        }

        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        Colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
    }

    public VoxelWorld Voxels { get; }

    public ObjectWorld Objects { get; }

    public MeshColliders Colliders { get; }

    public NothingPassable Passability { get; } = new();

    public void Dispose()
    {
        Colliders.Dispose();
        _physics.Dispose();
        _damage.Dispose();
        _models.Dispose();
        Voxels.Dispose();
    }

    /// <summary>A mover of its own over the world, its colliders, and its objects with their damage, in 10 cm cells.</summary>
    public VoxelCharacterMover Mover() =>
        new(Voxels, 10, Passability, meshes: Colliders)
        {
            Objects = new ObjectColumns(Objects, _kinds, Passability, 10, _damage),
        };

    /// <summary>Rays of their own over the world, its colliders, and its objects with their damage.</summary>
    public CollisionSources Sources() =>
        new(Colliders, new NoVoxelSurfaceTags(), Voxels, new ObjectRays(Objects, _kinds, Voxels, _damage));

    /// <summary>
    /// Places or takes away a crate or a block at random within <paramref name="metres"/> of the middle, or at every
    /// eighth <paramref name="step"/> now and then digs a crater.
    /// </summary>
    public void Change(Random random, int step, float metres = 12)
    {
        var at = new Vector3((random.NextSingle() * 2 - 1) * metres, 0, (random.NextSingle() * 2 - 1) * metres);
        switch (random.Next(7))
        {
            case 0:
            case 1:
                _crates.Add(
                    new TestTriangles()
                        .Box(new Vector3(at.X - 0.3f, -0.1f, at.Z - 0.3f), new Vector3(at.X + 0.3f, 0.5f, at.Z + 0.3f))
                        .AddTo(Colliders)
                );
                break;
            case 2 when _crates.Count > 0:
                var crate = random.Next(_crates.Count);
                Colliders.Remove(_crates[crate]);
                _crates.RemoveAt(crate);
                break;
            case 3:
            case 4:
                var anchor = new VoxelCoord((int)(at.X * 100), 0, (int)(at.Z * 100));
                if (
                    Objects.TryPlace(
                        _block,
                        anchor,
                        random.Next(ObjectPlacement.HeadingSteps),
                        VoxelEdit.NoMaker,
                        out var block
                    )
                )
                {
                    _blocks.Add(block);
                }

                break;
            case 5 when _blocks.Count > 0:
                var removed = random.Next(_blocks.Count);
                Assert.True(Objects.TryRemove(_blocks[removed], VoxelEdit.NoMaker));
                _blocks.RemoveAt(removed);
                break;
            case 6 when step % 8 == 0:
                var centre = new VoxelCoord((int)(at.X * 100), 0, (int)(at.Z * 100));
                Voxels.Apply(VoxelEdit.Sphere(centre, 60, VoxelEditOperation.Clear));
                Voxels.ApplyPendingEdits(64);
                break;
        }
    }
}
