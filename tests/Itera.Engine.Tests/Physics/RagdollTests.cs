using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// Ragdolls of loose bodies (<see cref="LooseBodies.TryCreateRagdoll(ReadOnlySpan{LoosePart}, ReadOnlySpan{int}, ReadOnlySpan{RagdollJoint}, float, Vector3, float, List{BodyHandle})"/>):
/// a chain of overlapping parts dropped on the floor stays joined and within its swing limits and rests as one; a part
/// spun about its joint's axis stops within the twist limit; its bodies, as every body cut off a character, touch only
/// the voxels, falling through each other, other bodies, and stand-ins; its joints go with its bodies; and a ragdoll that
/// would pass the body limit is not made. A ragdoll of shapes (capsules, a figure drawn from a mesh) dropped on the floor
/// stays joined and rests as one, with no models; one started turning round its foot moves as a whole turning would and
/// falls that way; bad shapes and joints make nothing; removed, its bodies and joints go.
/// </summary>
public sealed class RagdollTests : IDisposable
{
    private const int Floor = 200;
    private const float Step = 1f / 60;

    /// <summary>A limb part: 10 by 40 by 10 cm of stone.</summary>
    private static readonly VoxelCoord s_size = new(10, 40, 10);

    private readonly VoxelWorld _world;
    private readonly PhysicsWorld _physics;
    private readonly VoxelColliders _colliders;
    private readonly VoxelModels _models;
    private readonly LooseBodies _bodies;
    private readonly ModelTree _tree;

    public RagdollTests()
    {
        _world = TestWorlds.Load(new BoxesGenerator(Floor), new Vector3(0, 2, 0), radiusMetres: 4);
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
        _bodies = new LooseBodies(
            _physics,
            _world,
            _colliders,
            _models,
            new MoverWorldFixture.LeavesPass(),
            Options.Create(new EngineOptions { Physics = new PhysicsSettings { MaxLooseBodies = 8 } }),
            NullLogger<LooseBodies>.Instance
        );
        var voxels = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < s_size.Z; z++)
        {
            for (var y = 0; y < s_size.Y; y++)
            {
                for (var x = 0; x < s_size.X; x++)
                {
                    voxels[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = BoxesGenerator.Stone;
                }
            }
        }

        _tree = _models.Encode(voxels);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_chain_dropped_on_the_floor_stays_joined_within_its_limits_and_rests_as_one()
    {
        var made = new List<BodyHandle>();
        // Three parts hanging one under the next from 3.4 m, each overlapping the next by 2 cm, tipped a little so the
        // chain folds as it lands.
        var tip = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.3f);
        var corners = new[] { new Vector3(0, 3.0f, 0), new Vector3(0, 2.62f, 0), new Vector3(0, 2.24f, 0) };
        var joints = new[]
        {
            new RagdollJoint(
                0,
                1,
                Rotate(new Vector3(0.05f, 3.01f, 0.05f), tip),
                Vector3.Transform(-Vector3.UnitY, tip),
                0.8f
            ),
            new RagdollJoint(
                1,
                2,
                Rotate(new Vector3(0.05f, 2.63f, 0.05f), tip),
                Vector3.Transform(-Vector3.UnitY, tip),
                0.8f
            ),
        };

        Assert.True(
            _bodies.TryCreateRagdoll(
                [
                    Part(Rotate(corners[0], tip), tip),
                    Part(Rotate(corners[1], tip), tip),
                    Part(Rotate(corners[2], tip), tip),
                ],
                [0, 1, 2],
                joints,
                1000,
                Vector3.Zero,
                0.4f,
                made
            )
        );
        Assert.Equal(3, made.Count);
        // A ball socket, a swing limit, and a twist limit for each joint.
        Assert.Equal(6, _physics.Simulation.Solver.CountConstraints());

        // Where each joint lies in the bodies it joins.
        var offsets = new (Vector3 A, Vector3 B)[joints.Length];
        for (var j = 0; j < joints.Length; j++)
        {
            offsets[j] = (Local(made[joints[j].BodyA], joints[j].Point), Local(made[joints[j].BodyB], joints[j].Point));
        }

        var restsTogether = true;
        // Made still, it counts as resting at once: it falls a second first.
        Run(60);
        var steps = 60;
        while (steps < 8 * 60 && !_bodies.IsAtRest(made[0]))
        {
            Run(1);
            steps++;
            restsTogether &= _bodies.IsAtRest(made[0]) == _bodies.IsAtRest(made[1]);
            restsTogether &= _bodies.IsAtRest(made[1]) == _bodies.IsAtRest(made[2]);
        }

        Assert.True(_bodies.IsAtRest(made[0]), "The ragdoll did not come to rest.");
        Assert.True(restsTogether);
        for (var j = 0; j < joints.Length; j++)
        {
            // Still joined: both bodies put the joint within 2 cm of each other.
            var a = World(made[joints[j].BodyA], offsets[j].A);
            var b = World(made[joints[j].BodyB], offsets[j].B);
            Assert.True(Vector3.Distance(a, b) < 0.02f, $"Joint {j} came apart by {Vector3.Distance(a, b)} m.");
            // Within the limit: the parts' long axes at most 0.8 radians apart (and a little for the joints' spring).
            var axis = joints[j].Axis;
            var dot = Vector3.Dot(Axis(made[joints[j].BodyA], axis), Axis(made[joints[j].BodyB], axis));
            Assert.True(dot > MathF.Cos(0.8f) - 0.05f, $"Joint {j} swung to {MathF.Acos(dot)} radians.");
        }

        // All on the floor, none sunk into it.
        foreach (var body in made)
        {
            _bodies.BoundsOf(body, out var min, out _);
            Assert.True(min.Y > 1.97f && min.Y < 2.1f, $"A part lay at {min.Y} m.");
        }
    }

    [Fact]
    public void A_part_spun_about_its_joints_axis_stops_within_the_twist_limit()
    {
        // Two parts lying along X, joined end to end, falling from 3.5 m; the second spun fast about its own length, as a
        // large body's forearm turned without a limit. In the air nothing but the joint stops it.
        var along = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        var made = new List<BodyHandle>();
        Assert.True(
            _bodies.TryCreateRagdoll(
                [Part(new Vector3(0, 3.5f, 0), along), Part(new Vector3(0.38f, 3.5f, 0), along)],
                [0, 1],
                [new RagdollJoint(0, 1, new Vector3(0.39f, 3.45f, 0.05f), Vector3.UnitX, 0.8f)],
                1000,
                Vector3.Zero,
                0,
                made
            )
        );
        _physics.Simulation.Bodies[made[1]].Velocity.Angular = new Vector3(20, 0, 0);

        var most = 0f;
        // Half a second: it falls about 1.2 m, still above the floor, and would twist 10 radians unlimited.
        for (var i = 0; i < 30; i++)
        {
            Run(1);
            most = Math.Max(most, MathF.Abs(Twist(made[0], made[1], Vector3.UnitX)));
        }

        _bodies.BoundsOf(made[1], out var min, out _);
        Assert.True(min.Y > 2.05f, $"The parts reached the floor ({min.Y} m).");

        // Within the limit, and a little for the joint's spring.
        Assert.True(most < LooseBodies.RagdollTwistRadians + 0.15f, $"The part twisted {most} radians.");
    }

    [Fact]
    public void Its_bodies_touch_only_the_voxels()
    {
        var made = new List<BodyHandle>();
        Assert.True(
            _bodies.TryCreateRagdoll(
                [
                    Part(new Vector3(0, 2.5f, 0), Quaternion.Identity),
                    Part(new Vector3(0, 2.12f, 0), Quaternion.Identity),
                ],
                [0, 1],
                [new RagdollJoint(0, 1, new Vector3(0.05f, 2.51f, 0.05f), -Vector3.UnitY, 0.8f)],
                1000,
                Vector3.Zero,
                0,
                made
            )
        );
        Assert.True(
            _bodies.TryCreate(
                [Part(new Vector3(1, 2.1f, 0), Quaternion.Identity)],
                1000,
                Vector3.Zero,
                0,
                out var other
            )
        );
        Assert.True(_bodies.TryCreateMirror([Part(new Vector3(-1, 2.1f, 0), Quaternion.Identity)], 0, out var mirror));
        var piece = AddPieceBody(new Vector3(2, 2.3f, 0));
        var standIn = _physics.AddStandIn(new Vector3(-2, 2, 0), 0.26f, 1.7f, 80);
        // Statics of the floor's voxels are built near the bodies.
        Run(10);
        Assert.NotEqual(0, _physics.Simulation.Statics.Count);
        var ground = new CollidableReference(_physics.Simulation.Statics.IndexToHandle[0]);
        var callbacks = new NarrowPhaseCallbacks(_physics.Kinds);
        bool Touch(CollidableReference a, CollidableReference b)
        {
            var margin = 0f;
            return callbacks.AllowContactGeneration(0, a, b, ref margin)
                && callbacks.AllowContactGeneration(0, b, a, ref margin);
        }

        bool Apart(CollidableReference a, CollidableReference b)
        {
            var margin = 0f;
            return !callbacks.AllowContactGeneration(0, a, b, ref margin)
                && !callbacks.AllowContactGeneration(0, b, a, ref margin);
        }

        Assert.True(Touch(Dynamic(made[0]), ground));
        Assert.True(Touch(Dynamic(other), ground));
        Assert.True(Apart(Dynamic(made[0]), Dynamic(made[1])));
        Assert.True(Apart(Dynamic(made[0]), Dynamic(other)));
        Assert.True(Apart(Dynamic(made[1]), Dynamic(piece)));
        Assert.True(Apart(Dynamic(other), Dynamic(standIn)));
        // A client's mirror of a cut body (kinematic) does not push the client's stand-in either.
        Assert.True(Apart(new CollidableReference(CollidableMobility.Kinematic, mirror), Dynamic(standIn)));
        // A lifted piece still touches the voxels and stand-ins; a stand-in still does not touch the voxels.
        Assert.True(Touch(Dynamic(piece), ground));
        Assert.True(Touch(Dynamic(piece), Dynamic(standIn)));
        Assert.True(Apart(Dynamic(standIn), ground));
    }

    [Fact]
    public void A_body_falls_through_another_and_through_a_stand_in_onto_the_floor()
    {
        // One part standing on the floor (2 m), a stand-in round it, and a second part lying along X dropped across the
        // first's top (2.4 m): it would rest there if the two touched.
        Assert.True(
            _bodies.TryCreate(
                [Part(new Vector3(0, 2.01f, 0), Quaternion.Identity)],
                1000,
                Vector3.Zero,
                0,
                out var standing
            )
        );
        Run(30);
        var before = _physics.Simulation.Bodies[standing].Pose.Position;
        var along = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        Assert.True(
            _bodies.TryCreate([Part(new Vector3(-0.15f, 2.7f, 0), along)], 1000, Vector3.Zero, 0, out var dropped)
        );
        var standIn = _physics.AddStandIn(new Vector3(0.05f, 2, 0.05f), 0.26f, 1.7f, 80);
        var centre = new Vector3(0.05f, 2.85f, 0.05f);

        var push = 0f;
        for (var i = 0; i < 90; i++)
        {
            _physics.PlaceStandIn(standIn, centre, Vector3.Zero);
            Run(1);
            push = MathF.Max(push, _physics.PushOn(standIn, Vector3.Zero, Step).Length());
        }

        _bodies.BoundsOf(dropped, out var min, out _);
        var moved = Vector3.Distance(before, _physics.Simulation.Bodies[standing].Pose.Position);
        Assert.True(min.Y < 2.1f, $"The dropped part stopped at {min.Y} m.");
        Assert.True(min.Y > 1.97f, $"The dropped part sank to {min.Y} m.");
        Assert.True(moved < 0.005f, $"The standing part moved {moved} m.");
        Assert.True(push < 0.01f, $"The stand-in was pushed at {push} m/s.");
    }

    [Fact]
    public void Its_joints_go_with_its_bodies()
    {
        var made = new List<BodyHandle>();
        _bodies.TryCreateRagdoll(
            [Part(new Vector3(0, 3, 0), Quaternion.Identity), Part(new Vector3(0, 2.62f, 0), Quaternion.Identity)],
            [0, 1],
            [new RagdollJoint(0, 1, new Vector3(0.05f, 3.01f, 0.05f), -Vector3.UnitY, 0.8f)],
            1000,
            Vector3.Zero,
            0,
            made
        );
        Run(10);

        _bodies.Remove(made[0]);
        var afterFirst = _physics.Simulation.Solver.CountConstraints();
        _bodies.Remove(made[1]);

        Assert.Equal(0, afterFirst);
        Assert.Equal(0, _bodies.Count);
        Assert.Equal(0, _physics.Simulation.Solver.CountConstraints());
    }

    /// <summary>
    /// A ragdoll where no level of the voxels is resident freezes as one, and once they load its bodies thaw together
    /// with their joints back (Bepu dropped the joints as its bodies froze one by one,
    /// and it thawed as loose limbs).
    /// </summary>
    [Fact]
    public void A_ragdoll_where_nothing_is_resident_freezes_and_thaws_as_one_with_its_joints()
    {
        var made = new List<BodyHandle>();
        var at = new Vector3(20, 0, 0);
        var joints = new[]
        {
            new RagdollJoint(0, 1, at + new Vector3(0.05f, 3.01f, 0.05f), -Vector3.UnitY, 0.8f),
            new RagdollJoint(1, 2, at + new Vector3(0.05f, 2.63f, 0.05f), -Vector3.UnitY, 0.8f),
        };
        Assert.True(
            _bodies.TryCreateRagdoll(
                [
                    Part(at + new Vector3(0, 3.0f, 0), Quaternion.Identity),
                    Part(at + new Vector3(0, 2.62f, 0), Quaternion.Identity),
                    Part(at + new Vector3(0, 2.24f, 0), Quaternion.Identity),
                ],
                [0, 1, 2],
                joints,
                1000,
                Vector3.Zero,
                0.4f,
                made
            )
        );
        Assert.Equal(6, _physics.Simulation.Solver.CountConstraints());
        var offsets = new (Vector3 A, Vector3 B)[joints.Length];
        for (var j = 0; j < joints.Length; j++)
        {
            offsets[j] = (Local(made[joints[j].BodyA], joints[j].Point), Local(made[joints[j].BodyB], joints[j].Point));
        }

        Run(5);
        Assert.All(made, body => Assert.True(_colliders.IsFrozen(body)));

        // The voxels load there: one update thaws every body, with its joints.
        _world.SetCenters([at + new Vector3(0, 2, 0)]);
        TestWorlds.WaitUntilLoaded(_world);
        _colliders.Update(Step);
        Assert.All(made, body => Assert.False(_colliders.IsFrozen(body)));
        Assert.Equal(6, _physics.Simulation.Solver.CountConstraints());

        // It falls onto the floor still joined: both bodies put each joint within 2 cm of each other.
        Run(120);
        for (var j = 0; j < joints.Length; j++)
        {
            var a = World(made[joints[j].BodyA], offsets[j].A);
            var b = World(made[joints[j].BodyB], offsets[j].B);
            Assert.True(Vector3.Distance(a, b) < 0.02f, $"Joint {j} came apart by {Vector3.Distance(a, b)} m.");
        }

        Assert.True(_physics.Simulation.Bodies[made[2]].Pose.Position.Y < 2.4f, "The ragdoll did not fall.");
    }

    [Fact]
    public void A_ragdoll_that_would_pass_the_body_limit_is_not_made()
    {
        var made = new List<BodyHandle>();
        var parts = new LoosePart[9];
        var bodyOf = new int[9];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = Part(new Vector3(0.2f * i, 3, 0), Quaternion.Identity);
            bodyOf[i] = i;
        }

        Assert.False(_bodies.TryCreateRagdoll(parts, bodyOf, [], 1000, Vector3.Zero, 0, made));
        Assert.Empty(made);
        Assert.Equal(0, _bodies.Count);
    }

    [Fact]
    public void Bad_body_and_joint_indices_make_nothing()
    {
        var made = new List<BodyHandle>();
        LoosePart[] parts =
        [
            Part(new Vector3(0, 3, 0), Quaternion.Identity),
            Part(new Vector3(0, 2.6f, 0), Quaternion.Identity),
        ];
        var joint = new RagdollJoint(0, 1, new Vector3(0.05f, 3.01f, 0.05f), -Vector3.UnitY, 0.8f);

        // A negative body index, a joint to a body past the last, a joint from a body to itself, and a body count that
        // does not match the parts.
        Assert.False(_bodies.TryCreateRagdoll(parts, [-1, 0], [joint], 1000, Vector3.Zero, 0, made));
        Assert.False(_bodies.TryCreateRagdoll(parts, [0, 1], [joint with { BodyB = 2 }], 1000, Vector3.Zero, 0, made));
        Assert.False(_bodies.TryCreateRagdoll(parts, [0, 1], [joint with { BodyB = 0 }], 1000, Vector3.Zero, 0, made));
        Assert.False(_bodies.TryCreateRagdoll(parts, [0], [joint], 1000, Vector3.Zero, 0, made));

        Assert.Empty(made);
        Assert.Equal(0, _bodies.Count);
        Assert.Equal(0, _physics.Simulation.Solver.CountConstraints());
        // The same parts and joint, well formed, still make a ragdoll: the refusals left nothing behind.
        Assert.True(_bodies.TryCreateRagdoll(parts, [0, 1], [joint], 1000, Vector3.Zero, 0, made));
        Assert.Equal(2, made.Count);
    }

    [Fact]
    public void A_ragdoll_of_shapes_dropped_on_the_floor_stays_joined_and_rests_as_one_with_no_models()
    {
        var made = new List<BodyHandle>();
        // A hip, a thigh, and a shin hanging from 3.2 m, tipped so the leg folds as it lands.
        RagdollShape[] shapes =
        [
            new(new Vector3(-0.1f, 3.2f, 0), new Vector3(0.1f, 3.2f, 0), 0.1f, 12),
            new(new Vector3(0.1f, 3.15f, 0), new Vector3(0.25f, 2.75f, 0), 0.07f, 8),
            new(new Vector3(0.25f, 2.75f, 0), new Vector3(0.3f, 2.35f, 0), 0.05f, 4),
        ];
        RagdollJoint[] joints =
        [
            new(0, 1, new Vector3(0.1f, 3.15f, 0), Vector3.Normalize(new Vector3(0.15f, -0.4f, 0)), 1.2f),
            new(1, 2, new Vector3(0.25f, 2.75f, 0), Vector3.Normalize(new Vector3(0.05f, -0.4f, 0)), 1.2f),
        ];

        Assert.True(_bodies.TryCreateRagdoll(shapes, joints, Vector3.Zero, made));
        Assert.Equal(3, made.Count);
        Assert.Equal(6, _physics.Simulation.Solver.CountConstraints());
        Assert.True(made.TrueForAll(body => _bodies.IsCut(body) && _bodies.PartsOf(body).IsEmpty));
        Assert.Equal(_bodies.RagdollOf(made[0]), _bodies.RagdollOf(made[2]));
        // Its mass is the shapes', each body centred on its shape's middle.
        Assert.Equal(8, _bodies.MassOf(made[1]), 1e-3f);
        Assert.True(Vector3.Distance(new Vector3(0.175f, 2.95f, 0), _bodies.PivotOf(made[1])) < 1e-3f);
        var offsets = new (Vector3 A, Vector3 B)[joints.Length];
        for (var j = 0; j < joints.Length; j++)
        {
            offsets[j] = (Local(made[joints[j].BodyA], joints[j].Point), Local(made[joints[j].BodyB], joints[j].Point));
        }

        // A second to fall (made still, it counts as resting at once), then until it rests.
        Run(60);
        var steps = 60;
        while (steps < 8 * 60 && !_bodies.IsAtRest(made[0]))
        {
            Run(1);
            steps++;
        }

        Assert.True(_bodies.IsAtRest(made[0]) && _bodies.IsAtRest(made[2]), "The ragdoll did not come to rest.");
        for (var j = 0; j < joints.Length; j++)
        {
            var a = World(made[joints[j].BodyA], offsets[j].A);
            var b = World(made[joints[j].BodyB], offsets[j].B);
            Assert.True(Vector3.Distance(a, b) < 0.02f, $"Joint {j} came apart by {Vector3.Distance(a, b)} m.");
        }

        foreach (var body in made)
        {
            _bodies.BoundsOf(body, out var min, out _);
            Assert.True(min.Y > 1.97f && min.Y < 2.3f, $"A shape lay at {min.Y} m.");
        }

        // Removed, its bodies and joints go.
        foreach (var body in made)
        {
            _bodies.Remove(body);
        }

        Assert.Equal(0, _bodies.Count);
        Assert.Equal(0, _physics.Simulation.Solver.CountConstraints());
    }

    [Fact]
    public void A_ragdoll_of_shapes_started_turning_round_its_foot_moves_as_a_whole_turning_would_and_falls_that_way()
    {
        var made = new List<BodyHandle>();
        // A leg and a body 1.6 m tall standing on the floor, turning towards +x round its foot.
        RagdollShape[] shapes =
        [
            new(new Vector3(0, 2.1f, 0), new Vector3(0, 2.8f, 0), 0.08f, 20),
            new(new Vector3(0, 2.85f, 0), new Vector3(0, 3.6f, 0), 0.12f, 40),
        ];
        RagdollJoint[] joints = [new(0, 1, new Vector3(0, 2.82f, 0), Vector3.UnitY, 0.3f)];
        var spin = new Vector3(0, 0, -1);
        var foot = new Vector3(0, 2, 0);

        Assert.True(_bodies.TryCreateRagdoll(shapes, joints, Vector3.Zero, spin, foot, made));
        // Each body moves as the whole turning round the foot: its middle's height times the spin, towards +x.
        foreach (var body in made)
        {
            var velocity = _physics.Simulation.Bodies[body].Velocity;
            var height = _bodies.PivotOf(body).Y - foot.Y;
            Assert.True(
                MathF.Abs(velocity.Linear.X - height) < 1e-3f && MathF.Abs(velocity.Linear.Y) < 1e-3f,
                $"A body {height} m up moved at {velocity.Linear}."
            );
            Assert.Equal(spin, velocity.Angular);
        }

        Run(3 * 60);
        var upper = _bodies.PivotOf(made[1]);
        Assert.True(upper.X > 0.6f && upper.Y < 2.5f, $"Its upper body lay at {upper}.");
        Assert.False(_bodies.TryCreateRagdoll(shapes, joints, Vector3.Zero, new Vector3(float.NaN, 0, 0), foot, made));
    }

    [Fact]
    public void Bad_shapes_and_joints_make_no_ragdoll_of_shapes()
    {
        var made = new List<BodyHandle>();
        RagdollShape good = new(new Vector3(0, 3, 0), new Vector3(0, 2.6f, 0), 0.05f, 4);
        var joint = new RagdollJoint(0, 1, new Vector3(0, 2.6f, 0), -Vector3.UnitY, 0.8f);

        Assert.False(_bodies.TryCreateRagdoll([good, good with { RadiusMetres = 0 }], [joint], Vector3.Zero, made));
        Assert.False(_bodies.TryCreateRagdoll([good, good with { MassKilograms = -1 }], [joint], Vector3.Zero, made));
        Assert.False(
            _bodies.TryCreateRagdoll(
                [good, good with { To = new Vector3(float.NaN, 0, 0) }],
                [joint],
                Vector3.Zero,
                made
            )
        );
        Assert.False(_bodies.TryCreateRagdoll([good, good], [joint with { BodyB = 2 }], Vector3.Zero, made));
        Assert.False(_bodies.TryCreateRagdoll([good, good], [joint with { BodyB = 0 }], Vector3.Zero, made));
        Assert.False(_bodies.TryCreateRagdoll(new RagdollShape[9], [], Vector3.Zero, made));

        Assert.Empty(made);
        Assert.Equal(0, _bodies.Count);
        // A sphere where the ends meet, well formed, is made.
        Assert.True(_bodies.TryCreateRagdoll([good, good with { To = good.From }], [joint], Vector3.Zero, made));
        Assert.Equal(2, made.Count);
    }

    private LoosePart Part(Vector3 corner, Quaternion turn) => new(-1, _tree, s_size, corner, turn, false);

    private static Vector3 Rotate(Vector3 point, Quaternion turn) =>
        new Vector3(0, 3.4f, 0) + Vector3.Transform(point - new Vector3(0, 3.4f, 0), turn);

    private static CollidableReference Dynamic(BodyHandle body) => new(CollidableMobility.Dynamic, body);

    /// <summary>A 20 cm box of 16 kg as a loose body of a thrown ball or lifted piece would be, to Bepu.</summary>
    private BodyHandle AddPieceBody(Vector3 position)
    {
        var box = new Box(0.2f, 0.2f, 0.2f);
        var simulation = _physics.Simulation;
        var body = simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(position),
                box.ComputeInertia(16),
                new CollidableDescription(simulation.Shapes.Add(box), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
        _physics.Kinds.Set(body, BodyKind.Loose);
        return body;
    }

    private Vector3 Local(BodyHandle body, Vector3 point)
    {
        var pose = _physics.Simulation.Bodies[body].Pose;
        return Vector3.Transform(point - pose.Position, Quaternion.Conjugate(pose.Orientation));
    }

    private Vector3 World(BodyHandle body, Vector3 local)
    {
        var pose = _physics.Simulation.Bodies[body].Pose;
        return pose.Position + Vector3.Transform(local, pose.Orientation);
    }

    /// <summary>
    /// A part's long axis in the world now: <paramref name="axis"/>, the axis as it lay when the body was made unturned,
    /// turned with its body.
    /// </summary>
    private Vector3 Axis(BodyHandle body, Vector3 axis) =>
        Vector3.Transform(axis, _physics.Simulation.Bodies[body].Pose.Orientation);

    /// <summary>
    /// How far <paramref name="b"/> has turned about <paramref name="axis"/> (in <paramref name="a"/>'s axes, as the
    /// bodies lay when made unturned) relative to <paramref name="a"/>, in radians.
    /// </summary>
    private float Twist(BodyHandle a, BodyHandle b, Vector3 axis)
    {
        var relative =
            Quaternion.Conjugate(_physics.Simulation.Bodies[a].Pose.Orientation)
            * _physics.Simulation.Bodies[b].Pose.Orientation;
        var along = Vector3.Dot(new Vector3(relative.X, relative.Y, relative.Z), axis);
        return 2 * MathF.Atan2(along, relative.W);
    }

    private void Run(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            _colliders.Update(Step);
            _physics.Step(Step);
        }
    }
}
