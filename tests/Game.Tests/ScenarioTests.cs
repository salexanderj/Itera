using System.Numerics;
using Arch.Core;
using Game.WorldGeneration;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Matches;
using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Input;

namespace Game.Tests;

/// <summary>
/// The starter played without a window (<see cref="HeadlessMatch"/>): its real world, player, and systems, stepped for
/// seconds of play and read through the ECS world. One physics thread, so the steps repeat.
/// </summary>
public sealed class ScenarioTests
{
    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
        ["Engine:Voxels:WorkerThreads"] = "4",
    };

    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        LocalPlayer,
        CharacterPosition,
        CharacterContact
    >();

    private static readonly QueryDescription s_balls = new QueryDescription().WithAll<LooseBody>();

    [Fact]
    public void The_player_stands_on_the_starters_ground()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);

        Assert.True(match.WaitUntilLoaded());
        match.Step(60);

        var (position, contact) = Player(match.World);
        var ground = GameGenerator.SurfaceHeight(0, 0, 1) / (float)VoxelConstants.CentimetresPerMetre;
        Assert.True(contact.Grounded);
        Assert.Equal(ground, position.Feet.Y, 0.15f);
    }

    [Fact]
    public void Holding_forward_for_a_second_walks_the_player_forward()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(30);
        var before = Player(match.World).Position.Feet;

        match.Input.InjectKey(Key.W, true);
        match.Step(60);
        match.Input.InjectKey(Key.W, false);

        var after = Player(match.World).Position.Feet;
        var walked = new Vector2(after.X - before.X, after.Z - before.Z);
        // The start pose looks along -z; walking is 4.5 m/s once up to speed.
        Assert.True(walked.Y < 0, $"walked {walked}");
        Assert.InRange(walked.Length(), 3f, MovementStats.Default.Walk + 0.01f);
    }

    [Fact]
    public void Balls_drop_rest_and_go_back_into_the_world()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(10);
        var edits = match.Match.Voxels.EditCount;
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();

        commands.Execute("balls 4", output);
        match.Step(10);
        var dropped = match.World.CountEntities(in s_balls);
        // Settled for 3 s, each ball is filled back into the grid as an edit.
        match.Step(60 * 10);

        Assert.Empty(output.Failures);
        Assert.Equal(4, dropped);
        Assert.Equal(0, match.World.CountEntities(in s_balls));
        Assert.Equal(edits + 4, match.Match.Voxels.EditCount);
    }

    private static (CharacterPosition Position, CharacterContact Contact) Player(World world)
    {
        var found = default((CharacterPosition, CharacterContact));
        var count = 0;
        world.Query(
            in s_player,
            (ref CharacterPosition position, ref CharacterContact contact) =>
            {
                found = (position, contact);
                count++;
            }
        );
        Assert.Equal(1, count);
        return found;
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Failures { get; } = [];

        public void Print(string text) { }

        public void PrintColumns(string left, string right, int column, int indent) { }

        public void PrintError(string text) => Failures.Add(text);
    }
}
