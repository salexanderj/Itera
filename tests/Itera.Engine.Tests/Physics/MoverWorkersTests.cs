using System.Numerics;
using Itera.Engine.Physics;
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
    private const float Step = 1f / 60;
    private const int Workers = 4;
    private static readonly CharacterShape s_shape = new(1.7f, 0.26f, 0.4f);

    private readonly WorkersWorld _world = new();

    public void Dispose() => _world.Dispose();

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
        var reference = _world.Mover();
        var owner = _world.Mover();
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

        // Crates and blocks among the bodies from the start.
        for (var i = 0; i < 60; i++)
        {
            _world.Change(random, step: 1, metres: 6);
        }

        var (expectedFeet, expectedVelocities) = (new Vector3[64], new Vector3[64]);
        var (gotFeet, gotVelocities) = (new Vector3[64], new Vector3[64]);
        var (expected, got) = (new CharacterMoveResult[64], new CharacterMoveResult[64]);
        int walls = 0,
            grounded = 0;
        for (var step = 0; step < 40; step++)
        {
            _world.Change(random, step);
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
            var untouched = (owner.Kept, _world.Colliders.LastMark, _world.Objects.Gathered);
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
            Assert.Equal(untouched, (owner.Kept, _world.Colliders.LastMark, _world.Objects.Gathered));
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
    /// 64 bodies standing among crates, blocks, and craters made and taken away between steps: each step probes of their
    /// own and four workers' probes, on other threads at once, ask round each body for walls and their ledges' tops, room,
    /// the ground's top, the shared ray, and whether the body stands unchanged since its stamp. The answers agree bit for
    /// bit, and the workers' leave the shared marks alone.
    /// </summary>
    [Fact]
    public void Workers_probes_and_rest_checks_answer_as_their_own_do_bit_for_bit()
    {
        var reference = _world.Mover();
        var referenceProbes = new MovementProbes(_world.Sources(), reference);
        var owner = _world.Mover();
        var ownerProbes = new MovementProbes(_world.Sources(), owner);
        var probes = new MovementProbes[Workers];
        for (var w = 0; w < Workers; w++)
        {
            probes[w] = ownerProbes.CreateWorker(owner.CreateWorker());
        }

        var random = new Random(43);
        for (var i = 0; i < 60; i++)
        {
            _world.Change(random, step: 1, metres: 6);
        }

        var feet = new Vector3[64];
        var (expectedStamps, gotStamps) = (new GroundStamp[feet.Length], new GroundStamp[feet.Length]);
        for (var i = 0; i < feet.Length; i++)
        {
            feet[i] = new Vector3((i % 8 - 3.5f) * 1.4f, 0, (i / 8 - 3.5f) * 1.4f);
            (expectedStamps[i], gotStamps[i]) = (reference.Stamp(), owner.Stamp());
        }

        var (expected, got) = (new Probed[feet.Length], new Probed[feet.Length]);
        var seen = new Probed();
        for (var step = 0; step < 30; step++)
        {
            _world.Change(random, step, metres: 6);
            var turn = random.NextSingle() * MathF.Tau;
            for (var i = 0; i < feet.Length; i++)
            {
                expected[i] = Probe(referenceProbes, feet[i], turn + i, ref expectedStamps[i]);
                seen.Add(in expected[i]);
            }

            owner.BeginPass();
            for (var i = 0; i < feet.Length; i++)
            {
                owner.Prepare(feet[i], in s_shape, Vector3.Zero, Step);
            }

            var untouched = (owner.Kept, _world.Colliders.LastMark, _world.Objects.Gathered);
            Parallel.For(
                0,
                Workers,
                w =>
                {
                    for (var i = w; i < feet.Length; i += Workers)
                    {
                        got[i] = Probe(probes[w], feet[i], turn + i, ref gotStamps[i]);
                    }
                }
            );
            Assert.Equal(untouched, (owner.Kept, _world.Colliders.LastMark, _world.Objects.Gathered));
            owner.EndPass();
            for (var i = 0; i < feet.Length; i++)
            {
                if (got[i] != expected[i] || gotStamps[i] != expectedStamps[i])
                {
                    Assert.Fail(
                        $"Step {step}, body {i}: a worker's probes gave {got[i]}, probes of their own {expected[i]}."
                    );
                }
            }
        }

        // Walls and their tops, the ray's meshes and objects, and rest checks that held and that did not.
        Assert.True(
            seen.Wall > 100 && seen.Ledge > 20 && seen.Room > 100 && seen.Ground > 100 && seen.Stands > 100,
            $"{seen}"
        );
        Assert.True(seen.Mesh > 20 && seen.Object > 20 && seen.Moved > 20, $"{seen}");
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
        var worker = _world.Mover().CreateWorker();

        Assert.Throws<InvalidOperationException>(worker.BeginPass);
        Assert.Throws<InvalidOperationException>(() => worker.CreateWorker());
        Assert.Throws<InvalidOperationException>(() => worker.Prepare(Vector3.Zero, in s_shape, Vector3.Zero, Step));
    }

#if DEBUG
    [Fact]
    public void In_debug_a_mover_refuses_a_second_move_while_one_runs()
    {
        var mover = _world.Mover();
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
    /// Every probe once round a body at <paramref name="feet"/>, looking along <paramref name="turn"/> radians, and the
    /// rest check against its <paramref name="stamp"/>.
    /// </summary>
    private static Probed Probe(MovementProbes probes, Vector3 feet, float turn, ref GroundStamp stamp)
    {
        var way = new Vector2(MathF.Cos(turn), MathF.Sin(turn));
        var probed = new Probed
        {
            Wall = probes.TryWallAhead(feet, in s_shape, way, 1.2f, out var wall) ? 1 : 0,
            WallProbe = wall,
            Room = probes.HasRoom(feet + new Vector3(0, 0.05f, 0), in s_shape) ? 1 : 0,
            Ground = probes.Mover.GroundTop(feet + new Vector3(way.X, 0, way.Y), 0.1f, 0.8f, 0.8f, out var top) ? 1 : 0,
            GroundTop = top,
            Stands = probes.Mover.StandsUnchanged(ref stamp, feet, in s_shape) ? 1 : 0,
        };
        if (probed.Wall == 1)
        {
            probed.Ledge = probes.TryLedgeTop(feet, in s_shape, in wall, 0.3f, 0.15f, out var ledge) ? 1 : 0;
            probed.LedgeTop = ledge;
        }

        // Down past the ground ahead from a little over the head, through crates and blocks on the way.
        var down = new Vector3(way.X * 2, -1, way.Y * 2);
        if (probes.Sources.Raycast(feet + new Vector3(0, 2, 0), down, 6, out var hit))
        {
            probed.Hit = hit;
            probed.Mesh = hit.Source == SurfaceSource.Mesh ? 1 : 0;
            probed.Object = hit.Source == SurfaceSource.VoxelObject ? 1 : 0;
        }

        probed.Moved = probed.Stands == 1 ? 0 : 1;
        return probed;
    }

    /// <summary>What <see cref="Probe"/> found round a body, or the counts of what it found over many.</summary>
    private record struct Probed(
        int Wall,
        WallProbe WallProbe,
        int Ledge,
        Vector3 LedgeTop,
        int Room,
        int Ground,
        float GroundTop,
        int Stands,
        int Moved,
        SurfaceHit Hit,
        int Mesh,
        int Object
    )
    {
        public void Add(in Probed other)
        {
            Wall += other.Wall;
            Ledge += other.Ledge;
            Room += other.Room;
            Ground += other.Ground;
            Stands += other.Stands;
            Moved += other.Moved;
            Mesh += other.Mesh;
            Object += other.Object;
        }
    }

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
}
