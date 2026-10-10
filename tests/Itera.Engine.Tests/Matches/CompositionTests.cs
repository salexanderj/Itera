using System.Numerics;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Microsoft.Extensions.DependencyInjection;

namespace Itera.Engine.Tests.Matches;

/// <summary>
/// The engine's composition with the least a game adds (<see cref="EngineComposition"/>): it builds with every
/// registration checked, the run's services (the loop among them) resolve from the root without a match's (scoped)
/// service, a match's scope resolves its services (without terrain, a character mover over the mesh colliders alone,
/// which never waits, the engine's default play area, voxel models that make balls' pieces with no world, and one origin
/// that the world, the mover, the bodies, and the mesh colliders share), and a game's fixed and frame systems come out in the order it
/// registered them, which is the order they run in.
/// </summary>
public sealed class CompositionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_run_resolves_from_the_root_and_a_match_from_its_scope(bool terrain)
    {
        using var provider = EngineComposition.Build(terrain: terrain);

        Assert.NotNull(provider.GetRequiredService<GameLoop>());
        using var scope = provider.CreateScope();
        scope
            .ServiceProvider.GetRequiredService<MatchContext>()
            .Set(new MatchStart(SessionMode.Offline, SessionTransportKind.Direct, null, 1, null, false));
        var match = scope.ServiceProvider.GetRequiredService<MatchServices>();
        var mover = scope.ServiceProvider.GetRequiredService<VoxelCharacterMover>();
        var area = scope.ServiceProvider.GetRequiredService<PlayArea>();
        Assert.Equal(terrain, match.Terrain is not null);
        Assert.Equal(!terrain, ReferenceEquals(PlayArea.WithoutTerrain, area));
        var bodies = scope.ServiceProvider.GetRequiredService<LooseBodies>();
        Assert.Equal(terrain, bodies.CanSettle);
        Assert.Same(scope.ServiceProvider.GetRequiredService<LooseBodyVoxels>(), match.BodyVoxels);
        // One origin for the match: every service that keeps local positions measures from it.
        var origin = scope.ServiceProvider.GetRequiredService<WorldOrigin>();
        Assert.Same(origin, mover.Origin);
        Assert.Same(origin, bodies.Origin);
        Assert.Same(origin, scope.ServiceProvider.GetRequiredService<MeshColliders>().Origin);
        if (terrain)
        {
            Assert.Same(origin, scope.ServiceProvider.GetRequiredService<VoxelWorld>().Origin);
        }

        if (!terrain)
        {
            Assert.True(mover.IsResident(new Vector3(300, 2, -300), 1));
            // The models keep their trees in their own memory: a ball's piece is made with no world.
            Assert.True(match.Models.MakeBall(10, 3).SolidVoxels > 0);
        }
    }

    [Fact]
    public void Systems_come_out_in_the_order_the_game_registered_them()
    {
        var services = EngineComposition.Services();
        services.AddScoped<IFixedUpdateSystem>(sp => new Second(sp.GetRequiredService<EcsWorld>().World));
        services.AddScoped<IFrameUpdateSystem>(sp => new SecondFrame(sp.GetRequiredService<EcsWorld>().World));
        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
        using var scope = provider.CreateScope();

        Assert.Equal(
            [typeof(EngineComposition.StepCounter), typeof(Second)],
            scope.ServiceProvider.GetServices<IFixedUpdateSystem>().Select(s => s.GetType())
        );
        Assert.Equal(
            [typeof(EngineComposition.FrameCounter), typeof(SecondFrame)],
            scope.ServiceProvider.GetServices<IFrameUpdateSystem>().Select(s => s.GetType())
        );
    }

    private sealed class Second(Arch.Core.World world)
        : Arch.System.BaseSystem<Arch.Core.World, Itera.Modding.FixedStepTime>(world),
            IFixedUpdateSystem;

    private sealed class SecondFrame(Arch.Core.World world)
        : Arch.System.BaseSystem<Arch.Core.World, Itera.Modding.FrameTime>(world),
            IFrameUpdateSystem;
}
