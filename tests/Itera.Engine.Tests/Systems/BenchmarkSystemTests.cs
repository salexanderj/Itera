using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Systems;

public sealed class BenchmarkSystemTests
{
    private const double Frame = 0.1;
    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();
    private static readonly BenchmarkView[] TwoViews =
    [
        new("a", new Vector3(1, 2, 3), 0.5f, 0.1f),
        new("b", new Vector3(4, 5, 6), 1f, -0.2f),
    ];

    private readonly FakeHost _host = new();

    [Fact]
    public void The_camera_stays_at_a_view_while_it_is_loading()
    {
        using var world = CreateWorld();
        using var system = CreateSystem(world);
        system.Initialize();

        for (var i = 0; i < 300; i++)
        {
            system.Update(new FrameTime((float)Frame, i * Frame, (ulong)i, 0));
            Assert.Equal(TwoViews[0].Position, ReadPose(world).Position);
        }

        Assert.Empty(_host.Exits);
    }

    [Fact]
    public void Each_view_loads_settles_and_is_measured_then_the_circle_is_flown_and_the_game_exits_once()
    {
        using var world = CreateWorld();
        using var system = CreateSystem(world);
        system.Initialize();
        _host.IsLoaded = true;

        var visited = new List<Vector3>();
        var exitedAt = double.NaN;
        for (var i = 0; i < 400 && double.IsNaN(exitedAt); i++)
        {
            var now = i * Frame;
            system.Update(new FrameTime((float)Frame, now, (ulong)i, 0));
            var position = ReadPose(world).Position;
            if (visited.Count == 0 || visited[^1] != position)
            {
                visited.Add(position);
            }

            if (_host.Exits.Count > 0)
            {
                exitedAt = now;
            }
        }

        // Per view: 1 s before trusting "loaded", 0.5 s loaded, 1 s settling, 2 s measuring. Then the circle's start
        // loads (1.5 s) and is flown for 3 s: 13.5 s, and a frame later for each wait that rounding ends a frame late.
        Assert.InRange(exitedAt, 13.5 - 1e-9, 13.5 + 7 * Frame);
        Assert.Equal(["benchmark finished"], _host.Exits);
        Assert.Equal(TwoViews[0].Position, visited[0]);
        Assert.Equal(TwoViews[1].Position, visited[1]);
        Assert.Equal(DebugFlyCameraSystem.CirclePose(BenchmarkSystem.FlightSpeed, 0, 20).Position, visited[2]);
        Assert.True(visited.Count > 10, "The camera should move along the circle.");
    }

    /// <summary>
    /// A view that never loads is measured anyway after the load timeout, said once: the timeout's Warning and no
    /// "loaded" line after it. A flight of 0 s ends the run after the views.
    /// </summary>
    [Fact]
    public void A_view_that_never_loads_is_measured_after_the_timeout_and_a_flight_of_0_s_ends_after_the_views()
    {
        using var world = CreateWorld();
        var lines = new List<string>();
        using var system = CreateSystem(world, flightSeconds: 0, logger: new ListLogger(lines));
        system.Initialize();

        var exitedAt = double.NaN;
        for (var i = 0; i < 2000 && double.IsNaN(exitedAt); i++)
        {
            system.Update(new FrameTime((float)Frame, i * Frame, (ulong)i, 0));
            if (_host.Exits.Count > 0)
            {
                exitedAt = i * Frame;
            }
        }

        // Each view waits the 60 s timeout, settles 1 s, and measures 2 s; with no flight the circle is never loaded.
        Assert.InRange(exitedAt, 2 * 63 - 1e-9, 2 * 63 + 10 * Frame);
        Assert.Equal(["benchmark finished"], _host.Exits);
        Assert.Equal(2, lines.Count(line => line.Contains("still loading", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, line => line.Contains("loaded after", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_allocates_nothing_while_measuring()
    {
        using var world = CreateWorld();
        using var system = CreateSystem(world, measureSeconds: 1000);
        system.Initialize();
        _host.IsLoaded = true;
        for (var i = 0; i < 40; i++)
        {
            system.Update(new FrameTime((float)Frame, i * Frame, (ulong)i, 0));
        }

        Assert.Equal(0, SystemAllocation.BytesAllocated(system, new FrameTime((float)Frame, 5, 50, 0)));
    }

    private static World CreateWorld()
    {
        var world = World.Create();
        world.Create(new CameraPose(), new ActiveCamera());
        return world;
    }

    private BenchmarkSystem CreateSystem(
        World world,
        double measureSeconds = 2,
        double flightSeconds = 3,
        ILogger<BenchmarkSystem>? logger = null
    ) =>
        new(
            world,
            _host,
            new BenchmarkSettings
            {
                Enabled = true,
                SettleSeconds = 1,
                MeasureSeconds = measureSeconds,
                FlightSeconds = flightSeconds,
            },
            TwoViews,
            logger ?? NullLogger<BenchmarkSystem>.Instance
        );

    private static CameraPose ReadPose(World world)
    {
        var pose = default(CameraPose);
        world.Query(in s_camera, (ref CameraPose p) => pose = p);
        return pose;
    }

    private sealed class ListLogger(List<string> lines) : ILogger<BenchmarkSystem>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => lines.Add(formatter(state, exception));
    }

    private sealed class FakeHost : IBenchmarkHost
    {
        public bool IsLoaded { get; set; }

        public (int Width, int Height) FramebufferSize => (640, 360);

        public List<string> Exits { get; } = [];

        public void RequestExit(string reason) => Exits.Add(reason);
    }
}
