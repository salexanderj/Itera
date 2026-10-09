using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// <see cref="LooseBodies"/>: pieces lifted from the world fly, rest, and go back into the grid; bodies that fall out of
/// the world; the body limit; thin walls; and stand-ins touching loose bodies but not the voxels.
/// </summary>
public sealed class LooseBodiesTests : IDisposable
{
    private const int Floor = 200;
    private const float Step = 1f / 60;

    private readonly VoxelWorld _world;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;
    private readonly VoxelModels _models;
    private readonly LooseBodies _bodies;
    private readonly ITestOutputHelper _output;

    public LooseBodiesTests(ITestOutputHelper output)
    {
        _output = output;
        // A stone wall 1 voxel thick at x = 100, 2 m high.
        var generator = new BoxesGenerator(Floor, new VoxelBox(100, 200, -100, 100, 399, 100, BoxesGenerator.Stone));
        _world = TestWorlds.Load(generator, new Vector3(0, 2, 0), radiusMetres: 4);
        _physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        _physics.Initialize();
        _colliders = new VoxelColliders(
            _physics,
            _world,
            new MoverWorldFixture.LeavesPass(),
            NullLogger<VoxelColliders>.Instance
        );
        _colliders.Initialize();
        _models = new VoxelModels(_world);
        _bodies = CreateBodies(maxBodies: 8);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    /// <summary>
    /// A piece lifted from the world is drawn as a model of centimetres (a lift reads it a centimetre at a time), and a
    /// giant's coarse cells are the world's voxels of a giant's part size (a check that fails when the world's voxels
    /// change size, until <see cref="VoxelColliders.CoarseLevel"/> follows).
    /// </summary>
    [Fact]
    public void A_pieces_model_has_centimetres_and_the_coarse_level_a_giants()
    {
        var piece = LiftBall(new VoxelCoord(-100, 188, 0));
        Assert.True(_bodies.TryCreate(piece, new Vector3(0, 2.5f, 0), Vector3.Zero, out var body));

        var model = _bodies.ModelOf(body);
        Assert.Equal(VoxelModels.Centimetres, _models.UnitCentimetres(model));
        Assert.Equal(0.01f, _models.VoxelMetres(model, 0));
        Assert.Equal(VoxelColliders.CoarseLevel, VoxelColliders.CoarseLevelOf(_world.Grid));
        Assert.Equal(VoxelColliders.CoarsePartCentimetres, _world.Grid.Edge(VoxelColliders.CoarseLevel));
        Assert.Equal(0, VoxelColliders.CoarseLevelOf(new VoxelGrid(10)));
    }

    [Fact]
    public void A_thrown_piece_lands_rests_and_after_3_s_is_voxels_where_it_lay()
    {
        // A ball 25 cm across of the floor's dirt, from under (-1, 1.88, 0) m, thrown from (0, 2.5, 0) m.
        var piece = LiftBall(new VoxelCoord(-100, 188, 0));
        Assert.True(_bodies.TryCreate(piece, new Vector3(0, 2.5f, 0), new Vector3(1, 0, 0), out var body));
        var mass = _bodies.MassOf(body);

        var steps = 0;
        var resting = 0;
        while (resting < 180)
        {
            Assert.True(++steps < 1200, $"The ball did not rest; it is at {_bodies.PivotOf(body)}.");
            Run(1);
            resting = _bodies.IsAtRest(body) ? resting + 1 : 0;
        }

        var pivot = _bodies.PivotOf(body);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        _bodies.Settle(body);
        _output.WriteLine(
            System.FormattableString.Invariant(
                $"Rested after {steps} steps at {pivot}; settling took {timer.Elapsed.TotalMilliseconds:F2} ms."
            )
        );

        _world.TryGetMaterial(VoxelCoord.FromMetres(pivot), out var there);
        _world.TryGetMaterial(new VoxelCoord(-100, 188, 0), out var hole);
        Assert.Equal(0, _bodies.Count);
        Assert.False(_physics.Simulation.Bodies.BodyExists(body));
        Assert.InRange(mass, 12, 16);
        Assert.True(pivot.X > 0.2f, $"The ball rests at {pivot}.");
        Assert.InRange(pivot.Y, 2.08f, 2.16f);
        Assert.Equal(BoxesGenerator.Dirt, there);
        Assert.Equal(VoxelConstants.Air, hole);
    }

    [Fact]
    public void A_ball_three_times_as_big_is_hulled_from_its_support_points_and_rests()
    {
        // A ball 73 cm across of the floor's dirt: 33,512 box corners, past MaxHullPoints.
        var piece = Lift(_world, VoxelEdit.Sphere(new VoxelCoord(-150, 150, 0), 36, VoxelEditOperation.Lift));
        Assert.True(_bodies.TryCreate(piece, new Vector3(0, 2.8f, 0), new Vector3(1, 0, 0), out var body));

        var volume = 4 / 3f * MathF.PI * 0.365f * 0.365f * 0.365f;
        Assert.InRange(_bodies.MassOf(body), 0.95f * volume * 2000, 1.05f * volume * 2000);
        var steps = 0;
        var resting = 0;
        while (resting < 60)
        {
            Assert.True(++steps < 1200, $"The ball did not rest; it is at {_bodies.PivotOf(body)}.");
            Run(1);
            resting = _bodies.IsAtRest(body) ? resting + 1 : 0;
        }

        // It lies on the floor (2 m up), its pivot (the voxel at its middle) its radius above it, or up to 2 cm less:
        // the hull of its support points lies up to about 1.7 cm inside its voxels at a facet's middle.
        Assert.InRange(_bodies.PivotOf(body).Y, 2.36f - 0.025f, 2.36f + 0.01f);
    }

    [Fact]
    public void A_large_part_is_hulled_from_its_support_points_and_rests_on_them()
    {
        // A part (as a character's) that is a ball of stone 61 cm across: its full hull has more corners than
        // PartHullDirections.
        const int Radius = 30;
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z <= 2 * Radius; z++)
        {
            for (var y = 0; y <= 2 * Radius; y++)
            {
                for (var x = 0; x <= 2 * Radius; x++)
                {
                    int dx = x - Radius,
                        dy = y - Radius,
                        dz = z - Radius;
                    if (dx * dx + dy * dy + dz * dz <= Radius * Radius)
                    {
                        voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] =
                            BoxesGenerator.Stone;
                    }
                }
            }
        }

        var size = new VoxelCoord(2 * Radius + 1, 2 * Radius + 1, 2 * Radius + 1);
        var part = new LoosePart(
            -1,
            _models.Encode(voxels),
            size,
            new Vector3(-1.5f, 2.5f, -0.3f),
            Quaternion.Identity,
            false
        );
        Assert.True(_bodies.TryCreate([part], 2000, Vector3.Zero, 0, out var body));

        var compound = _physics.Simulation.Shapes.GetShape<Compound>(
            _physics.Simulation.Bodies[body].Collidable.Shape.Index
        );
        var hull = _physics.Simulation.Shapes.GetShape<ConvexHull>(compound.Children[0].ShapeIndex.Index);
        var corners = new HashSet<Vector3>();
        for (var i = 0; i < hull.Points.Length * Vector<float>.Count; i++)
        {
            hull.GetPoint(i, out var point);
            corners.Add(point);
        }

        Assert.InRange(corners.Count, 100, LooseBodies.PartHullDirections);
        var steps = 0;
        var resting = 0;
        while (resting < 60)
        {
            Assert.True(++steps < 1200, $"The part did not rest; it is at {Position(body)}.");
            Run(1);
            resting = _bodies.IsAtRest(body) ? resting + 1 : 0;
        }

        // It lies on the floor (2 m up), its middle its radius above it (30.5 cm), or up to 1 cm less: its hull lies
        // within about 1 cm of its voxels.
        Assert.InRange(Position(body).Y, 2.305f - 0.01f, 2.305f + 0.005f);
    }

    [Fact]
    public void Support_points_are_the_farthest_points_in_each_direction()
    {
        var random = new Random(3);
        var points = new Vector3[5000];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 2 - Vector3.One;
        }

        var all = (Vector3[])points.Clone();
        var kept = _bodies.SupportPoints(points);

        Assert.InRange(kept, 4, LooseBodies.HullDirections);
        Assert.All(points[..kept], point => Assert.Contains(point, all));
        Assert.Equal(kept, points[..kept].Distinct().Count());
        // Along the axes the kept points reach as far as all of them.
        foreach (var axis in (Vector3[])[Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ])
        {
            Assert.Equal(all.Max(p => Vector3.Dot(p, axis)), points[..kept].Max(p => Vector3.Dot(p, axis)));
        }
    }

    [Fact]
    public void Before_a_save_every_thrown_ball_goes_back_into_the_grid_where_it_flies()
    {
        var first = LiftBall(new VoxelCoord(-100, 188, 0));
        var second = LiftBall(new VoxelCoord(60, 188, 40));
        Assert.True(_bodies.TryCreate(first, new Vector3(0, 2.8f, 0), new Vector3(1, 2, 0), out var flying));
        Assert.True(_bodies.TryCreate(second, new Vector3(0.6f, 2.5f, 0.4f), default, out _));
        Run(5);
        var pivot = _bodies.PivotOf(flying);
        var edits = _world.EditCount;

        var settled = _bodies.SettleThrown();

        // Each went back as a piece edit where it was, in the air, so a save holds both balls.
        Assert.Equal(2, settled);
        Assert.Equal(0, _bodies.Count);
        Assert.Equal(edits + 2, _world.EditCount);
        _world.TryGetMaterial(VoxelCoord.FromMetres(pivot), out var there);
        Assert.Equal(BoxesGenerator.Dirt, there);
    }

    [Fact]
    public void A_piece_is_put_back_where_it_was_lifted_and_a_piece_no_lift_made_is_refused()
    {
        var centre = new VoxelCoord(-100, 188, 0);
        var piece = LiftBall(centre);
        var edits = _world.EditCount;

        // A piece no lift made once put back an empty piece at the origin.
        Assert.Throws<ArgumentException>(() => _bodies.PutBack(piece + 1000));
        Assert.Equal(edits, _world.EditCount);

        _bodies.PutBack(piece);
        Assert.Equal(edits + 1, _world.EditCount);
        _world.TryGetMaterial(centre, out var there);
        Assert.Equal(BoxesGenerator.Dirt, there);
    }

    /// <summary>
    /// A mirror of another game's body is made past this game's limit, which that game's own limit keeps for it
    /// (a client's own limit dropped the host's bodies).
    /// </summary>
    [Fact]
    public void A_mirror_is_made_past_the_body_limit()
    {
        var bodies = CreateBodies(maxBodies: 1);
        Assert.True(bodies.TryCreate(LiftBall(new VoxelCoord(-100, 188, 0)), new Vector3(0, 2.5f, 0), default, out _));
        var piece = LiftBall(new VoxelCoord(60, 188, 40));

        Assert.False(bodies.TryCreate(piece, new Vector3(1, 2.5f, 0), default, out _));
        Assert.True(bodies.TryCreateMirror(piece, new Vector3(1, 2.5f, 0), Quaternion.Identity, out _));
        Assert.Equal(2, bodies.Count);
    }

    [Fact]
    public void Balls_lifted_one_after_another_from_one_spot_and_thrown_into_it_land_there()
    {
        // Right click pressed quickly at one spot (the crash of 2026-09-25): each lift rebuilds the cells that the balls
        // before it are flying into or lying on.
        var start = new Vector3(-2, 2.5f, 0);
        VoxelCoord[] lifts = [new(0, 188, 0), new(13, 180, 2), new(2, 188, 14), new(-12, 190, -3), new(6, 176, -11)];
        var balls = new List<BodyHandle>();
        foreach (var lift in lifts)
        {
            var piece = LiftBall(lift);
            var target = new Vector3(lift.X, lift.Y, lift.Z) / VoxelConstants.CentimetresPerMetre;
            Assert.True(_bodies.TryCreate(piece, start, Vector3.Normalize(target - start) * 10, out var body));
            balls.Add(body);
            Run(10);
        }

        Run(120);

        foreach (var ball in balls)
        {
            var pivot = _bodies.PivotOf(ball);
            // The holes reach down to 1.64 m and the floor's top is at 2 m.
            Assert.True(pivot.Y is > 1.6f and < 2.3f, $"A ball is at {pivot}.");
        }
    }

    /// <summary>
    /// <see cref="LooseBodies.AnyPieceNear"/>, which gives characters their stand-ins, sees a lifted piece's body and not a
    /// cut one, and forgets a piece's body once it is removed or settled (it walked
    /// every cut body for every character each step).
    /// </summary>
    [Fact]
    public void Only_a_pieces_body_is_near_for_the_stand_ins_until_it_goes()
    {
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < 10; z++)
        {
            for (var y = 0; y < 10; y++)
            {
                for (var x = 0; x < 10; x++)
                {
                    voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = BoxesGenerator.Stone;
                }
            }
        }

        var part = new LoosePart(
            -1,
            _models.Encode(voxels),
            new VoxelCoord(10, 10, 10),
            new Vector3(-1.5f, 2.5f, 0),
            Quaternion.Identity,
            false
        );
        Assert.True(_bodies.TryCreate([part], 2000, Vector3.Zero, 0, out var cut));
        Assert.False(_bodies.AnyPieceNear(Middle(cut), 0.1f));

        Assert.True(
            _bodies.TryCreate(LiftBall(new VoxelCoord(-100, 188, 0)), new Vector3(0, 2.5f, 0), default, out var a)
        );
        Assert.True(
            _bodies.TryCreate(LiftBall(new VoxelCoord(60, 188, 40)), new Vector3(0.6f, 2.5f, 0.6f), default, out var b)
        );
        var (nearA, nearB) = (Middle(a), Middle(b));
        Assert.True(_bodies.AnyPieceNear(nearA, 0.1f));
        Assert.True(_bodies.AnyPieceNear(nearB, 0.1f));
        Assert.False(_bodies.AnyPieceNear(Middle(cut), 0.1f));

        _bodies.Remove(a);
        Assert.False(_bodies.AnyPieceNear(nearA, 0.1f));
        Assert.True(_bodies.AnyPieceNear(nearB, 0.1f));
        _bodies.Settle(b);
        Assert.False(_bodies.AnyPieceNear(nearB, 0.1f));
    }

    [Fact]
    public void A_body_is_drawn_by_a_model_that_leaves_with_it_or_is_retired_when_it_settles()
    {
        var first = LiftBall(new VoxelCoord(-100, 188, 0));
        var second = LiftBall(new VoxelCoord(-60, 188, 40));
        Assert.True(_bodies.TryCreate(first, new Vector3(0, 2.5f, 0), Vector3.Zero, out var removed));
        Assert.True(_bodies.TryCreate(second, new Vector3(0.5f, 2.5f, 0), Vector3.Zero, out var settled));
        var removedModel = _bodies.ModelOf(removed);
        var settledModel = _bodies.ModelOf(settled);
        var size = _models.Size(removedModel);

        _bodies.Remove(removed);
        var afterRemove = _models.Count;
        _bodies.Settle(settled, tint: 0.3f);

        _world.TryGetPiece(first, out var piece);
        Assert.Equal(piece.Size, size);
        Assert.NotEqual(removedModel, settledModel);
        Assert.Equal(1, afterRemove);
        var retired = Assert.Single(_models.Retired.ToArray());
        Assert.Equal(settledModel, retired.Handle);
        Assert.Equal(0.3f, retired.Tint);
        _models.RemoveRetired();
        Assert.Equal(0, _models.Count);
    }

    [Fact]
    public void A_fast_piece_does_not_pass_through_a_wall_1_voxel_thick()
    {
        var piece = LiftBall(new VoxelCoord(-100, 188, 0));
        // 30 m/s moves 50 cm a step; the wall at x = 1 m is 1 cm thick.
        Assert.True(_bodies.TryCreate(piece, new Vector3(0.3f, 2.5f, 0), new Vector3(30, 0, 0), out var body));

        Run(20);

        Assert.True(_bodies.PivotOf(body).X < 1, $"The piece went through the wall to {_bodies.PivotOf(body)}.");
    }

    [Fact]
    public void A_blast_throws_bodies_within_its_reach_away_from_it_and_leaves_farther_ones()
    {
        var near = LiftBall(new VoxelCoord(-100, 188, 0));
        var far = LiftBall(new VoxelCoord(-150, 188, 60));
        Assert.True(_bodies.TryCreate(near, new Vector3(-0.5f, 2.2f, 0), Vector3.Zero, out var nearBody));
        Assert.True(_bodies.TryCreate(far, new Vector3(-0.5f, 2.2f, 3), Vector3.Zero, out var farBody));
        var centre = new Vector3(0, 2.1f, 0);

        // Allocates nothing.
        var before = SystemAllocation.StartMeasurement();
        var pushed = _bodies.Push(centre, radiusMetres: 2, metresPerSecond: 12);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(1, pushed);
        Assert.Equal(0, allocated);
        var thrown = _physics.Simulation.Bodies[nearBody].Velocity.Linear;
        Assert.True(thrown.X < -2, $"The near ball moves at {thrown}.");
        Assert.True(thrown.Y > 0, $"The near ball moves at {thrown}.");
        Assert.Equal(Vector3.Zero, _physics.Simulation.Bodies[farBody].Velocity.Linear);
        Assert.Equal(0, _bodies.Push(centre, 0, 12));
    }

    /// <summary>
    /// A blast wakes a sleeping ball and throws it, and leaves a host's mirror (kinematic) alone (the test
    /// above had one awake dynamic body).
    /// </summary>
    [Fact]
    public void A_blast_wakes_a_sleeping_ball_and_leaves_a_mirror_alone()
    {
        var resting = LiftBall(new VoxelCoord(-100, 188, 0));
        var mirrored = LiftBall(new VoxelCoord(-150, 188, 60));
        Assert.True(_bodies.TryCreate(resting, new Vector3(-0.5f, 2.2f, 0), Vector3.Zero, out var sleeping));
        Assert.True(_bodies.TryCreateMirror(mirrored, new Vector3(0.5f, 2.2f, 0), Quaternion.Identity, out var mirror));
        Run(2);
        var reference = _physics.Simulation.Bodies[sleeping];
        reference.Awake = false;
        Assert.False(_physics.Simulation.Bodies[sleeping].Awake);

        var pushed = _bodies.Push(new Vector3(0, 2.1f, 0), radiusMetres: 2, metresPerSecond: 12);

        Assert.Equal(1, pushed);
        Assert.True(_physics.Simulation.Bodies[sleeping].Awake);
        var thrown = _physics.Simulation.Bodies[sleeping].Velocity.Linear;
        Assert.True(thrown.X < -2, $"The woken ball moves at {thrown}.");
        Assert.Equal(Vector3.Zero, _physics.Simulation.Bodies[mirror].Velocity.Linear);
    }

    [Fact]
    public void A_body_below_the_world_has_fallen_out()
    {
        var piece = LiftBall(new VoxelCoord(-100, 188, 0));
        // Past the map's edge nothing holds it up.
        var outside = VoxelGrid.Unit.EndVoxelXZ / 100f + 5;
        Assert.True(_bodies.TryCreate(piece, new Vector3(outside, 2.5f, 0), Vector3.Zero, out var body));

        Run(60);
        var early = _bodies.HasFallenOut(body);
        Run(240);

        Assert.False(early);
        Assert.True(_bodies.HasFallenOut(body));
        _bodies.Remove(body);
        Assert.Equal(0, _bodies.Count);
    }

    [Fact]
    public void A_body_frozen_until_its_columns_load_is_not_at_rest()
    {
        var piece = LiftBall(new VoxelCoord(-100, 188, 0));
        // 20 m away, far outside the 4 m that is loaded.
        Assert.True(_bodies.TryCreate(piece, new Vector3(20, 2.5f, 0), Vector3.Zero, out var body));

        Run(10);

        Assert.True(_colliders.IsFrozen(body));
        Assert.False(_bodies.IsAtRest(body));
    }

    [Fact]
    public void No_more_bodies_than_the_limit_are_made()
    {
        var bodies = CreateBodies(maxBodies: 2);
        var made = 0;
        for (var i = 0; i < 3; i++)
        {
            var piece = LiftBall(new VoxelCoord(-150 + 50 * i, 188, 60));
            made += bodies.TryCreate(piece, new Vector3(i, 2.5f, -0.5f), Vector3.Zero, out _) ? 1 : 0;
        }

        Assert.Equal(2, made);
        Assert.Equal(2, bodies.Count);
    }

    [Fact]
    public void A_stand_in_passes_through_the_voxels()
    {
        // The ball makes statics of the floor around it; the stand-in stands on them, beside the ball.
        var piece = LiftBall(new VoxelCoord(-100, 188, 0));
        Assert.True(_bodies.TryCreate(piece, new Vector3(0.5f, 2.2f, 0), Vector3.Zero, out _));
        Run(30);
        var standIn = AddStandIn(new Vector3(0.64f, 2.86f, 0.5f));

        Run(30);

        Assert.True(_colliders.StaticCount > 0);
        Assert.True(Position(standIn).Y < 2.86f - 0.5f, $"The stand-in stood at {Position(standIn)}.");
    }

    [Fact]
    public void A_stand_in_pushes_loose_bodies()
    {
        var piece = LiftBall(new VoxelCoord(-100, 188, 0));
        Assert.True(_bodies.TryCreate(piece, new Vector3(0.2f, 2.2f, 0), Vector3.Zero, out var ball));
        Run(60);
        var before = _bodies.PivotOf(ball);
        // Sunk 30 cm into the floor, which it passes through, walking at the ball at 2 m/s.
        var standIn = AddStandIn(new Vector3(-0.6f, 2.55f, 0));

        for (var i = 0; i < 25; i++)
        {
            _physics.Simulation.Bodies[standIn].Velocity.Linear = new Vector3(2, 0, 0);
            Run(1);
        }

        Assert.True(
            _bodies.PivotOf(ball).X > before.X + 0.2f,
            $"The ball stayed at {_bodies.PivotOf(ball)} from {before}."
        );
    }

    /// <summary>An upright cylinder of 80 kg, 1.7 m tall, that never tips or sleeps, as a character's stand-in.</summary>
    private BodyHandle AddStandIn(Vector3 centre)
    {
        var cylinder = new Cylinder(0.26f, 1.7f);
        var simulation = _physics.Simulation;
        var inertia = cylinder.ComputeInertia(80);
        inertia.InverseInertiaTensor = default;
        var standIn = simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(centre),
                inertia,
                new CollidableDescription(simulation.Shapes.Add(cylinder), 0.1f),
                new BodyActivityDescription(-1)
            )
        );
        _physics.Kinds.Set(standIn, BodyKind.StandIn);
        return standIn;
    }

    private Vector3 Position(BodyHandle body) => _physics.Simulation.Bodies[body].Pose.Position;

    /// <summary>The middle of <paramref name="body"/>'s bounds, in metres.</summary>
    private Vector3 Middle(BodyHandle body)
    {
        _bodies.BoundsOf(body, out var min, out var max);
        return (min + max) / 2;
    }

    private long LiftBall(VoxelCoord centre) => Lift(_world, VoxelEdit.Sphere(centre, 12, VoxelEditOperation.Lift));

    private LooseBodies CreateBodies(int maxBodies) =>
        new(
            _physics,
            _world,
            _colliders,
            _models,
            new MoverWorldFixture.LeavesPass(),
            Options.Create(new EngineOptions { Physics = new PhysicsSettings { MaxLooseBodies = maxBodies } }),
            NullLogger<LooseBodies>.Instance
        );

    private void Run(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            _colliders.Update(Step);
            _physics.Step(Step);
        }
    }

    private static long Lift(VoxelWorld world, in VoxelEdit edit)
    {
        Assert.True(world.TryLift(in edit, out var sequence), "The lift's columns are not resident.");
        return sequence;
    }
}
