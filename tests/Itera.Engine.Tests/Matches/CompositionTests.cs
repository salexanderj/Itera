using System.Numerics;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Networking;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

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
    [Fact]
    public void A_games_own_menu_takes_the_engines_place_among_the_layers_and_in_the_loop()
    {
        using var provider = EngineComposition.Build(configure: services => services.AddSingleton<IMenu, GameMenu>());

        var menu = provider.GetRequiredService<IMenu>();
        Assert.IsType<GameMenu>(menu);
        var layers = provider.GetServices<IUiLayer>().ToList();
        Assert.Contains(menu, layers);
        Assert.DoesNotContain(layers, layer => layer is MenuLayer);
        Assert.True(menu.AboveMatch);
        Assert.NotNull(provider.GetRequiredService<GameLoop>());
    }

    [Fact]
    public void With_the_loop_made_a_friends_steam_join_is_taken_whatever_the_menu()
    {
        using var provider = EngineComposition.Build(configure: services => services.AddSingleton<IMenu, GameMenu>());
        provider.GetRequiredService<GameLoop>();

        ((ISteamFriendsListener)provider.GetRequiredService<SteamFriendList>()).OnJoinRequested(5, "lobby:12");

        Assert.Equal("lobby:12", provider.GetRequiredService<MatchControl>().TakeStart()?.Address);
    }

    [Fact]
    public void The_engines_menu_is_the_default()
    {
        using var provider = EngineComposition.Build();

        Assert.Same(provider.GetRequiredService<MenuLayer>(), provider.GetRequiredService<IMenu>());
    }

    [Theory]
    [InlineData(true, true, SessionRole.Offline, MatchPhase.Playing, true)]
    [InlineData(false, true, SessionRole.Offline, MatchPhase.Playing, false)]
    [InlineData(true, false, SessionRole.Offline, MatchPhase.Playing, false)]
    [InlineData(true, true, SessionRole.Host, MatchPhase.Playing, false)]
    [InlineData(true, true, SessionRole.Client, MatchPhase.Playing, false)]
    [InlineData(true, true, SessionRole.Offline, MatchPhase.Loading, false)]
    public void The_menu_holds_only_an_offline_match_being_played_and_only_where_the_game_asks(
        bool asked,
        bool open,
        SessionRole role,
        MatchPhase phase,
        bool holds
    ) => Assert.Equal(holds, GameLoop.MenuHoldsSteps(asked, open, role, phase));

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

    /// <summary>A game's own menu, in place of the engine's: never open.</summary>
    private sealed class GameMenu : IMenu
    {
        public bool IsOpen => false;

        public ITextInput? TextFocus => null;

        public bool CapturingBinding => false;

        public bool Visible => false;

        public bool Dirty => false;

        public void Escape() { }

        public void BindingPressed(InputBinding binding) { }

        public void Update(in FrameTime time) { }

        public SKRectI Bounds(int width, int height) => new(0, 0, width, height);

        public void Draw(SKCanvas canvas, int width, int height) { }
    }
}
