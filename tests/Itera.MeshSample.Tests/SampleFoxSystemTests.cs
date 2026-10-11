using System.Numerics;
using Arch.Core;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.MeshSample.Components;
using Itera.MeshSample.Systems;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.MeshSample.Tests;

/// <summary>
/// <see cref="SampleFoxSystem"/>: the fox starts on its circle, walks round it at its speed facing the way it goes, its
/// pose of the step before kept; a mesh placed by the console with a clip is left alone; it stops without its model
/// declared; and a step allocates nothing.
/// </summary>
public sealed class SampleFoxSystemTests
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private static readonly QueryDescription s_fox = new QueryDescription().WithAll<SampleFox, MeshPlacement>();

    private readonly MeshAssets _assets = new(NullLogger<MeshAssets>.Instance);

    public SampleFoxSystemTests()
    {
        // A stand-in for the fox: the yard's block, which has no rig.
        Yard.Add(_assets);
        _assets.Add(SampleRunStartup.Fox, _assets[_assets.Find(Yard.Block)]);
    }

    [Fact]
    public void It_walks_round_its_circle_facing_the_way_it_goes()
    {
        using var world = World.Create();
        using var system = new SampleFoxSystem(world, _assets, AtMiddle.Origin);
        system.Initialize();
        var start = Placement(world).Position;

        for (var i = 0; i < 60; i++)
        {
            system.Update(in Step);
        }

        var placement = Placement(world);
        var angle = SampleFoxSystem.Speed / SampleFoxSystem.CircleMetres;
        var expected =
            AtMiddle.Local(SampleFoxSystem.Middle)
            + new Vector3(MathF.Cos(angle), 0, MathF.Sin(angle)) * SampleFoxSystem.CircleMetres;
        var way = new Vector3(-MathF.Sin(angle), 0, MathF.Cos(angle));
        Assert.Equal(AtMiddle.Local(SampleFoxSystem.Middle) + Vector3.UnitX * SampleFoxSystem.CircleMetres, start);
        Assert.True(Vector3.Distance(expected, placement.Position) < 1e-3f, $"At {placement.Position}.");
        Assert.True(Vector3.Distance(way, Vector3.Transform(Vector3.UnitZ, placement.Orientation)) < 1e-3f);
        Assert.True(placement.PreviousPosition != placement.Position);
    }

    [Fact]
    public void A_mesh_the_console_placed_with_a_clip_is_left_alone()
    {
        using var world = World.Create();
        using var system = new SampleFoxSystem(world, _assets, AtMiddle.Origin);
        system.Initialize();
        var placed = world.Create(
            MeshPlacement.At(new Vector3(20, 0, 20), Quaternion.Identity),
            DrawnMesh.Of(0),
            MeshAnimator.Playing(0),
            new ConsoleMesh()
        );

        system.Update(in Step);

        Assert.Equal(new Vector3(20, 0, 20), world.Get<MeshPlacement>(placed).Position);
    }

    [Fact]
    public void Without_its_model_declared_the_start_stops()
    {
        using var world = World.Create();
        using var system = new SampleFoxSystem(world, new MeshAssets(NullLogger<MeshAssets>.Instance), AtMiddle.Origin);

        var error = Assert.Throws<InvalidOperationException>(system.Initialize);
        Assert.Contains(SampleRunStartup.Fox, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_allocates_nothing()
    {
        using var world = World.Create();
        using var system = new SampleFoxSystem(world, _assets, AtMiddle.Origin);
        system.Initialize();

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Step));
    }

    private static MeshPlacement Placement(World world)
    {
        var found = default(MeshPlacement);
        world.Query(in s_fox, (ref MeshPlacement placement) => found = placement);
        return found;
    }
}
