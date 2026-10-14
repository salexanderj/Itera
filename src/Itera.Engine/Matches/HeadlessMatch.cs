using System.Diagnostics;
using Arch.Core;
using Itera.Engine.Assets;
using Itera.Engine.Diagnostics;
using Itera.Engine.Input;
using Itera.Engine.Rendering;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Matches;

/// <summary>
/// The run's side of matches played without a window or GPU, for tests and tools: from a game's composed services
/// (<c>AddIteraEngine</c> and the game's own), it makes the game's run-wide registries once (<see cref="IRunStartup"/>),
/// as the run's game step does, reads the mesh assets they declared without their textures, as the run's mesh assets
/// step does without graphics, and plays matches: offline (<see cref="Play"/>), or hosting (<see cref="Host"/>) and
/// joining (<see cref="Join"/>) over the run's transports, which a test makes in-memory (an
/// <see cref="Networking.ISessionTransports"/> of its own), so a host and a client run in one process, each from its
/// own services. Nothing is drawn, heard, or loaded from mods. Main thread.
/// </summary>
public sealed class HeadlessRun
{
    private readonly IServiceProvider _services;

    /// <param name="services">The game's services; every <see cref="IRunStartup"/> of them starts now, once.</param>
    public HeadlessRun(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        foreach (var startup in services.GetServices<IRunStartup>())
        {
            startup.Start();
        }

        services.GetRequiredService<MeshAssets>().LoadAll(textures: false);
    }

    /// <summary>
    /// Starts an offline match of <paramref name="seed"/>: its session and world steps run now. With
    /// <paramref name="saveName"/> it starts from that named save when it exists, and writes it.
    /// </summary>
    public HeadlessMatch Play(ulong seed, string? saveName = null) =>
        new(_services, new MatchStart(SessionMode.Offline, SessionTransportKind.Direct, null, seed, saveName, false));

    /// <summary>
    /// Hosts a match of <paramref name="seed"/> over the run's direct transport: its session and world steps run now,
    /// and clients join it while it polls (<see cref="HeadlessMatch.Step"/>, or a <see cref="Join"/>). With
    /// <paramref name="saveName"/> it starts from that named save when it exists, and writes it.
    /// </summary>
    public HeadlessMatch Host(ulong seed, string? saveName = null) =>
        new(_services, new MatchStart(SessionMode.Host, SessionTransportKind.Direct, null, seed, saveName, false));

    /// <summary>
    /// Joins <paramref name="host"/>'s match at <paramref name="address"/> over the run's direct transport: polls the
    /// host's session and this one's in turn until the host's world has been downloaded (at most
    /// <paramref name="mostSeconds"/>), then starts the world from it. Throws when the join fails or takes longer.
    /// </summary>
    public HeadlessMatch Join(HeadlessMatch host, string address = "127.0.0.1:47600", double mostSeconds = 60)
    {
        ArgumentNullException.ThrowIfNull(host);
        return new(
            _services,
            new MatchStart(SessionMode.Join, SessionTransportKind.Direct, address, 0, null, false),
            host,
            mostSeconds
        );
    }
}

/// <summary>
/// One match played without a window or GPU (<see cref="HeadlessRun.Play"/>, <see cref="HeadlessRun.Host"/>,
/// <see cref="HeadlessRun.Join"/>), through the same <see cref="MatchFlow"/> the main loop uses: its world starts at once
/// (a client's once the host's is downloaded), and it moves only when told: <see cref="Step"/> runs fixed steps as the
/// main loop does (the session's poll, the world's streaming, the step's input, then
/// <see cref="MatchServices.RunFixedStep"/>), each followed by a frame of the frame group (<see cref="Frame"/>), which
/// writes only the renderer's CPU-side objects. A host and a client in one process step in turn, each polling its own
/// session first. Input comes from <see cref="Input"/>'s injections (keys, buttons, look); the game's input is not
/// blocked. Read the match through <see cref="World"/> and <see cref="Services"/>. Disposing it ends the match. Main
/// thread.
/// </summary>
public sealed class HeadlessMatch : IDisposable
{
    private readonly MatchFlow _flow;
    private readonly MatchControl _matches;
    private readonly DebugLines _debugLines;
    private readonly ModelInstances _models;
    private readonly MeshInstances _meshes;
    private readonly PointLights _lights;
    private readonly RenderViews _views;
    private readonly Particles.ParticleEmitter _particles;
    private readonly int _editColumns;
    private readonly float _stepSeconds;
    private ulong _frame;
    private bool _disposed;

    internal HeadlessMatch(
        IServiceProvider services,
        MatchStart start,
        HeadlessMatch? host = null,
        double mostSeconds = 60
    )
    {
        var options = services.GetRequiredService<IOptions<EngineOptions>>().Value;
        _matches = services.GetRequiredService<MatchControl>();
        _debugLines = services.GetRequiredService<DebugLines>();
        _models = services.GetRequiredService<ModelInstances>();
        _meshes = services.GetRequiredService<MeshInstances>();
        _lights = services.GetRequiredService<PointLights>();
        _views = services.GetRequiredService<RenderViews>();
        _particles = services.GetRequiredService<Particles.ParticleEmitter>();
        Input = services.GetRequiredService<InputService>();
        _editColumns = options.Voxels.EditColumnsPerStep;
        _stepSeconds = 1f / options.FixedStepHz;
        _flow = new MatchFlow(
            services.GetRequiredService<IServiceScopeFactory>(),
            _matches,
            services.GetRequiredService<SystemTimes>(),
            new MatchFlowHooks(new MatchGpu(_ => { }, () => { }), _ => { }, () => { }),
            () => Seconds,
            _ => { },
            services.GetRequiredService<ILogger<HeadlessMatch>>()
        );
        _flow.Start(start);
        if (host is not null)
        {
            JoinWith(host, mostSeconds);
        }

        // No frame says the world is starting: it starts at once.
        _flow.StartWorldWhenShown(ulong.MaxValue);
        Match =
            _flow.Playing
            ?? throw new InvalidOperationException("The headless match's world did not start: " + _matches.LastEnd);
        Input.SetGameInputBlocked(false);
    }

    /// <summary>
    /// A joining match's join: the host's session and this one's are polled in turn, as two main loops would, until the
    /// download has ended (the match is starting) or failed, or <paramref name="mostSeconds"/> have gone.
    /// </summary>
    private void JoinWith(HeadlessMatch host, double mostSeconds)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(mostSeconds * Stopwatch.Frequency);
        while (_matches.Phase == MatchPhase.Joining)
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                _flow.End(clean: false, "The headless join took too long.");
                break;
            }

            host.PollSession();
            PollSession();
        }

        if (_matches.Phase != MatchPhase.Starting)
        {
            var reason = _matches.LastEnd;
            _flow.End(clean: false);
            throw new InvalidOperationException("The headless join did not finish: " + reason);
        }
    }

    /// <summary>Takes what arrived for the match's session, as the main loop does once a frame before its steps.</summary>
    public void PollSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _flow.PollSession(_frame);
    }

    /// <summary>True once the match's session has ended: on a client, the host left or the connection was lost.</summary>
    public bool SessionEnded => _flow.SessionEnded;

    /// <summary>The match's engine services.</summary>
    public MatchServices Match { get; }

    /// <summary>The match's ECS world.</summary>
    public World World => Match.Ecs.World;

    /// <summary>The match's scope, for the game's services.</summary>
    public IServiceProvider Services => Match.Services;

    /// <summary>The run's input: inject keys, buttons, and looks here before <see cref="Step"/>.</summary>
    public InputService Input { get; }

    /// <summary>Fixed steps run so far.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Seconds of game time so far: the steps run times a step's length.</summary>
    public double Seconds => Tick * (double)_stepSeconds;

    /// <summary>
    /// Runs two steps, which place the streaming centres (a game's streaming system, or <c>SetCenters</c>), then waits
    /// until every column they want is resident (the view loaded, as far as no GPU is concerned): the worker threads
    /// generate them in real time, so it waits on the clock, at most <paramref name="mostSeconds"/>, taking finished
    /// columns as the main loop does, with no more steps. True once loaded; the match then counts as playing.
    /// </summary>
    public bool WaitUntilLoaded(double mostSeconds = 60)
    {
        Step(2);
        var deadline = Stopwatch.GetTimestamp() + (long)(mostSeconds * Stopwatch.Frequency);
        while (Match.Terrain is { } terrain && !terrain.Voxels.WantedColumnsResident)
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                return false;
            }

            terrain.Update(Seconds);
            Thread.Sleep(1);
        }

        _matches.Phase = MatchPhase.Playing;
        return true;
    }

    /// <summary>
    /// Runs <paramref name="ticks"/> fixed steps, each after a poll of the session (nothing offline) and followed by a
    /// frame.
    /// </summary>
    public void Step(int ticks)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var i = 0; i < ticks; i++)
        {
            _flow.PollSession(_frame);
            Match.Terrain?.Update(Seconds);
            Input.BeginTick();
            Match.RunFixedStep(new FixedStepTime(_stepSeconds, Tick), _editColumns);
            Tick++;
            Frame();
        }
    }

    /// <summary>
    /// Runs the frame group once, as the main loop's frame does before it draws: the frame's input, then the systems,
    /// which hand the camera, the models, the lights, the lines, and the image views they ask for to the renderer's
    /// CPU-side objects (the last frame's forgotten first, and the particles queued since it dropped,
    /// <see cref="Particles.ParticleEmitter.Discard"/>); nothing is drawn.
    /// Models retired before it go after it, as an upload would remove them.
    /// </summary>
    public void Frame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Input.BeginFrame();
        _debugLines.Clear();
        _models.Clear();
        _meshes.Clear();
        _lights.BeginFrame(Seconds);
        _views.BeginFrame();
        _particles.Discard();
        // Just before the next step: the frame shows the last step's state.
        var frame = new FrameTime(_stepSeconds, Seconds, _frame++, 0.999999f);
        Match.Ecs.Update(in frame);
        // No uploader waits to draw a retired model until the world's meshes hold its voxels.
        Match.Models.RemoveRetired();
    }

    /// <summary>Ends the match, as leaving it does.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flow.End(clean: true);
    }
}
