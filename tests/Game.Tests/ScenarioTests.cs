using System.Numerics;
using Arch.Core;
using Game.Components;
using Game.Systems;
using Game.WorldGeneration;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Matches;
using Itera.Engine.Rendering;
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

    private static readonly QueryDescription s_fox = new QueryDescription().WithAll<
        FoxRoute,
        MeshPlacement,
        MeshAnimator
    >();

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
    public void The_fox_stands_in_front_of_the_start_and_is_drawn_every_frame()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(2);

        var assets = provider.GetRequiredService<MeshAssets>();
        var fox = Assert.Single(provider.GetRequiredService<MeshInstances>().Instances.ToArray());
        Assert.Equal(assets.Find(GameModels.Fox), fox.Asset);
        Assert.Equal(576, assets[fox.Asset].TriangleCount);
        // Posed by its rig's 24 joints, looking about.
        Assert.True(fox.Posed);
        Assert.Equal(24, fox.Joints);
        // The start pose looks along -z from over the middle.
        var at = Vector3.Transform(Vector3.Zero, fox.Transform);
        var ground = GameGenerator.SurfaceHeight(0, -400, 1) / (float)VoxelConstants.CentimetresPerMetre;
        Assert.Equal(0, at.X, 1e-4f);
        Assert.Equal(ground, at.Y, 1e-4f);
        Assert.Equal(-FoxSystem.AheadMetres, at.Z, 1e-4f);
    }

    [Fact]
    public void The_foxs_clip_moves_on_with_the_steps_and_not_between_them()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(30);
        var before = Fox(match.World).Animator.Seconds;

        // Frames with no step between them, as while the match is paused: the clip stays.
        for (var i = 0; i < 30; i++)
        {
            match.Frame();
        }

        var held = Fox(match.World).Animator.Seconds;
        match.Step(60);
        var after = Fox(match.World).Animator.Seconds;

        Assert.Equal(before, held);
        var survey = provider.GetRequiredService<MeshAssets>()[0].Rig!.Clips[0];
        Assert.Equal("Survey", survey.Name);
        Assert.Equal((before + 1) % survey.Duration, after, 1e-3f);
    }

    [Fact]
    public void The_fox_walks_round_its_circle_after_looking_about()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var start = Fox(match.World).Placement.Position;

        // Two seconds into its walk.
        match.Step((int)((FoxSystem.SurveySeconds + 2) * 60));

        var (route, placement, animator) = Fox(match.World);
        var walk = provider.GetRequiredService<MeshAssets>()[0].Rig!.FindClip("Walk");
        Assert.Equal(walk, animator.Clip);
        var flat = new Vector2(placement.Position.X - route.Centre.X, placement.Position.Z - route.Centre.Z);
        Assert.Equal(FoxSystem.CircleMetres, flat.Length(), 1e-3f);
        Assert.InRange(Vector3.Distance(start, placement.Position), 1.5f, 2.5f);
        // On the ground: within a voxel of where a ray down finds it.
        var ground =
            GameGenerator.SurfaceHeight(
                (int)MathF.Round(placement.Position.X * 100),
                (int)MathF.Round(placement.Position.Z * 100),
                1
            ) / 100;
        Assert.Equal((float)ground, placement.Position.Y, 0.15f);
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

    private static (FoxRoute Route, MeshPlacement Placement, MeshAnimator Animator) Fox(World world)
    {
        var found = default((FoxRoute, MeshPlacement, MeshAnimator));
        var count = 0;
        world.Query(
            in s_fox,
            (ref FoxRoute route, ref MeshPlacement placement, ref MeshAnimator animator) =>
            {
                found = (route, placement, animator);
                count++;
            }
        );
        Assert.Equal(1, count);
        return found;
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
