using System.Globalization;
using System.Numerics;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.MeshSample.Systems;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.MeshSample.Tests;

/// <summary>
/// <see cref="AimHighlightSystem"/> in the sample played without a window: looking at a block within reach shows a box
/// round it a little larger than it; looking up, at a block out of reach, or at a piece that is not climbable (the
/// glass) shows none; a frame
/// allocates nothing. And <see cref="YardColliders"/> names each collider's piece.
/// </summary>
public sealed class AimHighlightSystemTests
{
    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
    };

    private static readonly float s_eyeHeight = CharacterSize.ForHeight(1.7f).EyeMetres;

    [Fact]
    public void Looking_at_a_block_in_reach_shows_a_box_round_it_and_looking_away_hides_it()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var highlight = provider.GetRequiredService<AimHighlight>();

        // The 2 m block (x -4 to -2, z -6 to -4), from 0.5 m off its +z face, looking along -z.
        Teleport(provider, new Vector3(-3, s_eyeHeight, -3.5f), pitch: 0);
        match.Step(10);
        var shown = highlight.Active;
        var box = highlight.Transform;

        Teleport(provider, new Vector3(-3, s_eyeHeight, -3.5f), pitch: 70);
        match.Step(10);
        var lookingUp = highlight.Active;

        Teleport(provider, new Vector3(-3, s_eyeHeight, -3.5f), pitch: 0);
        match.Step(10);
        var again = highlight.Active;

        Assert.True(shown);
        var rim = AimHighlightSystem.RimMetres;
        var expected =
            Matrix4x4.CreateScale(2 + 2 * rim) * Matrix4x4.CreateTranslation(new Vector3(-4, 0, -6) - new Vector3(rim));
        Assert.Equal(expected, box);
        Assert.False(lookingUp);
        Assert.True(again);
    }

    [Theory]
    [InlineData(-3, -2.3f, 0)] // the same block from 1.7 m: out of reach
    [InlineData(3.5f, 1.6f, 0)] // the glass pane (x 2 to 5, z 1 to 1.1) 0.5 m ahead: in reach, not climbable
    public void Nothing_out_of_reach_or_not_climbable_is_shown(float x, float z, float pitch)
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());

        Teleport(provider, new Vector3(x, s_eyeHeight, z), pitch);
        match.Step(10);

        Assert.False(provider.GetRequiredService<AimHighlight>().Active);
    }

    [Fact]
    public void A_frame_allocates_nothing()
    {
        using var provider = SampleComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        Teleport(provider, new Vector3(-3, s_eyeHeight, -3.5f), pitch: 0);
        match.Step(10);
        var system = match.Services.GetServices<IFrameUpdateSystem>().OfType<AimHighlightSystem>().Single();

        Assert.True(provider.GetRequiredService<AimHighlight>().Active);
        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FrameTime(1f / 60, 1, 1, 0)));
    }

    [Fact]
    public void The_yard_colliders_name_each_piece_and_no_other_collider()
    {
        var yard = new YardColliders();
        for (var i = 0; i < Yard.Pieces.Count; i++)
        {
            yard.Add(10 + i, Yard.Pieces[i]);
        }

        Assert.True(yard.TryGet(12, out var piece));
        Assert.Equal(Yard.Pieces[2], piece);
        Assert.False(yard.TryGet(9, out _));
        Assert.Throws<InvalidOperationException>(() => yard.Add(99, Yard.Pieces[0]));
    }

    private static void Teleport(IServiceProvider provider, Vector3 eye, float pitch)
    {
        var output = new Output();
        provider
            .GetRequiredService<ConsoleCommands>()
            .Execute(string.Create(CultureInfo.InvariantCulture, $"tp {eye.X} {eye.Y} {eye.Z} 0 {pitch}"), output);
        Assert.Empty(output.Failures);
    }

    private sealed class Output : IConsoleOutput
    {
        public List<string> Failures { get; } = [];

        public void Print(string text) { }

        public void PrintError(string text) => Failures.Add(text);
    }
}
