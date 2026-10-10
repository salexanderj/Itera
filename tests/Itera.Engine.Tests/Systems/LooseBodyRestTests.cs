using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Modding;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="LooseBodySystem"/> without voxel terrain (<see cref="MeshGroundHarness"/>): a rested ball stays when no
/// game decides, and when a game keeps it, asked once until it moves again; a game may remove it or take it over (its
/// model stays); a cut body is never asked and goes after its rest time; a body that fell out is removed; and a kept
/// ball's steps allocate nothing.
/// </summary>
public sealed class LooseBodyRestTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private static readonly QueryDescription s_bodies = new QueryDescription().WithAll<LooseBody>();

    private Entity _thrown = Entity.Null;

    // Bodies count as rested after half a second at rest here.
    private readonly MeshGroundHarness _harness = new(settleSeconds: 0.5);

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Without_a_game_to_decide_a_rested_ball_stays()
    {
        using var world = World.Create();
        using var system = Create(world, rest: null);
        var ball = Throw(world, new Vector3(0, 0.5f, 0));

        Run(world, system, 300);

        Assert.True(_harness.Bodies.Contains(ball));
        Assert.Equal(1, world.CountEntities(in s_bodies));
        Assert.True(Single(world).Kept);
    }

    [Fact]
    public void A_kept_ball_is_asked_once_until_it_moves_again()
    {
        using var world = World.Create();
        var rest = new Choosing(LooseBodyRestChoice.Keep);
        using var system = Create(world, rest);
        var ball = Throw(world, new Vector3(0, 0.5f, 0));

        Run(world, system, 300);
        var askedAtRest = rest.Asked;
        // Up, and back down near where it lay.
        var reference = _harness.Physics.Simulation.Bodies[ball];
        reference.Velocity.Linear = new Vector3(0.2f, 3, 0);
        reference.Awake = true;
        Run(world, system, 300);

        Assert.Equal(1, askedAtRest);
        Assert.Equal(2, rest.Asked);
        Assert.Equal(MeshGroundHarness.BallRadius, rest.Last.BallRadius);
        Assert.True(_harness.Bodies.Contains(ball));
    }

    [Fact]
    public void A_game_may_remove_a_rested_ball()
    {
        using var world = World.Create();
        using var system = Create(world, new Choosing(LooseBodyRestChoice.Remove));
        var ball = Throw(world, new Vector3(0, 0.5f, 0));

        Run(world, system, 300);

        Assert.False(_harness.Bodies.Contains(ball));
        Assert.Equal(0, world.CountEntities(in s_bodies));
        Assert.Equal(0, _harness.Models.Count);
    }

    [Fact]
    public void A_game_may_take_a_rested_ball_over_and_keep_its_model()
    {
        using var world = World.Create();
        var rest = new Choosing(LooseBodyRestChoice.TakeOver);
        using var system = Create(world, rest);
        var ball = Throw(world, new Vector3(0, 0.5f, 0));
        var model = _harness.Bodies.ModelOf(ball);

        Run(world, system, 300);

        Assert.False(_harness.Bodies.Contains(ball));
        Assert.False(_harness.Physics.Simulation.Bodies.BodyExists(ball));
        Assert.Equal(0, world.CountEntities(in s_bodies));
        Assert.Equal((1, model), (_harness.Models.Count, rest.Last.Model));
        Assert.InRange(rest.Last.PivotMetres.Y, 0.1f, 0.15f);
    }

    [Fact]
    public void A_cut_body_is_never_asked_and_goes_after_its_rest_time()
    {
        using var world = World.Create();
        var rest = new Choosing(LooseBodyRestChoice.Keep);
        using var system = Create(world, rest);
        var body = _harness.CutBody(new Vector3(0, 0.5f, 0));
        world.Create(new LooseBody { Body = body }, new CutPiece());

        Run(world, system, (int)(LooseBodySystem.CutRestSeconds * 60) + 300);

        Assert.Equal(0, rest.Asked);
        Assert.Equal(0, world.CountEntities(in s_bodies));
        Assert.False(_harness.Bodies.Contains(body));
    }

    [Fact]
    public void A_ball_that_fell_out_is_removed()
    {
        using var world = World.Create();
        using var system = Create(world, new Choosing(LooseBodyRestChoice.Keep));
        Throw(world, new Vector3(50, 0, 0));

        Run(world, system, 300);

        Assert.Equal(0, world.CountEntities(in s_bodies));
        Assert.Equal(0, _harness.Bodies.Count);
    }

    [Fact]
    public void Steps_with_a_kept_ball_allocate_nothing()
    {
        using var world = World.Create();
        using var system = Create(world, new Choosing(LooseBodyRestChoice.Keep));
        Throw(world, new Vector3(0, 0.5f, 0));
        Run(world, system, 300);

        Assert.True(Single(world).Kept);
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step, warmup: 5, measured: 20));
    }

    private LooseBodySystem Create(World world, ILooseBodyRest? rest)
    {
        var system = new LooseBodySystem(world, _harness.Bodies, _harness.Options, rest: rest);
        system.Initialize();
        return system;
    }

    private BepuPhysics.BodyHandle Throw(World world, Vector3 pivot)
    {
        var ball = _harness.ThrowBall(pivot);
        _thrown = world.Create(new LooseBody { Body = ball, BallRadius = MeshGroundHarness.BallRadius });
        return ball;
    }

    private LooseBody Single(World world) => world.Get<LooseBody>(_thrown);

    private void Run(World world, LooseBodySystem system, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            system.Update(in Step);
            _harness.RunPhysics();
        }
    }

    /// <summary>A game's rest handler that makes one choice and counts the asks.</summary>
    private sealed class Choosing(LooseBodyRestChoice choice) : ILooseBodyRest
    {
        public int Asked { get; private set; }

        public RestedBody Last { get; private set; }

        public LooseBodyRestChoice Rested(in RestedBody body)
        {
            Asked++;
            Last = body;
            return choice;
        }
    }
}
