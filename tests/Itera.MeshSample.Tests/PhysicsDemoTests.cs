using System.Globalization;
using System.Numerics;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.MeshSample.Systems;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Input;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The yard's physics corner played without a window (<see cref="PhysicsDemo"/>, <see cref="PhysicsDemoSystem"/>): a
/// rubber ball let go on the ice ramp rolls through the gate into the zone and chimes; the gate holds the player, who
/// chimes walking into the zone; a rubber ball on the rubber pad bounces most of its height and clangs as loud as it was
/// fast; the floating balls hang and the slow ones fall at 0.3 of gravity; and the system allocates nothing.
/// </summary>
public sealed class PhysicsDemoTests
{
    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
    };

    private static readonly QueryDescription s_balls = new QueryDescription().WithAll<LooseBody>();
    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        LocalPlayer,
        CharacterPosition
    >();

    [Fact]
    public void A_ball_let_go_on_the_ice_ramp_rolls_through_the_gate_into_the_zone_and_chimes()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var demo = Demo(match);

        // The first ball goes at 5 s; 4 s more to roll down and on.
        match.Step(60 * 9);

        Assert.True(demo.Chimes >= 1, "Nothing came into the zone.");
        var gate = AtMiddle.Local(PhysicsDemo.GatePlace).X;
        Assert.Contains(Pivots(match), pivot => pivot.X < gate - 0.2f && pivot.Z > 7.5f && pivot.Z < 11.5f);
    }

    [Fact]
    public void The_gate_holds_the_player_who_chimes_walking_into_the_zone()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var demo = Demo(match);
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var eye = CharacterSize.ForHeight(1.7f).EyeMetres;

        // East of the gate, looking west along -x: 2 s of walking.
        Teleport(commands, -10.6, eye, 9.5, 90);
        match.Step(10);
        match.Input.InjectKey(Key.W, true);
        match.Step(120);
        match.Input.InjectKey(Key.W, false);
        var held = Feet(match.World);
        // North of the zone, looking south along -z: walking in.
        var chimes = demo.Chimes;
        Teleport(commands, -14.5, eye, 13.5, 0);
        match.Step(10);
        match.Input.InjectKey(Key.W, true);
        match.Step(60);
        match.Input.InjectKey(Key.W, false);
        var player = Player(match.World);

        var gate = AtMiddle.Local(PhysicsDemo.GatePlace).X;
        Assert.InRange(held.X, gate + 0.1f, gate + 0.5f);
        Assert.True(demo.Chimes > chimes, "The player came in without a chime.");
        Assert.True(match.Services.GetRequiredService<CharacterTriggers>().IsInside(player, demo.Zone));
    }

    [Fact]
    public void A_rubber_ball_on_the_rubber_pad_bounces_most_of_its_height_and_clangs_as_loud_as_it_was_fast()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var demo = Demo(match);
        var release = AtMiddle.Local(PhysicsDemo.PadRelease);
        match.Step(300);

        // The pad ball falls 2.7 m to rest on the pad (its middle 0.3 m up), lands at 7.3 m/s, and rises again.
        var loudest = 0f;
        var landed = false;
        var highest = 0f;
        for (var step = 0; step < 150; step++)
        {
            var clangs = demo.Clangs.Count;
            match.Step(1);
            if (demo.Clangs.Count > clangs)
            {
                loudest = MathF.Max(loudest, demo.Clangs.LastGain);
            }

            var ball = Nearest(match, new Vector3(release.X, 0, release.Z));
            landed |= ball.Y < 0.5f;
            if (landed)
            {
                highest = MathF.Max(highest, ball.Y);
            }
        }

        Assert.InRange(loudest, 0.8f, 1);
        Assert.True(highest > 0.3f + 0.6f * 2.7f, $"It rose to {highest} m.");
    }

    [Fact]
    public void The_floating_balls_hang_and_the_slow_balls_fall_at_three_tenths_of_gravity()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var physics = match.Services.GetRequiredService<PhysicsWorld>();
        var floating = AtMiddle.Local(PhysicsDemo.FloatingPlaces[0]);
        var slow = AtMiddle.Local(PhysicsDemo.SlowPlaces[0]);
        var slowStart = Nearest(match, slow);

        match.Step(60);

        Assert.True(Vector3.Distance(Nearest(match, floating), floating) < 0.01f);
        // About half of 0.3 x 9.81 m/s² over a second, 1.47 m, and what it fell at before the first reading (the match's
        // first steps); a full fall would be 4.9 m.
        Assert.InRange(slowStart.Y - Nearest(match, slow).Y, 1.35f, 1.75f);
        Assert.Equal(PhysicsMarks.None, Marks(match, physics, floating));
        Assert.Equal(PhysicsMarks.ContactEvents, Marks(match, physics, slow));
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var demo = Demo(match);
        match.Step(60 * 6);

        // Steps of no time let no ball go; the events the last physics step gave are heard each time.
        Assert.Equal(0, SystemAllocation.BytesAllocated(demo, new FixedStepTime(0, 1)));
    }

    private static PhysicsDemoSystem Demo(HeadlessMatch match) =>
        match.Services.GetServices<IFixedUpdateSystem>().OfType<PhysicsDemoSystem>().Single();

    private static void Teleport(ConsoleCommands commands, double x, double y, double z, double yaw) =>
        commands.Execute(string.Create(CultureInfo.InvariantCulture, $"tp {x} {y} {z} {yaw} 0"), new Output());

    private static List<Vector3> Pivots(HeadlessMatch match)
    {
        var bodies = match.Services.GetRequiredService<LooseBodies>();
        var pivots = new List<Vector3>();
        match.World.Query(in s_balls, (ref LooseBody body) => pivots.Add(bodies.PivotOf(body.Body)));
        return pivots;
    }

    /// <summary>The middle of the ball nearest <paramref name="point"/> across x and z (and in y when it is not 0).</summary>
    private static Vector3 Nearest(HeadlessMatch match, Vector3 point) =>
        Pivots(match)
            .OrderBy(pivot =>
                point.Y == 0
                    ? Vector2.Distance(new Vector2(pivot.X, pivot.Z), new Vector2(point.X, point.Z))
                    : Vector3.Distance(pivot, point)
            )
            .First();

    private static PhysicsMarks Marks(HeadlessMatch match, PhysicsWorld physics, Vector3 near)
    {
        var bodies = match.Services.GetRequiredService<LooseBodies>();
        var found = PhysicsMarks.None;
        var best = float.MaxValue;
        match.World.Query(
            in s_balls,
            (ref LooseBody body) =>
            {
                var distance = Vector3.Distance(bodies.PivotOf(body.Body), near);
                if (distance < best)
                {
                    best = distance;
                    found = physics.MarksOf(body.Body);
                }
            }
        );
        return found;
    }

    private static Vector3 Feet(World world)
    {
        var feet = Vector3.Zero;
        world.Query(in s_player, (ref CharacterPosition position) => feet = position.Feet);
        return feet;
    }

    private static Entity Player(World world)
    {
        var player = Entity.Null;
        world.Query(in s_player, (Entity entity) => player = entity);
        return player;
    }

    private sealed class Output : IConsoleOutput
    {
        public void Print(string text) { }

        public void PrintError(string text) => throw new InvalidOperationException(text);
    }
}
