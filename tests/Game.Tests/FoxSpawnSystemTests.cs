using System.Numerics;
using Arch.Core;
using Game.Systems;
using Game.WorldGeneration;
using Itera.Engine.Assets;
using Itera.Engine.Components;
using Itera.Engine.Rendering;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;

namespace Game.Tests;

/// <summary>
/// The starter's fox: put once at the start on the ground 4 m in front of the start pose, its head towards the start's
/// right; a game without the model declared stops; a step afterwards does nothing and allocates nothing.
/// </summary>
public sealed class FoxSpawnSystemTests
{
    private const ulong Seed = 7;
    private static readonly QueryDescription s_meshes = new QueryDescription().WithAll<MeshPlacement, DrawnMesh>();
    private readonly MeshAssets _assets = new(NullLogger<MeshAssets>.Instance);

    public FoxSpawnSystemTests() => GameModels.Declare(_assets);

    [Fact]
    public void The_start_puts_the_fox_on_the_ground_in_front_side_on()
    {
        using var world = World.Create();
        // Looking along -x (yaw a quarter turn), from 20 m up.
        var start = new StartPose(new Vector3(3, 20, -4), MathF.PI / 2, -0.2f);
        using var system = Create(world, start);

        system.Initialize();

        Assert.Equal(1, world.CountEntities(in s_meshes));
        world.Query(
            in s_meshes,
            (ref MeshPlacement placement, ref DrawnMesh mesh) =>
            {
                Assert.Equal(_assets.Find(GameModels.Fox), mesh.Asset);
                Assert.Equal(ModelShadow.Casts, mesh.Shadow);
                var ground = (float)GameGenerator.SurfaceHeight(-100, -400, Seed) / 100;
                Assert.Equal(-1, placement.Position.X, 1e-4f);
                Assert.Equal(ground, placement.Position.Y, 1e-4f);
                Assert.Equal(-4, placement.Position.Z, 1e-4f);
                Assert.Equal(placement.Position, placement.PreviousPosition);
                Assert.Equal(1, placement.Scale);
                // Its head along -z, the right of a camera looking along -x.
                var head = Vector3.Transform(Vector3.UnitZ, placement.Orientation);
                Assert.Equal(0, head.X, 1e-5f);
                Assert.Equal(-1, head.Z, 1e-5f);
            }
        );
    }

    [Fact]
    public void Without_the_model_declared_the_start_stops()
    {
        using var world = World.Create();
        using var system = new FoxSpawnSystem(
            world,
            new MeshAssets(NullLogger<MeshAssets>.Instance),
            new StartPose(Vector3.Zero, 0, 0),
            Seed,
            NullLogger<FoxSpawnSystem>.Instance
        );

        Assert.Throws<InvalidOperationException>(system.Initialize);
    }

    [Fact]
    public void A_step_does_nothing_and_allocates_nothing()
    {
        using var world = World.Create();
        using var system = Create(world, new StartPose(new Vector3(0, 8, 0), 0, 0));
        system.Initialize();

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FixedStepTime(1f / 60, 0)));
        Assert.Equal(1, world.CountEntities(in s_meshes));
    }

    private FoxSpawnSystem Create(World world, StartPose start) =>
        new(world, _assets, start, Seed, NullLogger<FoxSpawnSystem>.Instance);
}
