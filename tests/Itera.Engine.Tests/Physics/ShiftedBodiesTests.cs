using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Loose bodies on voxel terrain across a move of the origin (<see cref="OriginShifts"/>): a ball at rest stays where
/// it rests, asleep, and settles into the voxels a ball never shifted does; the world's streaming centres stay where
/// they were; and the cost of a move with 384 bodies and
/// the voxel colliders' statics round them (logged, not held to a time: timings wait for a quiet machine).
/// </summary>
public sealed class ShiftedBodiesTests(ITestOutputHelper output)
{
    private const float Step = 1f / 60;

    [Fact]
    public void A_resting_ball_stays_where_it_rests_asleep_and_settles_where_one_never_shifted_does()
    {
        using var still = new Bodies();
        using var shifted = new Bodies();
        var ball = still.RestedBall();
        var other = shifted.RestedBall();
        var before = shifted.World.Origin.ToWorld(shifted.Loose.PivotOf(other));

        Assert.True(shifted.Shifts.ShiftTo(new WorldPoint(-36, 3, 41)));
        shifted.Run(120);
        still.Run(120);

        var after = shifted.World.Origin.ToWorld(shifted.Loose.PivotOf(other));
        Assert.True(before.DistanceTo(after) < 1e-4, $"The ball moved from {before} to {after}.");
        Assert.False(shifted.Physics.Simulation.Bodies[other].Awake);
        var near = still.World.GetEdit(still.Loose.Settle(ball));
        var far = shifted.World.GetEdit(shifted.Loose.Settle(other));
        Assert.Equal(near.Origin, far.Origin);
    }

    [Fact]
    public void The_worlds_streaming_centres_move_with_it_and_stay_where_they_were()
    {
        using var bodies = new Bodies();
        var before = bodies.World.Origin.ToWorld(bodies.World.Centers[0]);
        var centimetres = bodies.World.Origin.ToCentimetres(bodies.World.Centers[0]);

        bodies.Shifts.ShiftTo(new WorldPoint(-36, 3, 41));

        Assert.Equal(new Vector3(1, 2, 3) - new Vector3(-36, 3, 41), bodies.World.Centers[0]);
        Assert.Equal(bodies.World.Centers[0], bodies.World.StreamingCentres[0].Position);
        Assert.Equal(before, bodies.World.Origin.ToWorld(bodies.World.Centers[0]));
        Assert.Equal(centimetres, bodies.World.Origin.ToCentimetres(bodies.World.Centers[0]));
    }

    [Fact]
    public void A_move_with_384_bodies_and_their_statics_is_timed()
    {
        using var bodies = new Bodies(radiusMetres: 12);
        for (var i = 0; i < 384; i++)
        {
            var at = new Vector3(-4 + i % 20 * 0.6f, 2.2f + i / 200 * 0.5f, 1 + i / 20 % 10 * 0.6f);
            Assert.True(bodies.Loose.TryCreateBall(8, DensityTerrainGenerator.Stone, at, Vector3.Zero, out _));
        }

        bodies.Run(60);
        var statics = bodies.Physics.Simulation.Statics.Count;
        var first = Time(() => bodies.Shifts.ShiftTo(new WorldPoint(512, 0, -512)));
        bodies.Run(1);
        var second = Time(() => bodies.Shifts.ShiftTo(WorldPoint.Zero));
        var physics = Time(() => bodies.Physics.Shift(new Vector3(1, 0, 0)));

        // The voxel colliders hold a compound static a cell, so the bodies sit on few statics of many boxes.
        output.WriteLine(
            FormattableString.Invariant(
                $"A move of the origin with 384 bodies and {statics} statics: {first:F3} ms the first time, {second:F3} ms the next, Bepu's part {physics:F3} ms (Debug, not quiet)."
            )
        );
        Assert.True(statics > 0);
    }

    private static double Time(Action action)
    {
        var timer = Stopwatch.StartNew();
        action();
        return timer.Elapsed.TotalMilliseconds;
    }

    /// <summary>A world of 10 cm voxels with Bepu, the voxel colliders, loose bodies, and the origin's moves.</summary>
    private sealed class Bodies : IDisposable
    {
        private readonly EcsWorld _ecs = new(NullLogger<EcsWorld>.Instance, new SystemTimes());
        private readonly VoxelColliders _colliders;
        private readonly VoxelModels _models;

        public Bodies(double radiusMetres = 6)
        {
            World = TestWorlds.Load(new DensityTerrainGenerator(new VoxelGrid(10)), new Vector3(1, 2, 3), radiusMetres);
            var options = Options.Create(new EngineOptions { Physics = { Threads = 1 } });
            Physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, options);
            Physics.Initialize();
            var passability = new NothingPassable();
            _colliders = new VoxelColliders(Physics, World, passability, NullLogger<VoxelColliders>.Instance);
            _colliders.Initialize();
            _models = new VoxelModels(World);
            Loose = new LooseBodies(
                Physics,
                World,
                _colliders,
                _models,
                passability,
                options,
                NullLogger<LooseBodies>.Instance
            );
            Shifts = new OriginShifts(
                World.Origin,
                Physics,
                _ecs,
                _models,
                new PointLights(),
                new ParticleEmitter(Options.Create(new EngineOptions()), NullLogger<ParticleEmitter>.Instance),
                [],
                NullLogger<OriginShifts>.Instance,
                World
            );
        }

        public VoxelWorld World { get; }

        public PhysicsWorld Physics { get; }

        public LooseBodies Loose { get; }

        public OriginShifts Shifts { get; }

        /// <summary>A ball dropped on the floor and stepped until Bepu puts it to sleep.</summary>
        public BepuPhysics.BodyHandle RestedBall()
        {
            Assert.True(
                Loose.TryCreateBall(
                    12,
                    DensityTerrainGenerator.Stone,
                    new Vector3(1.3f, 2.2f, 0.7f),
                    new Vector3(0.4f, 0, 0.2f),
                    out var ball
                )
            );
            for (var i = 0; i < 1200 && Physics.Simulation.Bodies[ball].Awake; i++)
            {
                Run(1);
            }

            Assert.False(Physics.Simulation.Bodies[ball].Awake, "The ball never fell asleep.");
            return ball;
        }

        public void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                _colliders.Update(Step);
                Physics.Step(Step);
            }
        }

        public void Dispose()
        {
            _colliders.Dispose();
            _ecs.Dispose();
            Physics.Dispose();
            World.Dispose();
        }
    }
}
