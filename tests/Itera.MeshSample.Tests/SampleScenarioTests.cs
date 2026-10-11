using System.Globalization;
using System.Numerics;
using Arch.Core;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Matches;
using Itera.Engine.Physics;
using Itera.Engine.Rendering;
using Itera.MeshSample.Systems;
using Microsoft.Extensions.DependencyInjection;
using Silk.NET.Input;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The sample played without a window (<see cref="HeadlessMatch"/>): a match with no voxel world, whose player stands
/// on the yard's mesh ground, climbs a block onto its top, and stops at the yard's edge; whose balls land on the ground
/// and stay there; whose every piece and fox is drawn each frame; and whose fox walks its circle.
/// </summary>
public sealed class SampleScenarioTests
{
    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
    };

    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        LocalPlayer,
        CharacterPosition,
        CharacterContact,
        Climber
    >();

    private static readonly QueryDescription s_balls = new QueryDescription().WithAll<LooseBody>();
    private static readonly QueryDescription s_fox = new QueryDescription().WithAll<MeshPlacement, MeshAnimator>();

    [Fact]
    public void A_match_without_voxels_stands_the_player_on_the_mesh_ground()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());

        match.Step(60);

        var (feet, contact, _) = Player(match.World);
        Assert.Null(match.Match.Terrain);
        Assert.True(contact.Grounded);
        Assert.Equal(SampleSpawnSystem.StartFeet.Y, feet.Y, 0.01f);
    }

    [Fact]
    public void The_player_climbs_a_block_onto_its_top()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();
        // Facing the 2 m block (from x -4 to -2, z -6 to -4) from 0.24 m off its +z face, looking along -z.
        var eye = new Vector3(-3, CharacterSize.ForHeight(1.7f).EyeMetres, -3.5f);
        commands.Execute(string.Create(CultureInfo.InvariantCulture, $"tp {eye.X} {eye.Y} {eye.Z} 0 0"), output);
        match.Step(30);

        match.Input.InjectKey(Key.Space, true);
        match.Step(1);
        match.Input.InjectKey(Key.Space, false);
        var grabbed = Player(match.World).Climber.State == ClimbState.Climbing;
        match.Input.InjectKey(Key.W, true);
        for (var step = 0; step < 300 && Player(match.World).Climber.State != ClimbState.Free; step++)
        {
            match.Step(1);
        }

        match.Input.InjectKey(Key.W, false);
        match.Step(30);
        var (feet, contact, _) = Player(match.World);

        Assert.Empty(output.Failures);
        Assert.True(grabbed);
        Assert.True(contact.Grounded);
        Assert.Equal(2, feet.Y, 0.02f);
    }

    [Fact]
    public void Balls_land_on_the_mesh_ground_and_stay_there()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(2);
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();

        commands.Execute("balls 3", output);
        // Past the time a ball rests before it would settle into a grid (3 s): with none, it stays.
        match.Step(60 * 6);

        var bodies = match.Services.GetRequiredService<LooseBodies>();
        var pivots = new List<Vector3>();
        match.World.Query(in s_balls, (ref LooseBody body) => pivots.Add(bodies.PivotOf(body.Body)));
        Assert.Empty(output.Failures);
        Assert.Equal(3, pivots.Count);
        // On the ground: a 12 cm ball's middle 12 cm up.
        Assert.All(pivots, pivot => Assert.InRange(pivot.Y, 0.1f, 0.15f));
    }

    [Fact]
    public void A_frame_draws_every_piece_of_the_yard_and_the_fox()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());

        match.Step(2);

        var assets = provider.GetRequiredService<MeshAssets>();
        var drawn = provider.GetRequiredService<MeshInstances>().Instances.ToArray().Select(i => i.Asset).ToList();
        Assert.Equal(Yard.Pieces.Count + 1, drawn.Count);
        Assert.Contains(assets.Find(SampleRunStartup.Fox), drawn);
        Assert.All(Yard.Pieces, piece => Assert.Contains(assets.Find(piece.Asset), drawn));
    }

    [Fact]
    public void The_yards_edge_is_a_wall()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var output = new Output();
        // 3 m from the east edge, looking east (a yaw of -90 degrees).
        var eye = CharacterSize.ForHeight(1.7f).EyeMetres;
        commands.Execute(string.Create(CultureInfo.InvariantCulture, $"tp 27 {eye} 0 -90 0"), output);
        match.Step(30);

        match.Input.InjectKey(Key.W, true);
        match.Step(120);
        match.Input.InjectKey(Key.W, false);
        var (feet, contact, _) = Player(match.World);

        Assert.Empty(output.Failures);
        Assert.True(contact.Grounded);
        Assert.InRange(feet.X, Yard.GroundHalfMetres - 0.3f, Yard.GroundHalfMetres - 0.25f);
    }

    [Fact]
    public void The_fox_walks_its_circle()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(2);
        var before = Fox(match.World);

        match.Step(60);

        var after = Fox(match.World);
        Assert.Equal(
            SampleFoxSystem.CircleMetres,
            Vector3.Distance(after, AtMiddle.Local(SampleFoxSystem.Middle)),
            1e-3f
        );
        Assert.Equal(SampleFoxSystem.Speed, Vector3.Distance(before, after), 0.05f);
    }

    private static (Vector3 Feet, CharacterContact Contact, Climber Climber) Player(World world)
    {
        var found = default((Vector3, CharacterContact, Climber));
        world.Query(
            in s_player,
            (ref CharacterPosition position, ref CharacterContact contact, ref Climber climber) =>
                found = (position.Feet, contact, climber)
        );
        return found;
    }

    private static Vector3 Fox(World world)
    {
        var found = Vector3.Zero;
        world.Query(in s_fox, (ref MeshPlacement placement) => found = placement.Position);
        return found;
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Failures { get; } = [];

        public void Print(string text) { }

        public void PrintError(string text) => Failures.Add(text);
    }
}
