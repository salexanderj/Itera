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
/// The character mover's workers (<see cref="VoxelCharacterMover.CreateWorker"/>) moving bodies on other threads at
/// once: over 1 m voxels in 10 cm cells among mesh colliders, voxel objects, and craters that change between steps,
/// their moves are a mover's of its own, bit for bit, with every chunk prepared or none (then made by each worker
/// itself); a prepared pass leaves a worker nothing to make, and a worker's move allocates nothing; among loose balls
/// on 1 cm voxels too; a worker refuses to begin a pass; and in Debug a mover refuses a second move while one runs.
/// </summary>
[RunsLong]
public sealed class MoverWorkersTests : IDisposable
{
    private const byte Stone = 40;
    private const float Step = 1f / 60;
    private const int Workers = 4;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.4f);

    private readonly VoxelWorld _world;
    private readonly VoxelModels _models;
    private readonly ObjectWorld _objects;
    private readonly ObjectDamage _damage;
    private readonly PhysicsWorld _physics;
    private readonly MeshColliders _colliders;
    private readonly VoxelObjectKinds _kinds = new();
    private readonly NothingPassable _passability = new();
    private readonly VoxelObjectKindId _block;

    public MoverWorkersTests()
    {
        // 1 m square, 30 cm high, of 10 cm voxels, its anchor its bottom's middle.
        var shape = new VoxelObjectShape(10, new VoxelCoord(10, 3, 10), new VoxelCoord(50, 0, 50));
        shape.FillBox(0, 0, 0, 100, 30, 100, Stone);
        _block = _kinds.Register("block", shape);
        var generator = new FlatTestGenerator(new VoxelGrid(100));
        _world = new VoxelWorld(
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
        _world.Initialize();
        _world.SetCenters([Vector3.Zero]);
        TestWorlds.WaitUntilLoaded(_world);
        _models = new VoxelModels(_world);
        var options = Options.Create(new EngineOptions());
        _objects = new ObjectWorld(_world, _kinds, generator, options, NullLogger<ObjectWorld>.Instance);
        var kindModels = new VoxelObjects(_kinds, _world, _models, _passability, NullLogger<VoxelObjects>.Instance);
        _damage = new ObjectDamage(
            _world,
            _objects,
            _kinds,
            kindModels,
            _models,
            options,
            NullLogger<ObjectDamage>.Instance
        );
        kindModels.Initialize();
        _objects.Initialize();
        _damage.Initialize();
        for (var i = 0; i == 0 || (i < 100 && _objects.WaitingAreas > 0); i++)
        {
            _objects.Update();
            _damage.Update();
        }

        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _damage.Dispose();
        _models.Dispose();
        _world.Dispose();
    }

    /// <summary>
    /// 64 bodies dropped and thrown about, among crates, blocks, and craters made and taken away between steps, some
    /// falling fast or thrown farther than a move goes: each step a mover of its own moves them, and four workers of
    /// another mover move the same bodies at once, in a pass that prepared every body's chunks or none.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Workers_on_other_threads_move_bodies_as_a_mover_of_its_own_does_bit_for_bit(bool prepared)
    {
        var reference = Mover();
        var owner = Mover();
        var workers = new VoxelCharacterMover[Workers];
        for (var w = 0; w < Workers; w++)
        {
            workers[w] = owner.CreateWorker();
        }

        var random = new Random(prepared ? 31 : 37);
        var feet = new Vector3[64];
        var velocities = new Vector3[64];
        for (var i = 0; i < feet.Length; i++)
        {
            feet[i] = new Vector3((i % 8 - 3.5f) * 1.5f, random.NextSingle() * 3, (i / 8 - 3.5f) * 1.5f);
        }

        var crates = new List<int>();
        var blocks = new List<ObjectPlacement>();
        // Crates and blocks among the bodies from the start.
        for (var i = 0; i < 60; i++)
        {
            Change(random, crates, blocks, step: 1, metres: 6);
        }

        var (expectedFeet, expectedVelocities) = (new Vector3[64], new Vector3[64]);
        var (gotFeet, gotVelocities) = (new Vector3[64], new Vector3[64]);
        var (expected, got) = (new CharacterMoveResult[64], new CharacterMoveResult[64]);
        int walls = 0,
            grounded = 0;
        for (var step = 0; step < 40; step++)
        {
            Change(random, crates, blocks, step);
            for (var i = 0; i < feet.Length; i++)
            {
                velocities[i] = Velocity(random, velocities[i]);
            }

            for (var i = 0; i < feet.Length; i++)
            {
                (expectedFeet[i], expectedVelocities[i]) = (feet[i], velocities[i]);
                expected[i] = reference.Move(ref expectedFeet[i], ref expectedVelocities[i], in s_shape, Step);
                walls += expected[i].HitWall ? 1 : 0;
                grounded += expected[i].Grounded ? 1 : 0;
            }

            owner.BeginPass();
            for (var i = 0; i < feet.Length && prepared; i++)
            {
                owner.Prepare(feet[i], in s_shape, velocities[i], Step);
            }

            // What the workers leave alone: their owner's chunks and reads, the colliders' marks, and the objects'.
            var untouched = (owner.Kept, _colliders.LastMark, _objects.Gathered);
            Parallel.For(
                0,
                Workers,
                w =>
                {
                    for (var i = w; i < feet.Length; i += Workers)
                    {
                        (gotFeet[i], gotVelocities[i]) = (feet[i], velocities[i]);
                        got[i] = workers[w].Move(ref gotFeet[i], ref gotVelocities[i], in s_shape, Step);
                    }
                }
            );
            Assert.Equal(untouched, (owner.Kept, _colliders.LastMark, _objects.Gathered));
            owner.EndPass();

            for (var i = 0; i < feet.Length; i++)
            {
                if (gotFeet[i] != expectedFeet[i] || gotVelocities[i] != expectedVelocities[i] || got[i] != expected[i])
                {
                    Assert.Fail(
                        $"Step {step}, body {i}: a worker moved it to {gotFeet[i]} at {gotVelocities[i]} ({got[i]}), "
                            + $"a mover of its own to {expectedFeet[i]} at {expectedVelocities[i]} ({expected[i]})."
                    );
                }
            }

            (feet, velocities) = ((Vector3[])expectedFeet.Clone(), (Vector3[])expectedVelocities.Clone());
        }

        // The bodies met the crates' walls and stood on the ground, the blocks, and the crates.
        Assert.True(walls > 20 && grounded > 500, $"{walls} moves met a wall, {grounded} stood.");
        var built = workers.Sum(worker => worker.OwnBuilds);
        if (prepared)
        {
            Assert.Equal(0, built);
            // A worker's move allocates nothing once it has moved a body: the same move again, prepared.
            owner.BeginPass();
            owner.Prepare(feet[0], in s_shape, velocities[0], Step);
            var (again, velocity) = (feet[0], velocities[0]);
            workers[0].Move(ref again, ref velocity, in s_shape, Step);
            var before = Itera.TestSupport.SystemAllocation.StartMeasurement();
            for (var i = 0; i < 10; i++)
            {
                (again, velocity) = (feet[0], velocities[0]);
                workers[0].Move(ref again, ref velocity, in s_shape, Step);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            owner.EndPass();
        }
        else
        {
            Assert.True(built > 0, "No worker made a chunk of its own.");
        }
    }

    /// <summary>
    /// 16 bodies walking among six balls rolling on 1 cm voxels: each step four workers of a mover move them at once,
    /// keeping no brick of the balls they read, then a mover of its own moves the same bodies.
    /// </summary>
    [Fact]
    public void Workers_among_loose_balls_move_bodies_as_a_mover_of_its_own_does_and_keep_no_brick()
    {
        using var world = TestWorlds.Load(new BoxesGenerator(200), new Vector3(0, 2, 0), radiusMetres: 5);
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        physics.Initialize();
        var passability = new MoverWorldFixture.LeavesPass();
        using var colliders = new VoxelColliders(physics, world, passability, NullLogger<VoxelColliders>.Instance);
        colliders.Initialize();
        var bodies = new LooseBodies(
            physics,
            world,
            colliders,
            new VoxelModels(world),
            passability,
            Options.Create(new EngineOptions()),
            NullLogger<LooseBodies>.Instance
        );
        var voxels = new LooseBodyVoxels(bodies, world, passability);
        var reference = new VoxelCharacterMover(world, passability, voxels);
        var owner = new VoxelCharacterMover(world, passability, voxels);
        var workers = Enumerable.Range(0, Workers).Select(_ => owner.CreateWorker()).ToArray();
        var random = new Random(41);
        for (var i = 0; i < 6; i++)
        {
            Assert.True(
                world.TryLift(
                    VoxelEdit.Sphere(new VoxelCoord(-300 + 40 * i, 188, 200), 12, VoxelEditOperation.Lift),
                    out var piece
                )
            );
            Assert.True(
                bodies.TryCreate(
                    piece,
                    new Vector3(random.NextSingle() * 2 - 1, 2.3f, random.NextSingle() * 2 - 1),
                    new Vector3(random.NextSingle() * 2 - 1, 0, random.NextSingle() * 2 - 1),
                    out _
                )
            );
        }

        var feet = Enumerable.Range(0, 16).Select(i => new Vector3(i % 4 - 1.5f, 2, i / 4 - 1.5f)).ToArray();
        var velocities = new Vector3[feet.Length];
        var (expectedFeet, expectedVelocities) = (new Vector3[feet.Length], new Vector3[feet.Length]);
        var (gotFeet, gotVelocities) = (new Vector3[feet.Length], new Vector3[feet.Length]);
        var (expected, got) = (new CharacterMoveResult[feet.Length], new CharacterMoveResult[feet.Length]);
        for (var step = 0; step < 120; step++)
        {
            colliders.Update(Step);
            physics.Step(Step);
            voxels.Refresh();
            for (var i = 0; i < feet.Length; i++)
            {
                var toMiddle = -new Vector3(feet[i].X, 0, feet[i].Z) * 2;
                velocities[i] = (step + i) % 30 == 0 ? Velocity(random, velocities[i]) : velocities[i];
                velocities[i] = new Vector3(
                    velocities[i].X + toMiddle.X * Step,
                    velocities[i].Y - 9.81f * Step,
                    velocities[i].Z + toMiddle.Z * Step
                );
            }

            // The workers first, so they read each brick first since the refresh.
            var kept = voxels.KeptBricks;
            owner.BeginPass();
            Parallel.For(
                0,
                Workers,
                w =>
                {
                    for (var i = w; i < feet.Length; i += Workers)
                    {
                        (gotFeet[i], gotVelocities[i]) = (feet[i], velocities[i]);
                        got[i] = workers[w].Move(ref gotFeet[i], ref gotVelocities[i], in s_shape, Step);
                    }
                }
            );
            owner.EndPass();
            Assert.Equal(kept, voxels.KeptBricks);
            for (var i = 0; i < feet.Length; i++)
            {
                (expectedFeet[i], expectedVelocities[i]) = (feet[i], velocities[i]);
                expected[i] = reference.Move(ref expectedFeet[i], ref expectedVelocities[i], in s_shape, Step);
                if (gotFeet[i] != expectedFeet[i] || gotVelocities[i] != expectedVelocities[i] || got[i] != expected[i])
                {
                    Assert.Fail(
                        $"Step {step}, body {i}: a worker gave {got[i]} at {gotFeet[i]}, a mover {expected[i]}."
                    );
                }
            }

            (feet, velocities) = ((Vector3[])expectedFeet.Clone(), (Vector3[])expectedVelocities.Clone());
        }

        Assert.True(voxels.KeptBricks > 0, "The mover of its own read no ball.");
    }

    [Fact]
    public void A_worker_begins_no_pass_and_makes_no_worker()
    {
        var worker = Mover().CreateWorker();

        Assert.Throws<InvalidOperationException>(worker.BeginPass);
        Assert.Throws<InvalidOperationException>(() => worker.CreateWorker());
        Assert.Throws<InvalidOperationException>(() => worker.Prepare(Vector3.Zero, in s_shape, Vector3.Zero, Step));
    }

#if DEBUG
    [Fact]
    public void In_debug_a_mover_refuses_a_second_move_while_one_runs()
    {
        var mover = Mover();
        var busy = typeof(VoxelCharacterMover).GetField(
            "_busy",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
        )!;
        busy.SetValue(mover, 1);
        var (feet, velocity) = (new Vector3(0, 0.2f, 0), Vector3.Zero);

        Assert.Throws<InvalidOperationException>(() => mover.Move(ref feet, ref velocity, in s_shape, Step));
        busy.SetValue(mover, 0);
        mover.Move(ref feet, ref velocity, in s_shape, Step);
    }
#endif

    /// <summary>
    /// A mover of its own over the world, its colliders, and its objects with their damage, in 10 cm cells.
    /// </summary>
    private VoxelCharacterMover Mover() =>
        new(_world, 10, _passability, meshes: _colliders)
        {
            Objects = new ObjectColumns(_objects, _kinds, _passability, 10, _damage),
        };

    /// <summary>
    /// A velocity for the next step: gravity on the last; now and then a new walk, a jump, a fast fall, or a throw past
    /// the farthest a move goes.
    /// </summary>
    private static Vector3 Velocity(Random random, Vector3 last)
    {
        var velocity = last with { Y = last.Y - 9.81f * Step };
        switch (random.Next(12))
        {
            case 0:
            case 1:
                var angle = random.NextSingle() * 2 * MathF.PI;
                var speed = random.NextSingle() * 8;
                return new Vector3(MathF.Cos(angle) * speed, velocity.Y, MathF.Sin(angle) * speed);
            case 2:
                return velocity with { Y = 5 };
            case 3:
                return velocity with { Y = -100 };
            case 4:
                return new Vector3(random.NextSingle() * 600 - 300, velocity.Y, random.NextSingle() * 600 - 300);
            default:
                return velocity;
        }
    }

    /// <summary>
    /// Places or takes away a crate or a block at random within <paramref name="metres"/> of the middle, or now and
    /// then digs a crater.
    /// </summary>
    private void Change(Random random, List<int> crates, List<ObjectPlacement> blocks, int step, float metres = 12)
    {
        var at = new Vector3((random.NextSingle() * 2 - 1) * metres, 0, (random.NextSingle() * 2 - 1) * metres);
        switch (random.Next(7))
        {
            case 0:
            case 1:
                crates.Add(
                    new TestTriangles()
                        .Box(new Vector3(at.X - 0.3f, -0.1f, at.Z - 0.3f), new Vector3(at.X + 0.3f, 0.5f, at.Z + 0.3f))
                        .AddTo(_colliders)
                );
                break;
            case 2 when crates.Count > 0:
                var crate = random.Next(crates.Count);
                _colliders.Remove(crates[crate]);
                crates.RemoveAt(crate);
                break;
            case 3:
            case 4:
                var anchor = new VoxelCoord((int)(at.X * 100), 0, (int)(at.Z * 100));
                if (
                    _objects.TryPlace(
                        _block,
                        anchor,
                        random.Next(ObjectPlacement.HeadingSteps),
                        VoxelEdit.NoMaker,
                        out var block
                    )
                )
                {
                    blocks.Add(block);
                }

                break;
            case 5 when blocks.Count > 0:
                var removed = random.Next(blocks.Count);
                Assert.True(_objects.TryRemove(blocks[removed], VoxelEdit.NoMaker));
                blocks.RemoveAt(removed);
                break;
            case 6 when step % 8 == 0:
                var centre = new VoxelCoord((int)(at.X * 100), 0, (int)(at.Z * 100));
                _world.Apply(VoxelEdit.Sphere(centre, 60, VoxelEditOperation.Clear));
                _world.ApplyPendingEdits(64);
                break;
        }
    }
}
