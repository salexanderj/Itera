using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Physics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// <see cref="PhysicsWorld.Shift"/>, for an origin that moved: every body (awake, asleep, kinematic) and static moves the
/// other way by whole metres; a sleeping body stays asleep and a ball dropped on it at its new place lands on it and
/// wakes it; a static is met at its new place and not at its old one; a ball in flight, shifted as an origin following it
/// would, goes on as one never shifted.
/// </summary>
public sealed class PhysicsShiftTests : IDisposable
{
    private const float Step = 1f / 60;
    private static readonly Vector3 s_shift = new(300, -20, -512);

    private readonly PhysicsWorld _physics = Create();

    public void Dispose() => _physics.Dispose();

    [Fact]
    public void Every_body_and_static_moves_by_the_shift_and_a_sleeping_body_stays_asleep()
    {
        var floor = AddFloor(_physics, Vector3.Zero);
        var resting = AddBall(_physics, new Vector3(0, 1, 0), Vector3.Zero);
        var thrown = AddBall(_physics, new Vector3(5, 3, 0), new Vector3(1, 2, 0));
        var kinematic = _physics.Simulation.Bodies.Add(
            BodyDescription.CreateKinematic(
                new RigidPose(new Vector3(-4, 2, 1)),
                new CollidableDescription(_physics.Simulation.Shapes.Add(new Sphere(0.2f)), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
        StepUntilAsleep(_physics, resting);
        var before = (
            Position(resting),
            Position(thrown),
            Position(kinematic),
            _physics.Simulation.Statics[floor].Pose.Position
        );
        var velocity = _physics.Simulation.Bodies[thrown].Velocity.Linear;

        var (bodies, statics) = _physics.Shift(s_shift);

        Assert.Equal((3, 1), (bodies, statics));
        Assert.Equal(before.Item1 - s_shift, Position(resting));
        Assert.Equal(before.Item2 - s_shift, Position(thrown));
        Assert.Equal(before.Item3 - s_shift, Position(kinematic));
        Assert.Equal(before.Item4 - s_shift, _physics.Simulation.Statics[floor].Pose.Position);
        Assert.False(_physics.Simulation.Bodies[resting].Awake);
        Assert.Equal(velocity, _physics.Simulation.Bodies[thrown].Velocity.Linear);
    }

    [Fact]
    public void A_ball_dropped_on_a_shifted_sleeping_ball_lands_on_it_and_wakes_it()
    {
        AddFloor(_physics, Vector3.Zero);
        var resting = AddBall(_physics, new Vector3(0, 1, 0), Vector3.Zero);
        StepUntilAsleep(_physics, resting);
        _physics.Shift(s_shift);
        var below = Position(resting);

        var dropped = AddBall(_physics, below + new Vector3(0.05f, 1.5f, 0), Vector3.Zero);
        var woke = false;
        for (var i = 0; i < 240; i++)
        {
            _physics.Step(Step);
            woke |= _physics.Simulation.Bodies[resting].Awake;
        }

        Assert.True(woke, "The sleeping ball did not wake when the other landed on it.");
        // It lies on the ball or beside it on the floor, not through either.
        Assert.True(Position(dropped).Y > below.Y - 0.01f, $"The dropped ball fell to {Position(dropped)}.");
        Assert.True(
            Vector3.Distance(Position(dropped), Position(resting)) > 0.39f,
            "The dropped ball went into the other."
        );
    }

    [Fact]
    public void A_static_is_met_at_its_new_place_and_not_at_its_old_one()
    {
        AddFloor(_physics, new Vector3(0, 0, 0));
        _physics.Shift(s_shift);

        var atNew = AddBall(_physics, -s_shift + new Vector3(0, 2, 0), Vector3.Zero);
        var atOld = AddBall(_physics, new Vector3(0, 2, 0), Vector3.Zero);
        for (var i = 0; i < 120; i++)
        {
            _physics.Step(Step);
        }

        // The floor's top is at y = 0 (a 1 m box under it); the ball's middle rests 0.2 m above it.
        Assert.Equal(-s_shift.Y + 0.2f, Position(atNew).Y, 0.01f);
        Assert.True(Position(atOld).Y < -1, $"A ball where the floor was stopped at {Position(atOld)}.");
    }

    [Fact]
    public void A_ball_in_flight_goes_on_as_one_never_shifted()
    {
        using var unshifted = Create();
        AddFloor(_physics, Vector3.Zero);
        AddFloor(unshifted, Vector3.Zero);
        var ball = AddBall(_physics, new Vector3(-3, 4, 1), new Vector3(3, 1, -0.5f));
        var twin = AddBall(unshifted, new Vector3(-3, 4, 1), new Vector3(3, 1, -0.5f));
        var shifted = Vector3.Zero;

        for (var i = 0; i < 180; i++)
        {
            if (i % 40 == 20)
            {
                // As an origin following the ball would: by the whole metres it has come from the last origin.
                var by = Round(Position(ball));
                _physics.Shift(by);
                shifted += by;
            }

            _physics.Step(Step);
            unshifted.Step(Step);
        }

        Assert.NotEqual(Vector3.Zero, shifted);
        var apart = Vector3.Distance(Position(ball) + shifted, unshifted.Simulation.Bodies[twin].Pose.Position);
        Assert.True(apart < 1e-4f, $"The shifted ball is {apart * 1000:F4} mm from the unshifted one.");
    }

    private static Vector3 Round(Vector3 metres) =>
        new(MathF.Round(metres.X), MathF.Round(metres.Y), MathF.Round(metres.Z));

    private Vector3 Position(BodyHandle body) => _physics.Simulation.Bodies[body].Pose.Position;

    private static PhysicsWorld Create()
    {
        var physics = new PhysicsWorld(
            NullLogger<PhysicsWorld>.Instance,
            Options.Create(new EngineOptions { Physics = { Threads = 1 } })
        );
        physics.Initialize();
        return physics;
    }

    /// <summary>A static floor 40 m square and 1 m thick, its top at <paramref name="top"/>.</summary>
    private static StaticHandle AddFloor(PhysicsWorld physics, Vector3 top)
    {
        var shape = physics.Simulation.Shapes.Add(new Box(40, 1, 40));
        var floor = physics.Simulation.Statics.Add(new StaticDescription(top - new Vector3(0, 0.5f, 0), shape));
        physics.Kinds.SetGameStatic(floor);
        return floor;
    }

    private static BodyHandle AddBall(PhysicsWorld physics, Vector3 at, Vector3 velocity)
    {
        var sphere = new Sphere(0.2f);
        return physics.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(at),
                new BodyVelocity(velocity),
                sphere.ComputeInertia(1),
                new CollidableDescription(physics.Simulation.Shapes.Add(sphere), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
    }

    private static void StepUntilAsleep(PhysicsWorld physics, BodyHandle body)
    {
        for (var i = 0; i < 600 && physics.Simulation.Bodies[body].Awake; i++)
        {
            physics.Step(Step);
        }

        Assert.False(physics.Simulation.Bodies[body].Awake, "The ball never fell asleep.");
    }
}
