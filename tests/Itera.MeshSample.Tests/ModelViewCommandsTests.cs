using System.Numerics;
using Arch.Core;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.MeshSample.Tests;

/// <summary>
/// The engine's commands that show one model from set places, in a match of the sample (no voxel world): <c>viewpoint</c>
/// draws the frames from a place in the world and gives the camera's own back, and <c>meshes place NAME at</c> puts a
/// model at a place in the world, turned, and prints where. Both read the world through the match's origin.
/// </summary>
public sealed class ModelViewCommandsTests
{
    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
    };

    [Fact]
    public void Viewpoint_draws_from_a_place_in_the_world_until_off()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        provider.GetRequiredService<EngineCommands>().Register();
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var view = provider.GetRequiredService<RenderView>();
        var origin = match.Services.GetRequiredService<OriginShifts>().Origin;
        var output = new RecordingOutput();

        commands.Execute("viewpoint 3,4,5,90,-30", output);
        var placed = view.DrawnCamera;
        commands.Execute("viewpoint off", output);

        Assert.Empty(output.Errors);
        Assert.Equal(origin.ToLocal(new WorldPoint(3, 4, 5)), placed.Position);
        AssertTurn(Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, -MathF.PI / 6, 0), placed.Orientation);
        Assert.Equal("viewpoint 3.00,4.00,5.00,90,-30", output.Lines[^2]);
        Assert.Null(view.CameraOverride);
    }

    [Fact]
    public void Meshes_place_at_puts_a_model_at_a_place_in_the_world_turned_and_says_where()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var commands = provider.GetRequiredService<ConsoleCommands>();
        var origin = match.Services.GetRequiredService<OriginShifts>().Origin;
        var output = new RecordingOutput();

        commands.Execute("meshes place fox at 5 0.5 -3 90 2", output);

        Assert.Empty(output.Errors);
        Assert.Equal("fox placed at 5.00 0.50 -3.00 in the world", output.Lines[^1]);
        var placement = Placed(match.World);
        Assert.Equal(origin.ToLocal(new WorldPoint(5, 0.5, -3)), placement.Position);
        AssertTurn(Quaternion.CreateFromYawPitchRoll(MathF.PI / 2, 0, 0), placement.Orientation);
        Assert.Equal(2, placement.Scale);
    }

    private static void AssertTurn(Quaternion expected, Quaternion actual)
    {
        if (MathF.Abs(Quaternion.Dot(expected, actual)) < 1 - 1e-6f)
        {
            Assert.Fail($"Expected the turn {expected}, got {actual}");
        }
    }

    private static MeshPlacement Placed(World world)
    {
        MeshPlacement? found = null;
        world.Query(
            new QueryDescription().WithAll<MeshPlacement, ConsoleMesh>(),
            (ref MeshPlacement placement) => found = placement
        );
        return Assert.NotNull(found);
    }

    private sealed class RecordingOutput : IConsoleOutput
    {
        public List<string> Lines { get; } = [];

        public List<string> Errors { get; } = [];

        public void Print(string text) => Lines.Add(text);

        public void PrintError(string text) => Errors.Add(text);
    }
}
