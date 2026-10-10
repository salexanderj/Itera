using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Loading;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Systems;

/// <summary>
/// The GPU benchmark (frame group, after <see cref="DebugFlyCameraSystem"/>, whose pose it overrides; a game registers
/// it only when <c>Engine:Benchmark:Enabled</c> is set, with its own list of views). It puts the
/// <see cref="ActiveCamera"/> at each view of the seed's world in turn, waits until every column is loaded and on the
/// GPU, waits <see cref="BenchmarkSettings.SettleSeconds"/>, and logs the start and end of a measurement of
/// <see cref="BenchmarkSettings.MeasureSeconds"/>. Then it flies the automatic circle for
/// <see cref="BenchmarkSettings.FlightSeconds"/> and asks the game to exit. The GPU times come from the frame stats lines
/// between each start and end (<c>tools/benchmark</c> reads them). Writes <see cref="CameraPose"/>; no structural
/// changes. The engine is reached through <see cref="IBenchmarkHost"/>, so tests can drive it.
/// </summary>
public sealed partial class BenchmarkSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    /// <summary>Speed of the flight at the end, in metres per second, as in earlier measurements.</summary>
    public const double FlightSpeed = 5;

    /// <summary>Longest wait for a view to load before measuring anyway (with a Warning), in seconds.</summary>
    private const double LoadTimeoutSeconds = 60;

    /// <summary>A view counts as loaded once nothing has been waiting for this long, in seconds.</summary>
    private const double ReadySeconds = 0.5;

    /// <summary>The least time between moving the camera and trusting "nothing waiting", in seconds.</summary>
    private const double FirstCheckSeconds = 1;

    private static readonly QueryDescription s_query = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private readonly IBenchmarkHost _host;
    private readonly float _circleHeight;
    private readonly BenchmarkSettings _options;
    private readonly ILogger<BenchmarkSystem> _logger;
    private readonly BenchmarkView[] _views;

    // The match's origin: the views are absolute, the camera's pose local to it.
    private readonly WorldOrigin _origin;
    private int _view;
    private Phase _phase = Phase.Loading;
    private double _phaseStart = double.NaN;
    private double _readySince = double.NaN;

    public BenchmarkSystem(
        World world,
        IBenchmarkHost host,
        BenchmarkSettings options,
        IReadOnlyList<BenchmarkView> views,
        ILogger<BenchmarkSystem> logger,
        float circleHeightMetres = 20,
        WorldOrigin? origin = null
    )
        : base(world)
    {
        _origin = origin ?? new WorldOrigin();
        _circleHeight = circleHeightMetres;
        _host = host;
        _options = options;
        _views = [.. views];
        _logger = logger;
    }

    /// <summary>The name of the current view, or "flight" for the circle's start.</summary>
    private string ViewName => _view < _views.Length ? _views[_view].Name : "flight";

    private enum Phase
    {
        Loading,
        Settling,
        Measuring,
        Flying,
        Done,
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<CameraPose>(in s_query);
        LogStart(_views.Length, _options.FlightSeconds);
        foreach (var view in _views)
        {
            LogView(view.Name, view.Position.X, view.Position.Y, view.Position.Z, view.Yaw, view.Pitch);
        }
    }

    public override void Update(in FrameTime frame)
    {
        var now = frame.TotalSeconds;
        if (double.IsNaN(_phaseStart))
        {
            _phaseStart = now;
        }

        switch (_phase)
        {
            case Phase.Loading:
                WaitForLoad(now);
                break;
            case Phase.Settling when now - _phaseStart >= _options.SettleSeconds:
                var (width, height) = _host.FramebufferSize;
                LogMeasuring(_view, _views[_view].Name, width, height);
                Enter(Phase.Measuring, now);
                break;
            case Phase.Measuring when now - _phaseStart >= _options.MeasureSeconds:
                LogDone(_view, _views[_view].Name);
                NextView(now);
                break;
            case Phase.Flying when now - _phaseStart >= _options.FlightSeconds:
                LogFlightDone();
                Finish(now);
                break;
            default:
                break;
        }

        var job = new Place
        {
            Pose =
                _view < _views.Length
                    ? _views[_view].PoseIn(_origin)
                    : DebugFlyCameraSystem.CirclePose(
                        _origin,
                        FlightSpeed,
                        _phase == Phase.Flying ? now - _phaseStart : 0,
                        _circleHeight
                    ),
        };
        World.InlineQuery<Place, CameraPose>(in s_query, ref job);
    }

    private void WaitForLoad(double now)
    {
        var waited = now - _phaseStart;
        var ready = waited >= FirstCheckSeconds && _host.IsLoaded;
        if (!ready)
        {
            _readySince = double.NaN;
            if (waited < LoadTimeoutSeconds)
            {
                return;
            }

            // Measured anyway, and said once: no "loaded" line follows the timeout's.
            LogLoadTimeout(_view, ViewName, waited);
            Proceed(now);
            return;
        }

        if (double.IsNaN(_readySince))
        {
            _readySince = now;
            return;
        }

        if (now - _readySince < ReadySeconds)
        {
            return;
        }

        LogLoaded(_view, ViewName, waited);
        Proceed(now);
    }

    /// <summary>After a view or the flight's start has loaded (or timed out): settle on the view, or fly.</summary>
    private void Proceed(double now)
    {
        if (_view < _views.Length)
        {
            Enter(Phase.Settling, now);
            return;
        }

        LogFlightMeasuring();
        Enter(Phase.Flying, now);
    }

    private void NextView(double now)
    {
        if (_view + 1 < _views.Length)
        {
            _view++;
            Enter(Phase.Loading, now);
            return;
        }

        if (_options.FlightSeconds > 0)
        {
            // The flight starts where the circle does, once that has loaded.
            _view = _views.Length;
            Enter(Phase.Loading, now);
            return;
        }

        Finish(now);
    }

    private void Finish(double now)
    {
        LogFinished();
        Enter(Phase.Done, now);
        _host.RequestExit("benchmark finished");
    }

    private void Enter(Phase phase, double now)
    {
        _phase = phase;
        _phaseStart = now;
        _readySince = double.NaN;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Benchmark: {Views} views, then the circle for {FlightSeconds} s"
    )]
    private partial void LogStart(int views, double flightSeconds);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Benchmark view {Name} at ({X:F2}, {Y:F2}, {Z:F2}) m, yaw {Yaw:F3}, pitch {Pitch:F3}"
    )]
    private partial void LogView(string name, double x, double y, double z, float yaw, float pitch);

    [LoggerMessage(Level = LogLevel.Information, Message = "Benchmark view {Index} {Name} loaded after {Seconds:F1} s")]
    private partial void LogLoaded(int index, string name, double seconds);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Benchmark view {Index} {Name} still loading after {Seconds:F1} s; measuring anyway"
    )]
    private partial void LogLoadTimeout(int index, string name, double seconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Benchmark view {Index} {Name} measuring at {Width}x{Height}"
    )]
    private partial void LogMeasuring(int index, string name, int width, int height);

    [LoggerMessage(Level = LogLevel.Information, Message = "Benchmark view {Index} {Name} done")]
    private partial void LogDone(int index, string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Benchmark flight measuring")]
    private partial void LogFlightMeasuring();

    [LoggerMessage(Level = LogLevel.Information, Message = "Benchmark flight done")]
    private partial void LogFlightDone();

    [LoggerMessage(Level = LogLevel.Information, Message = "Benchmark finished")]
    private partial void LogFinished();

    private struct Place : IForEach<CameraPose>
    {
        public CameraPose Pose;

        public readonly void Update(ref CameraPose pose) => pose = Pose;
    }
}

/// <summary>What the benchmark needs from the engine: whether the view has loaded, the image size, and a way to exit.</summary>
public interface IBenchmarkHost
{
    /// <summary>True when every column around the camera is loaded and on the GPU.</summary>
    bool IsLoaded { get; }

    /// <summary>The size of the image drawn, in pixels.</summary>
    (int Width, int Height) FramebufferSize { get; }

    /// <summary>Asks the game to exit after this frame.</summary>
    void RequestExit(string reason);
}

/// <summary>One benchmark view: a name for the log and the camera pose.</summary>
/// <param name="Name">The view's name in the log.</param>
/// <param name="Position">The eye, an absolute point.</param>
/// <param name="Yaw">The yaw, in radians.</param>
/// <param name="Pitch">The pitch, in radians.</param>
public readonly record struct BenchmarkView(string Name, WorldPoint Position, float Yaw, float Pitch)
{
    /// <summary>The view as a camera pose local to <paramref name="origin"/>.</summary>
    public CameraPose PoseIn(WorldOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return new()
        {
            Position = origin.ToLocal(Position),
            Yaw = Yaw,
            Pitch = Pitch,
        };
    }
}

/// <summary>
/// The engine as the benchmark sees it. A view is loaded only once the loading screen has gone, fade and all, so its
/// pass never lands in the measurements. One per match.
/// </summary>
public sealed class EngineBenchmarkHost(
    VoxelWorld voxels,
    IWorldOnGpu uploader,
    WindowService window,
    LoadingScreen loading
) : IBenchmarkHost
{
    public bool IsLoaded => voxels.PendingColumns == 0 && uploader.IsUpToDate && loading.Phase is LoadingPhase.Done;

    public (int Width, int Height) FramebufferSize => (window.FramebufferSize.X, window.FramebufferSize.Y);

    public void RequestExit(string reason) => window.RequestExit(reason);
}
