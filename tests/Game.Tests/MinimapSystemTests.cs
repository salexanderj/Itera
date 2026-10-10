using System.Numerics;
using Arch.Core;
using Game.Systems;
using Itera.Engine.Components;
using Itera.Engine.Input;
using Itera.Engine.Matches;
using Itera.Engine.Rendering;
using Itera.Engine.Settings;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Silk.NET.Input;

namespace Game.Tests;

/// <summary>
/// <see cref="MinimapSystem"/> in the starter played without a window: it asks for the minimap's image view each frame
/// from an orthographic camera at the player's eye looking straight down, north up, 64 m across, its arrow following the
/// look; M hides and shows it, kept in the player's settings (on at first), unless the input is blocked; the benchmark
/// hides it; a frame allocates nothing. And the minimap's place on the HUD (<see cref="MinimapLayer.Map"/>).
/// </summary>
public sealed class MinimapSystemTests
{
    private static readonly FrameTime Time = new(1f / 60, 1, 1, 0);

    private static readonly QueryDescription s_localPlayer = new QueryDescription().WithAll<
        PlayerCommand,
        LocalPlayer
    >();

    private static readonly Dictionary<string, string?> s_options = new()
    {
        ["Engine:Physics:Threads"] = "1",
        ["Engine:Audio:Enabled"] = "false",
        ["Engine:Steam:Enabled"] = "false",
    };

    [Fact]
    public void The_map_is_drawn_round_the_players_eye_looking_straight_down_north_up()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        match.Step(2);
        var minimap = provider.GetRequiredService<Minimap>();
        var views = provider.GetRequiredService<RenderViews>();
        var player = ToolAim.FindPlayer(match.World);

        Assert.True(minimap.Shown);
        var info = views.Info(minimap.View);
        Assert.Equal(
            (Minimap.ViewName, Minimap.Texels, Minimap.EveryFrames),
            (info.Name, info.Width, info.EveryFrames)
        );
        Assert.True(views.Asked(minimap.View, out var camera));
        Assert.True(camera.Orthographic);
        Assert.Equal(64, camera.HeightMetres);
        Assert.Equal(player.Eye, camera.Position);
        AssertClose(-Vector3.UnitY, camera.Forward);
        AssertClose(-Vector3.UnitZ, camera.Up);
        AssertClose(Vector3.UnitX, camera.Right);

        // Its arrow follows the player's look.
        match.World.Query(in s_localPlayer, static (ref PlayerCommand command) => command.Yaw = 1.25f);
        var (system, _) = NewSystem(provider, match, new PressedInput(), benchmark: false);
        using (system)
        {
            system.Initialize();
            system.Update(in Time);
        }

        Assert.Equal(1.25f, minimap.Yaw);
    }

    [Fact]
    public void M_hides_and_shows_the_map_in_the_players_settings_unless_the_input_is_blocked()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var minimap = provider.GetRequiredService<Minimap>();
        var settings = provider.GetRequiredService<PlayerSettings>();
        var block = match.Services.GetRequiredService<PlayerInputBlock>();
        var input = new PressedInput();
        var (system, views) = NewSystem(provider, match, input, benchmark: false);
        using var owned = system;
        system.Initialize();
        var onAtFirst = settings.IsOn(minimap.Switch);

        input.Press(Key.M);
        system.Update(in Time);
        var hidden = (settings.IsOn(minimap.Switch), minimap.Shown, views.Asked(minimap.View, out _));
        block.Blocked = true;
        input.Press(Key.M);
        system.Update(in Time);
        var blocked = settings.IsOn(minimap.Switch);
        block.Blocked = false;
        input.Press(Key.M);
        system.Update(in Time);

        Assert.True(onAtFirst);
        Assert.Equal((false, false, false), hidden);
        Assert.False(blocked);
        Assert.True(settings.IsOn(minimap.Switch));
        Assert.True(minimap.Shown);
        Assert.True(views.Asked(minimap.View, out _));
    }

    [Fact]
    public void The_benchmark_hides_the_map()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var minimap = provider.GetRequiredService<Minimap>();
        var (system, views) = NewSystem(provider, match, new PressedInput(), benchmark: true);
        using var owned = system;
        system.Initialize();

        system.Update(in Time);

        Assert.False(minimap.Shown);
        Assert.False(views.Asked(minimap.View, out _));
    }

    [Fact]
    public void A_frame_allocates_nothing()
    {
        using var provider = GameComposition.Build(s_options);
        using var match = new HeadlessRun(provider).Play(seed: 1);
        Assert.True(match.WaitUntilLoaded());
        var (system, _) = NewSystem(provider, match, new PressedInput(), benchmark: false);
        using var owned = system;
        system.Initialize();

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, Time));
    }

    /// <summary>
    /// The map is a square in the top-right corner, a fifth of the screen's height, a sixtieth of it in from the edges,
    /// and that far below other ink when there is some (a shown stats line).
    /// </summary>
    [Theory]
    [InlineData(2560, 1440, 0, 2248, 24, 288)]
    [InlineData(1280, 720, 0, 1124, 12, 144)]
    [InlineData(2560, 1440, 44, 2248, 68, 288)]
    public void The_map_sits_in_the_top_right_corner(int width, int height, int below, int left, int top, int side)
    {
        var map = MinimapLayer.Map(width, height, below);
        var margin = width - map.Right;

        Assert.Equal((left, top, side, side), (map.Left, map.Top, map.Width, map.Height));
        Assert.Equal(Math.Max(margin, below + margin), map.Top);
    }

    /// <summary>
    /// A system over the match's world and the run's minimap and settings, asking for the minimap in views of its own
    /// (declared as the game declares it, so the same index), which no other system asks in.
    /// </summary>
    private static (MinimapSystem System, RenderViews Views) NewSystem(
        ServiceProvider provider,
        HeadlessMatch match,
        IFrameInput input,
        bool benchmark
    )
    {
        var views = new RenderViews();
        views.Declare(Minimap.ViewName, Minimap.Texels, Minimap.Texels, Minimap.EveryFrames);
        return (
            new MinimapSystem(
                match.World,
                provider.GetRequiredService<Minimap>(),
                views,
                provider.GetRequiredService<PlayerSettings>(),
                input,
                match.Services.GetRequiredService<PlayerInputBlock>(),
                provider.GetRequiredService<IOptions<MinimapOptions>>().Value,
                benchmark
            ),
            views
        );
    }

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"Expected {expected}, got {actual}.");

    /// <summary>Frame input whose next snapshot has a key pressed (<see cref="Press"/>), then none.</summary>
    private sealed class PressedInput : IFrameInput
    {
        private readonly InputAccumulator _accumulator = new();
        private InputSnapshot _snapshot;

        public ref readonly InputSnapshot Frame => ref _snapshot;

        public Vector2 MouseSinceTick => Vector2.Zero;

        public void Press(Key key)
        {
            _accumulator.KeyDown(key);
            _snapshot = _accumulator.TakeFrame(true);
            _accumulator.KeyUp(key);
        }
    }
}
