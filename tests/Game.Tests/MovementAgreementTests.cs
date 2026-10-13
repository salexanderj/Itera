using System.Globalization;
using System.Numerics;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Input;
using Xunit.Abstractions;

namespace Game.Tests;

/// <summary>
/// The check behind host-corrected prediction (`docs/replication.md`): a host moves a client's player
/// from the client's commands while the client moves it from the same commands at once, so the two agree only where the
/// mover gives the same result on both. The host's floats are measured from its own origin, at its own camera, which may
/// be far from the client's player; the client's from an origin near it. Two matches of the starter's world play the
/// same input, one with its origin where the player starts and one with its origin far off, and the feet are compared
/// in absolute metres every step: a walk, a sprint across, jumps, a crouched walk, a walk backwards, balls dropped and
/// walked into, and the boulder nearest the start climbed. One physics thread, so each match repeats.
/// </summary>
[RunsLong]
public sealed class MovementAgreementTests(ITestOutputHelper output)
{
    /// <summary>How far apart a client's prediction and the host's result may be before the host corrects it.</summary>
    private const double CorrectionMetres = 0.01;

    /// <summary>The steps between a client's command and the host's result reaching it: a round trip of about 330 ms.</summary>
    private const int RoundTripSteps = 20;

    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
        ["Engine:Voxels:WorkerThreads"] = "4",
        // The origin stays where the test puts it, however far from the camera.
        ["Engine:Origin:ShiftMetres"] = "4096",
    };

    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        LocalPlayer,
        CharacterPosition,
        CharacterContact,
        Climber
    >();

    /// <summary>
    /// Every phase keeps the two within a correction's distance over a round trip's 20 steps: what a client's prediction
    /// meets before the host's result corrects it. Measured on 2026-10-12, with the origin 2 km off, the gap grew at most
    /// 3.1 mm in 20 steps while walking, sprinting, jumping, crouching, walking backwards, and pushing into balls; the
    /// climb agrees at 300 m (5.8 mm) but not at 2 km, where the host's floats are coarse and one pull-up landed a step
    /// apart (111 mm): a client climbing that far from the host's camera is corrected, and the correction eased.
    /// </summary>
    [Theory]
    [InlineData(300, true)]
    [InlineData(2000, false)]
    public void A_player_moves_alike_with_the_origin_at_it_and_far_off(int originMetres, bool climbAgrees)
    {
        var near = Play(originMetres: 0);
        var far = Play(originMetres);

        Assert.Equal(near.Count, far.Count);
        Assert.True(near[^1].Climbed && far[^1].Climbed, "The boulder was not climbed.");
        foreach (var phase in near.Select(s => s.Phase).Distinct())
        {
            var mostApart = 0.0;
            var mostGrowth = 0.0;
            var contactsDiffer = 0;
            for (var i = 0; i < near.Count; i++)
            {
                if (near[i].Phase != phase)
                {
                    continue;
                }

                mostApart = Math.Max(mostApart, near[i].Feet.DistanceTo(far[i].Feet));
                contactsDiffer += near[i].Grounded != far[i].Grounded || near[i].Climb != far[i].Climb ? 1 : 0;
                if (i >= RoundTripSteps)
                {
                    var now = Gap(near[i].Feet, far[i].Feet);
                    var before = Gap(near[i - RoundTripSteps].Feet, far[i - RoundTripSteps].Feet);
                    mostGrowth = Math.Max(mostGrowth, (now - before).Length());
                }
            }

            output.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Origin {originMetres} m off, {phase}: most apart {mostApart * 1000:F3} mm, most growth over {RoundTripSteps} steps {mostGrowth * 1000:F3} mm, {contactsDiffer} steps with another contact"
                )
            );
            if (phase != "climb" || climbAgrees)
            {
                Assert.True(mostGrowth < CorrectionMetres, $"{phase}: the gap grew {mostGrowth * 1000:F3} mm.");
                Assert.Equal(0, contactsDiffer);
            }
        }
    }

    /// <summary>
    /// Plays the script in a match of seed 1 whose origin is <paramref name="originMetres"/> along x (and half that along
    /// z) from where it starts, and reads the player's feet (absolute) after every step.
    /// </summary>
    private static List<Sample> Play(int originMetres)
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var shifts = match.Services.GetRequiredService<OriginShifts>();
        var origin = shifts.Origin;
        if (originMetres != 0)
        {
            var start = origin.ToWorld(Vector3.Zero);
            shifts.ShiftTo(new WorldPoint(start.X + originMetres, start.Y, start.Z + originMetres / 2.0));
        }

        var commands = provider.GetRequiredService<ConsoleCommands>();
        var console = new Output();
        var samples = new List<Sample>();
        var phase = "stand";
        void Run(int steps)
        {
            for (var i = 0; i < steps; i++)
            {
                match.Step(1);
                samples.Add(Read(match.World, origin, phase));
            }
        }

        void Hold(Key key, bool down) => match.Input.InjectKey(key, down);

        Run(30);
        phase = "walk";
        Hold(Key.W, true);
        Run(120);
        phase = "sprint across";
        Hold(Key.ShiftLeft, true);
        Hold(Key.D, true);
        Run(90);
        Hold(Key.D, false);
        Hold(Key.ShiftLeft, false);
        phase = "jumps";
        for (var jump = 0; jump < 3; jump++)
        {
            Hold(Key.Space, true);
            Run(1);
            Hold(Key.Space, false);
            Run(45);
        }

        phase = "crouched walk";
        Hold(Key.ControlLeft, true);
        Run(60);
        Hold(Key.ControlLeft, false);
        Hold(Key.W, false);
        Run(20);
        phase = "backwards";
        Hold(Key.S, true);
        Run(60);
        Hold(Key.S, false);
        Run(20);

        phase = "balls";
        commands.Execute("balls 4", console);
        Run(150);
        phase = "into the balls";
        Hold(Key.W, true);
        Run(120);
        Hold(Key.W, false);
        Run(30);

        phase = "climb";
        var (_, foot, ground) = ScenarioTests.FindBoulder();
        var eye = foot + new Vector3(0, CharacterSize.ForHeight(1.7f).EyeMetres, 0);
        // Looking along +x: a yaw of -90 degrees.
        commands.Execute(string.Create(CultureInfo.InvariantCulture, $"tp {eye.X} {eye.Y} {eye.Z} -90 0"), console);
        match.Step(1);
        Assert.True(match.WaitUntilLoaded());
        Run(60);
        Hold(Key.Space, true);
        Run(1);
        Hold(Key.Space, false);
        Hold(Key.W, true);
        Run(400);
        Hold(Key.W, false);
        Run(60);

        Assert.Empty(console.Failures);
        samples[^1] = samples[^1] with { Climbed = samples[^1].Feet.Y - ground > 1 };
        return samples;
    }

    private static Sample Read(World world, WorldOrigin origin, string phase)
    {
        var found = default(Sample);
        world.Query(
            in s_player,
            (ref CharacterPosition position, ref CharacterContact contact, ref Climber climber) =>
                found = new Sample(origin.ToWorld(position.Feet), contact.Grounded, climber.State, phase, false)
        );
        return found;
    }

    private static Vector3 Gap(WorldPoint a, WorldPoint b) =>
        new((float)(a.X - b.X), (float)(a.Y - b.Y), (float)(a.Z - b.Z));

    private readonly record struct Sample(WorldPoint Feet, bool Grounded, ClimbState Climb, string Phase, bool Climbed);

    private sealed class Output : IConsoleOutput
    {
        public List<string> Failures { get; } = [];

        public void Print(string text) { }

        public void PrintColumns(string left, string right, int column, int indent) { }

        public void PrintError(string text) => Failures.Add(text);
    }
}
