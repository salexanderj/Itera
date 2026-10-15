using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuUtilities.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Physics;

// The physics warm-up: Bepu's code for the engine's bodies, joints, and statics compiled before play.
public sealed partial class PhysicsWorld
{
    /// <summary>The steps the warm-up runs: 4 s of a 60 Hz match, time for its bodies to land, sleep, and wake.</summary>
    internal const int WarmUpSteps = 240;

    // The warm-up's steps at which its bodies are woken, its ragdoll frozen, and thawed.
    private const int WakeStep = 150;
    private const int FreezeStep = 180;
    private const int ThawStep = 183;

    private Task? _warmUp;

    /// <summary>True once the physics warm-up has ended, or when none was started.</summary>
    public bool WarmUpDone => _warmUp is null || _warmUp.IsCompleted;

    /// <summary>
    /// Starts the physics warm-up on a thread of its own: a world of its own with this one's settings and Bepu callbacks,
    /// in which what the engine's bodies are made of (a voxel cell's static of boxes, a game's mesh static, two people's
    /// ragdolls of capsules and a sphere with their ball sockets, swing limits, and twist limits, convex hulls of three
    /// sizes and a client's mirror of one, a cut part's compound of hulls, a stand-in, and a trigger zone) lands, rests,
    /// sleeps, wakes, freezes, thaws, and is taken away, then freed. Bepu makes its solver's code for each kind of joint
    /// and its collision code for each pair of shapes and kind of contact at run time, so no ahead-of-time compilation
    /// (ReadyToRun) covers them: without the warm-up the first ragdolls of a run compiled about 400 of Bepu's methods
    /// while the main thread waited, a step of 44 ms in a composite ReadyToRun build (66 ms in a Debug build), and the next
    /// deaths 42 more; with it, a step of 4.9 ms at most (2026-10-15). Once, after <see cref="Initialize"/>; not at all
    /// once an earlier match of this run warmed the code up (<see cref="Matches.WarmUps.Physics"/>).
    /// </summary>
    public void StartWarmUp()
    {
        if (_warmUps is { Physics: true } || _warmUp is not null || _disposed)
        {
            return;
        }

        _warmUp = Task.Factory.StartNew(
            WarmUp,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default
        );
    }

    private void WarmUp()
    {
        try
        {
            var start = Stopwatch.GetTimestamp();
            var compiled = JitInfo.GetCompiledMethodCount();
            int steps;
            using (var scratch = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, _options))
            {
                scratch.Initialize();
                steps = scratch.Exercise(this);
            }

            var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var methods = JitInfo.GetCompiledMethodCount() - compiled;
            LogWarmedUp(steps, milliseconds, methods);
            // Done for the run: the code is compiled for the process, whatever world runs it next.
            if (!_disposed && _warmUps is not null)
            {
                _warmUps.Physics = true;
            }
        }
        catch (Exception ex)
        {
            // A bug: logged, and the task ends, so the loading screen and Dispose's wait go on.
            LogWarmUpBroke(ex);
        }
    }

    /// <summary>
    /// Builds what the engine's bodies are made of in this world (a scratch world's), steps it
    /// <see cref="WarmUpSteps"/> times or until <paramref name="owner"/> is disposed, and takes it all away; the steps run.
    /// </summary>
    private int Exercise(PhysicsWorld owner)
    {
        var simulation = Simulation;
        var shapes = simulation.Shapes;
        var bodies = simulation.Bodies;

        // A voxel cell's static (VoxelColliders): a big compound of 1 m boxes, 8 by 8, and a lower one over a corner, its
        // top at y = 0.
        const int Side = 8;
        var cube = shapes.Add(new Box(1, 1, 1));
        var slab = shapes.Add(new Box(1, 0.3f, 1));
        Pool.Take(Side * Side + 1, out Buffer<CompoundChild> children);
        for (var i = 0; i < Side * Side; i++)
        {
            children[i] = new CompoundChild(new RigidPose(new Vector3(i % Side + 0.5f, 0.5f, i / Side + 0.5f)), cube);
        }

        children[Side * Side] = new CompoundChild(new RigidPose(new Vector3(0.5f, 1.15f, 0.5f)), slab);
        var groundShape = shapes.Add(new BigCompound(children, shapes, Pool));
        var ground = simulation.Statics.Add(
            new StaticDescription(new Vector3(-Side / 2f, -1, -Side / 2f), Quaternion.Identity, groundShape)
        );
        _kinds.SetStatic(ground, 0, layer: VoxelLayer);

        // A game's mesh static (MeshColliders), reporting its impacts: a ramp beside the ground, both ways round.
        Pool.Take<Triangle>(4, out var triangles);
        var (a, b, c, d) = (new Vector3(4, 0, -4), new Vector3(8, 2, -4), new Vector3(8, 2, 4), new Vector3(4, 0, 4));
        triangles[0] = new Triangle(a, b, c);
        triangles[1] = new Triangle(a, c, d);
        triangles[2] = new Triangle(a, c, b);
        triangles[3] = new Triangle(a, d, c);
        var rampShape = shapes.Add(new Mesh(triangles, Vector3.One, Pool));
        var ramp = AddStatic(
            new StaticDescription(Vector3.Zero, Quaternion.Identity, rampShape),
            new PhysicsBodySettings { Marks = PhysicsMarks.ContactEvents }
        );

        // Ragdolls (LooseBodies.TryCreateRagdoll) of a person's 10 capsules and a sphere, each a compound of one, joined at
        // 10 joints: one tumbling onto the voxels' static and one onto the mesh's ramp, so they meet the ground with the
        // many kinds of contact a fallen body makes.
        var ragdoll = AddFigure(new Vector3(-2, 0.6f, -2), new Vector3(1, 0, 0.5f), new Vector3(2, 1, 0), 1);
        var sliding = AddFigure(new Vector3(6, 2.4f, 0), Vector3.Zero, new Vector3(0, 0, 3), 2);
        var kept = new BodyInertia[ragdoll.Length];

        // Balls and lifted pieces (LooseBodies.Pieces): convex hulls of three sizes in a pile, one at half gravity, and a
        // client's mirror of one, kinematic; a part cut off a model (LooseBodies.Parts): a compound of two hulls.
        Span<Vector3> corners = stackalloc Vector3[8];
        var pile = new BodyHandle[6];
        var hull = default(ConvexHull);
        var hullShape = default(TypedIndex);
        for (var size = 0; size < 3; size++)
        {
            var edge = 0.15f + 0.15f * size;
            for (var i = 0; i < corners.Length; i++)
            {
                corners[i] = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f) * edge;
            }

            hull = new ConvexHull(corners, Pool, out _);
            hullShape = shapes.Add(hull);
            for (var copy = 0; copy < 2; copy++)
            {
                var at = new Vector3(2 + 0.1f * size, 0.3f + 0.5f * (2 * size + copy), 2 + 0.05f * copy);
                var push = new Vector3(-0.5f * copy, 0, 0);
                pile[2 * size + copy] = AddHullBody(hull, hullShape, at, push, kinematic: false);
            }
        }

        var piece = pile[0];
        SetGravityScale(pile[1], 0.5f);
        var mirror = AddHullBody(hull, hullShape, new Vector3(2, 0.3f, -3), new Vector3(0, 0, 1), kinematic: true);
        var cut = AddCutPart(hull, new Vector3(-1, 1, 2));

        // A stand-in walking into the first piece, and a trigger zone round the ground's middle.
        var standIn = AddStandIn(new Vector3(3.5f, 0, 2), 0.4f, 1.8f, 80);
        var trigger = AddTrigger(TriggerShape.Box(new Vector3(4, 2, 4)), new Vector3(0, 1, 0), Quaternion.Identity);

        var steps = 0;
        for (; steps < WarmUpSteps && !owner._disposed; steps++)
        {
            if (steps < 60)
            {
                var feet = new Vector3(3.5f - steps * 0.02f, 0, 2);
                PlaceStandIn(standIn, feet + new Vector3(0, 0.9f, 0), new Vector3(-1.2f, 0, 0));
            }
            else if (steps == 60)
            {
                // Out of the way, so the rest can fall asleep.
                RemoveStandIn(standIn);
                RemoveBody(mirror);
            }
            else if (steps == WakeStep)
            {
                var thrown = bodies[ragdoll[Chest]];
                thrown.Velocity.Linear = new Vector3(0, 3, 0);
                thrown.Awake = true;
                var pushed = bodies[piece];
                pushed.Awake = true;
            }
            else if (steps == FreezeStep)
            {
                // Frozen as VoxelColliders freezes a ragdoll where its voxels unload: kinematic, its joints dropped.
                for (var i = 0; i < ragdoll.Length; i++)
                {
                    var body = bodies[ragdoll[i]];
                    kept[i] = body.LocalInertia;
                    body.SetLocalInertia(default);
                    body.Velocity = default;
                }
            }
            else if (steps == ThawStep)
            {
                for (var i = 0; i < ragdoll.Length; i++)
                {
                    var body = bodies[ragdoll[i]];
                    body.SetLocalInertia(kept[i]);
                    body.Awake = true;
                }

                // Put back as LooseBodies does at a thaw (VoxelColliders.GroupThawed), where Bepu dropped them.
                if (bodies[ragdoll[Head]].Constraints.Count == 0)
                {
                    JoinFigure(ragdoll);
                }
            }

            Step(1 / 60f);
        }

        foreach (var body in ragdoll)
        {
            RemoveBody(body);
        }

        foreach (var body in sliding)
        {
            RemoveBody(body);
        }

        foreach (var body in pile)
        {
            RemoveBody(body);
        }

        RemoveBody(cut);
        RemoveTrigger(trigger);
        RemoveStatic(ramp);
        RemoveStatic(ground);
        Step(1 / 60f);
        return steps;
    }

    // The figure's bodies, by index in AddFigure's list.
    private const int Pelvis = 0;
    private const int Chest = 1;
    private const int Head = 2;

    /// <summary>
    /// A person's ragdoll standing with its feet at <paramref name="feet"/>, facing +z, moving at
    /// <paramref name="velocity"/> and spinning at <paramref name="spin"/> (radians a second): pelvis, chest, head, and
    /// each upper arm, forearm, thigh, and shin, joined at the waist, neck, shoulders, elbows, hips, and knees.
    /// </summary>
    private BodyHandle[] AddFigure(Vector3 feet, Vector3 velocity, Vector3 spin, int ragdoll)
    {
        var figure = new BodyHandle[11];
        figure[Pelvis] = AddSegment(
            feet + new Vector3(0, 0.95f, 0),
            feet + new Vector3(0, 1.1f, 0),
            0.13f,
            12,
            ragdoll
        );
        figure[Chest] = AddSegment(
            feet + new Vector3(0, 1.15f, 0),
            feet + new Vector3(0, 1.45f, 0),
            0.15f,
            20,
            ragdoll
        );
        figure[Head] = AddSegment(feet + new Vector3(0, 1.65f, 0), feet + new Vector3(0, 1.65f, 0), 0.11f, 5, ragdoll);
        for (var side = 0; side < 2; side++)
        {
            var x = side == 0 ? 1 : -1;
            figure[3 + side] = AddSegment(
                feet + new Vector3(0.2f * x, 1.45f, 0),
                feet + new Vector3(0.45f * x, 1.45f, 0),
                0.05f,
                2.5f,
                ragdoll
            );
            figure[5 + side] = AddSegment(
                feet + new Vector3(0.48f * x, 1.45f, 0),
                feet + new Vector3(0.72f * x, 1.45f, 0),
                0.045f,
                1.5f,
                ragdoll
            );
            figure[7 + side] = AddSegment(
                feet + new Vector3(0.1f * x, 0.9f, 0),
                feet + new Vector3(0.1f * x, 0.5f, 0),
                0.07f,
                8,
                ragdoll
            );
            figure[9 + side] = AddSegment(
                feet + new Vector3(0.1f * x, 0.47f, 0),
                feet + new Vector3(0.1f * x, 0.08f, 0),
                0.055f,
                4,
                ragdoll
            );
        }

        JoinFigure(figure);
        var bodies = Simulation.Bodies;
        foreach (var handle in figure)
        {
            var body = bodies[handle];
            body.Velocity.Linear = velocity;
            body.Velocity.Angular = spin;
        }

        return figure;
    }

    /// <summary>Joins a figure's bodies (<see cref="AddFigure"/>) halfway between their middles.</summary>
    private void JoinFigure(BodyHandle[] figure)
    {
        var bodies = Simulation.Bodies;
        var pelvis = bodies[figure[Pelvis]].Pose.Position;
        var chest = bodies[figure[Chest]].Pose.Position;
        JoinRagdollParts(figure[Pelvis], figure[Chest], (pelvis + chest) / 2);
        JoinRagdollParts(figure[Chest], figure[Head], (chest + bodies[figure[Head]].Pose.Position) / 2);
        for (var side = 0; side < 2; side++)
        {
            var upper = bodies[figure[3 + side]].Pose.Position;
            var thigh = bodies[figure[7 + side]].Pose.Position;
            JoinRagdollParts(figure[Chest], figure[3 + side], (chest + upper) / 2);
            JoinRagdollParts(figure[3 + side], figure[5 + side], (upper + bodies[figure[5 + side]].Pose.Position) / 2);
            JoinRagdollParts(figure[Pelvis], figure[7 + side], (pelvis + thigh) / 2);
            JoinRagdollParts(figure[7 + side], figure[9 + side], (thigh + bodies[figure[9 + side]].Pose.Position) / 2);
        }
    }

    /// <summary>
    /// A ragdoll's body, as LooseBodies makes them: a compound of one capsule from <paramref name="from"/> to
    /// <paramref name="to"/>, or of a sphere where they meet.
    /// </summary>
    private BodyHandle AddSegment(Vector3 from, Vector3 to, float radius, float mass, int ragdoll)
    {
        var simulation = Simulation;
        var along = to - from;
        var length = along.Length();
        var middle = (from + to) / 2;
        using var builder = new CompoundBuilder(Pool, simulation.Shapes, 1);
        if (length > 1e-4f)
        {
            builder.Add(
                new Capsule(radius, length),
                new RigidPose(middle, LooseBodies.ArcFromUp(along / length)),
                mass
            );
        }
        else
        {
            builder.Add(new Sphere(radius), new RigidPose(middle), mass);
        }

        builder.BuildDynamicCompound(out var children, out var inertia, out var centre);
        var body = simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(centre),
                default,
                inertia,
                new CollidableDescription(simulation.Shapes.Add(new Compound(children)), ContinuousDetection.Passive),
                new BodyActivityDescription(0.01f)
            )
        );
        _kinds.Set(body, BodyKind.Cut, 0, ragdoll);
        return body;
    }

    /// <summary>A ragdoll's joint at <paramref name="point"/>, as LooseBodies joins them: a ball socket, a swing limit, and a twist limit.</summary>
    private void JoinRagdollParts(BodyHandle a, BodyHandle b, Vector3 point)
    {
        var simulation = Simulation;
        var springs = new SpringSettings(30, 1);
        simulation.Solver.Add(
            a,
            b,
            new BallSocket
            {
                LocalOffsetA = point - simulation.Bodies[a].Pose.Position,
                LocalOffsetB = point - simulation.Bodies[b].Pose.Position,
                SpringSettings = springs,
            }
        );
        simulation.Solver.Add(
            a,
            b,
            new SwingLimit
            {
                AxisLocalA = Vector3.UnitZ,
                AxisLocalB = Vector3.UnitZ,
                MinimumDot = 0.5f,
                SpringSettings = springs,
            }
        );
        simulation.Solver.Add(
            a,
            b,
            new TwistLimit
            {
                LocalBasisA = Quaternion.Identity,
                LocalBasisB = Quaternion.Identity,
                MinimumAngle = -0.5f,
                MaximumAngle = 0.5f,
                SpringSettings = springs,
            }
        );
    }

    /// <summary>A ball's or lifted piece's body of <paramref name="hull"/>, as LooseBodies makes them (a mirror kinematic).</summary>
    private BodyHandle AddHullBody(in ConvexHull hull, TypedIndex shape, Vector3 at, Vector3 velocity, bool kinematic)
    {
        var collidable = new CollidableDescription(shape, ContinuousDetection.Continuous(1e-3f, 1e-3f));
        var body = Simulation.Bodies.Add(
            kinematic
                ? BodyDescription.CreateKinematic(
                    new RigidPose(at),
                    new BodyVelocity(velocity),
                    collidable,
                    new BodyActivityDescription(-1)
                )
                : BodyDescription.CreateDynamic(
                    new RigidPose(at),
                    new BodyVelocity(velocity),
                    hull.ComputeInertia(10),
                    collidable,
                    new BodyActivityDescription(0.01f)
                )
        );
        _kinds.Set(body, BodyKind.Loose);
        return body;
    }

    /// <summary>A part cut off a model, as LooseBodies makes them: a compound of two of <paramref name="hull"/>.</summary>
    private BodyHandle AddCutPart(in ConvexHull hull, Vector3 at)
    {
        var simulation = Simulation;
        using var builder = new CompoundBuilder(Pool, simulation.Shapes, 2);
        builder.Add(hull, new RigidPose(at), 5);
        builder.Add(hull, new RigidPose(at + new Vector3(0.3f, 0, 0)), 5);
        builder.BuildDynamicCompound(out var children, out var inertia, out var centre);
        var body = simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(centre),
                default,
                inertia,
                new CollidableDescription(simulation.Shapes.Add(new Compound(children)), ContinuousDetection.Passive),
                new BodyActivityDescription(0.01f)
            )
        );
        _kinds.Set(body, BodyKind.Cut, 0, ragdoll: 0);
        return body;
    }

    /// <summary>Stops the warm-up at its next step and waits for it, at most <paramref name="timeout"/>; false when it did not stop.</summary>
    private bool StopWarmUp(TimeSpan timeout) => _warmUp is null || _warmUp.Wait(timeout);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Physics: warm-up ran {Steps} step(s) in {Milliseconds:F0} ms; {Methods} method(s) compiled in the process meanwhile"
    )]
    private partial void LogWarmedUp(int steps, double milliseconds, long methods);

    [LoggerMessage(Level = LogLevel.Error, Message = "Physics: the warm-up failed")]
    private partial void LogWarmUpBroke(Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Physics: the warm-up did not stop within {Seconds} s; it was left to end on its own"
    )]
    private partial void LogWarmUpStuck(double seconds);
}
