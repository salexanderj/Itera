using Itera.Engine;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Tests;

/// <summary>
/// The game's composition: the real service collection, from the shipped settings, builds with every registration
/// checked, the run's services resolve from the root without a match's (scoped) service, a match's scope resolves its
/// services, and its fixed and frame systems come out in the order <c>GameServiceCollectionExtensions</c> gives, which
/// is the order they run in. A system added to the game is added here too, where it runs.
/// </summary>
public sealed class SystemOrderTests
{
    private static readonly string[] s_fixedOrder =
    [
        "PlayerSpawnSystem",
        "MapResetSystem",
        "DebugBallSystem",
        "PlayerInputSystem",
        "PhysicsReadSystem",
        "PlayerMovementSystem",
        "StandInSystem",
        "LooseBodySystem",
        "StreamingCentreSystem",
    ];

    private static readonly string[] s_frameOrder =
    [
        "PlayerCameraSystem",
        "DebugFlyCameraSystem",
        "DebugOutlineSystem",
        "CameraExtractSystem",
        "LooseBodyModelSystem",
        "AtmosphereSystem",
    ];

    [Fact]
    public void The_systems_run_in_the_documented_order() => AssertOrder([], s_frameOrder);

    /// <summary>With the benchmark on, everything still composes and its system runs right after the fly camera.</summary>
    [Fact]
    public void With_the_benchmark_on_its_system_runs_after_the_fly_camera() =>
        AssertOrder(
            new Dictionary<string, string?> { ["Engine:Benchmark:Enabled"] = "true" },
            [.. s_frameOrder[..2], "BenchmarkSystem", .. s_frameOrder[2..]]
        );

    private static void AssertOrder(Dictionary<string, string?> options, string[] frameOrder)
    {
        using var provider = GameComposition.Build(options);

        // The run: every run-wide service, none of which may take a match's (scoped) one (ValidateScopes throws).
        _ = provider.GetRequiredService<GameLoop>();
        // A match offline: its session's step settles the match's seed, which systems read.
        using var scope = provider.CreateScope();
        var match = scope.ServiceProvider;
        match
            .GetRequiredService<MatchContext>()
            .Set(new MatchStart(SessionMode.Offline, SessionTransportKind.Direct, null, 1, null, false));
        var matchServices = match.GetRequiredService<MatchServices>();
        matchServices.Session.Initialize();

        Assert.Equal(s_fixedOrder, match.GetServices<IFixedUpdateSystem>().Select(s => s.GetType().Name));
        Assert.Equal(frameOrder, match.GetServices<IFrameUpdateSystem>().Select(s => s.GetType().Name));
        matchServices.Session.Stop();
    }
}
