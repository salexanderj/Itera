using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Arch.Core;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Engine.Input;
using Itera.Engine.Loading;
using Itera.Engine.Matches;
using Itera.Engine.Networking;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Time;
using Itera.Engine.UI;
using Itera.Engine.Windowing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Control;

/// <summary>
/// The control port (<c>Engine:Control</c>): a TCP port on 127.0.0.1 through which a tool (<c>tools/control/game.py</c>)
/// drives a running game, so a check needs no restart. Off unless <c>Engine:Control:Enabled</c> is true; any program on
/// this machine can then send the game commands, so it is a development tool.
/// <para>
/// Each line a tool sends is one request, a JSON object (<see cref="ControlRequest"/>), and each gets one reply line. A
/// thread accepts connections and one thread a connection reads requests, one at a time; each is queued for the main
/// thread, which wakes (<see cref="WindowService.Wake"/>) and handles it in <see cref="RunPending"/>, between fixed steps,
/// where console commands run. The ops:
/// </para>
/// <list type="bullet">
/// <item><c>run</c> (<c>line</c>, <c>script</c>): runs console commands, as typed; the reply holds their answers and the
/// log lines they wrote. Their answers also show in the in-game console. With <c>script</c> true the line runs as a
/// script (<see cref="DevConsole.RunScript"/>): <c>wait</c> and <c>screenshot</c> hold the commands after them, and the
/// reply comes once every command has run (or at the request's timeout, which stops the script).</item>
/// <item><c>log</c> (<c>from</c>, <c>level</c>, <c>tail</c>): log lines of Debug and above from line number <c>from</c>
/// on, or the last <c>tail</c> lines, and the number to read from next. Handled on the connection's thread.</item>
/// <item><c>wait</c> (<c>for</c>: <c>loaded</c>, <c>frames</c> with <c>n</c>, <c>ticks</c> (fixed steps) with
/// <c>n</c>, <c>seconds</c> with <c>n</c>, or <c>log</c> with <c>pattern</c>, a regular expression; <c>timeout</c>):
/// replies once that holds.</item>
/// <item><c>gpu</c> (<c>frames</c>, <c>timeout</c>): each GPU pass's average and longest time over the next frames.</item>
/// <item><c>status</c>: the frames built, the clock, the fixed steps and their pause and time scale, the loading
/// screen, the session role, the camera, the match's entity count, and the local player's position, velocity, and
/// contact.</item>
/// <item><c>query</c> (<c>entity</c>: <c>ID[:VERSION]</c> or <c>player</c>; or <c>component</c> with <c>most</c>;
/// or neither): one entity's components, the entities with a component and its value, or the archetypes with their
/// counts, as JSON (<see cref="EcsInspector"/>).</item>
/// <item><c>shot</c> (<c>path</c>, <c>scale</c>, <c>crop</c>, <c>ui</c>): a screenshot, replied to once its PNG is
/// written (<see cref="Screenshots"/>).</item>
/// <item><c>reload</c>: compiles the shaders again and remakes the pipelines (<see cref="ShaderReloads"/>), replied to
/// when done, or with the compiler's error, the old pipelines kept.</item>
/// <item><c>input</c> (<c>actions</c>): keys, buttons, look, and the pointer's capture, run in order over time
/// (<see cref="InputScript"/>), replied to once the last has run.</item>
/// </list>
/// Idle iterations allocate nothing.
/// </summary>
public sealed partial class ControlService : IDisposable
{
    /// <summary>Seconds a request waits by default before it fails.</summary>
    public const double DefaultTimeoutSeconds = 60;

    /// <summary>The longest request line read, in characters; a longer one ends its connection.</summary>
    public const int MostLineChars = 1 << 20;

    /// <summary>Most tools connected at once; another is told so and closed.</summary>
    public const int MostConnections = 16;

    /// <summary>The longest a wait-for-log pattern may take on one line, on the main thread.</summary>
    public static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>Longest wait a request may ask for, in seconds.</summary>
    public const double MostTimeoutSeconds = 600;

    /// <summary>Log lines a <c>log</c> request without <c>from</c> returns by default.</summary>
    public const int DefaultTail = 50;

    private readonly bool _enabled;
    private readonly int _port;
    private readonly DevConsole _console;
    private readonly ControlLog _log;
    private readonly LoadingScreen _loading;
    private readonly GpuTimings _gpu;
    private readonly RenderView _view;
    private readonly Screenshots _screenshots;
    private readonly ShaderReloads _shaderReloads;
    private readonly InputService _input;
    private readonly Func<SessionRole> _role;
    private readonly Func<bool> _playing;
    private readonly Func<World?> _world;
    private readonly FixedStepAccumulator? _time;
    private readonly Action _wake;
    private readonly ILogger<ControlService> _logger;
    private readonly ConcurrentQueue<ControlRequest> _inbox = new();
    private readonly List<ControlRequest> _waiting = [];
    private readonly List<Connection> _connections = [];
    private TcpListener? _listener;
    private Thread? _acceptThread;
    private volatile bool _stopping;
    private bool _disposed;

    public ControlService(
        IOptions<EngineOptions> options,
        DevConsole console,
        ControlLog log,
        LoadingScreen loading,
        GpuTimings gpu,
        RenderView view,
        Screenshots screenshots,
        ShaderReloads shaderReloads,
        InputService input,
        MatchControl matches,
        WindowService window,
        FixedStepAccumulator time,
        ILogger<ControlService> logger
    )
        : this(
            options.Value.Control.Enabled,
            options.Value.Control.Port,
            console,
            log,
            loading,
            gpu,
            view,
            screenshots,
            shaderReloads,
            input,
            () => matches.Role,
            window.Wake,
            logger,
            () => matches.Phase == MatchPhase.Playing,
            () => matches.Current?.Ecs.World,
            time
        ) { }

    /// <summary>For tests: a <paramref name="port"/> of 0 lets the system pick one, which <see cref="Port"/> then names.</summary>
    internal ControlService(
        bool enabled,
        int port,
        DevConsole console,
        ControlLog log,
        LoadingScreen loading,
        GpuTimings gpu,
        RenderView view,
        Screenshots screenshots,
        ShaderReloads shaderReloads,
        InputService input,
        Func<SessionRole> role,
        Action wake,
        ILogger<ControlService> logger,
        Func<bool>? playing = null,
        Func<World?>? world = null,
        FixedStepAccumulator? time = null
    )
    {
        _playing = playing ?? (() => true);
        _world = world ?? (() => null);
        _time = time;
        _screenshots = screenshots;
        _shaderReloads = shaderReloads;
        _input = input;
        _enabled = enabled;
        _port = port;
        _console = console;
        _log = log;
        _loading = loading;
        _gpu = gpu;
        _view = view;
        _role = role;
        _wake = wake;
        _logger = logger;
    }

    /// <summary>True while the port is open.</summary>
    public bool Listening => _listener is not null;

    /// <summary>Requests waiting for their condition. Main thread; for tests.</summary>
    internal int WaitingCount => _waiting.Count;

    /// <summary>The port it listens on, or 0 when it does not.</summary>
    public int Port { get; private set; }

    /// <summary>
    /// Opens the port when enabled. A port another program holds is logged as a Warning, and the game goes on without it.
    /// </summary>
    public void Initialize()
    {
        if (!_enabled)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Loopback, _port);
        try
        {
            listener.Start();
        }
        catch (SocketException exception)
        {
            LogPortUnavailable(_port, exception.Message);
            listener.Dispose();
            return;
        }

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptThread = new Thread(Accept) { Name = "Control", IsBackground = true };
        _acceptThread.Start();
        LogListening(Port);
    }

    /// <summary>
    /// Starts the requests that arrived and completes the waiting ones whose condition holds. Main loop, between fixed
    /// steps, with the clock in seconds and the frames built so far.
    /// </summary>
    public void RunPending(double now, ulong frames)
    {
        if (_inbox.IsEmpty && _waiting.Count == 0)
        {
            return;
        }

        while (_inbox.TryDequeue(out var request))
        {
            try
            {
                Start(request, now, frames);
            }
            catch (Exception exception)
            {
                // A request's fields of an unexpected kind fail it, not the game.
                request.Complete(_log, "failed: " + exception.Message);
            }

            if (!request.IsCompleted)
            {
                _waiting.Add(request);
            }
        }

        for (var i = _waiting.Count - 1; i >= 0; i--)
        {
            var request = _waiting[i];
            if (request.IsCompleted || request.Check is not { } check || Checked(request, check, now, frames))
            {
                _waiting.RemoveAt(i);
            }
            else if (now - request.StartSeconds > request.TimeoutSeconds)
            {
                Abandon(request);
                request.Complete(
                    _log,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"timed out after {request.TimeoutSeconds:0.#} s waiting ({request.Op})"
                    )
                );
                _waiting.RemoveAt(i);
            }
        }
    }

    /// <summary>A waiting request's check; one that throws fails the request, not the game.</summary>
    private bool Checked(
        ControlRequest request,
        Func<ControlRequest, double, ulong, bool> check,
        double now,
        ulong frames
    )
    {
        try
        {
            return check(request, now, frames);
        }
        catch (Exception exception)
        {
            Abandon(request);
            request.Complete(_log, "failed: " + exception.Message);
            return true;
        }
    }

    /// <summary>
    /// Takes back what a waiting request asked for (a screenshot, a script's hold) as it fails or the game shuts down; a
    /// throw is logged, never left to end the loop.
    /// </summary>
    private void Abandon(ControlRequest request)
    {
        try
        {
            request.Abandon?.Invoke();
        }
        catch (Exception exception)
        {
            LogAbandonFailed(exception, request.Op);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _listener?.Stop();
        while (_inbox.TryDequeue(out var request))
        {
            request.Complete(_log, "the game is shutting down");
        }

        foreach (var request in _waiting)
        {
            Abandon(request);
            request.Complete(_log, "the game is shutting down");
        }

        _waiting.Clear();
        Connection[] connections;
        lock (_connections)
        {
            connections = [.. _connections];
        }

        // Each connection writes the reply it holds (a quit's, among them), then sees its end of reading and stops.
        foreach (var connection in connections)
        {
            connection.Stop();
        }

        _acceptThread?.Join(TimeSpan.FromSeconds(1));
        _listener?.Dispose();
        _listener = null;
        Port = 0;
    }

    /// <summary>Queues a request for the main thread and wakes it. A connection's thread.</summary>
    private void Enqueue(ControlRequest request)
    {
        if (_stopping)
        {
            request.Complete(_log, "the game is shutting down");
            return;
        }

        _inbox.Enqueue(request);
        _wake();
        // Dispose may have emptied the queue just before this request went in.
        if (_stopping && _inbox.TryDequeue(out var late))
        {
            late.Complete(_log, "the game is shutting down");
        }
    }

    private void Start(ControlRequest request, double now, ulong frames)
    {
        request.LogFrom = _log.Next;
        request.StartSeconds = now;
        request.StartFrame = frames;
        // The console ran just before, with the iteration's steps.
        request.StartSteps = _console.Now.Steps;
        request.TimeoutSeconds = Math.Clamp(request.Number("timeout", DefaultTimeoutSeconds), 0, MostTimeoutSeconds);
        switch (request.Op)
        {
            case "run":
                Run(request);
                break;
            case "wait":
                StartWait(request);
                break;
            case "gpu":
                StartGpu(request);
                break;
            case "status":
                Status(request, now, frames);
                break;
            case "query":
                Query(request);
                break;
            case "shot":
                StartShot(request);
                break;
            case "reload":
                StartReload(request);
                break;
            case "input":
                StartInput(request, now);
                break;
            default:
                request.Complete(
                    _log,
                    $"no op '{request.Op}'; ops: run, log, wait, gpu, status, query, shot, reload, input"
                );
                break;
        }
    }

    private void Run(ControlRequest request)
    {
        if (request.String("line") is not { } line)
        {
            request.Complete(_log, "run needs \"line\", a string");
            return;
        }

        if (!request.Flag("script", false))
        {
            _console.RunNow(line, request);
            request.Complete(_log);
            return;
        }

        // As a script: its holds (wait, screenshot) hold the commands after them, and the reply comes once all have run.
        var script = _console.RunScript(line, request);
        if (script.Done)
        {
            request.Complete(_log);
            return;
        }

        request.Check = (r, _, _) =>
        {
            if (!script.Done)
            {
                return false;
            }

            r.Complete(_log);
            return true;
        };
        request.Abandon = script.Stop;
    }

    private void StartWait(ControlRequest request)
    {
        var what = request.String("for", "");
        var n = request.Number("n", 1);
        switch (what)
        {
            case "loaded":
                // A match is played and its loading screen has gone (not the menu, nor a match still joining).
                request.Check = (r, _, _) => CompleteWhen(r, _playing() && _loading.Phase == LoadingPhase.Done);
                break;
            case "frames":
                var frames = (ulong)Math.Max(0, n);
                request.Check = (r, _, built) => CompleteWhen(r, built - r.StartFrame >= frames);
                break;
            case "seconds":
                request.Check = (r, now, _) => CompleteWhen(r, now - r.StartSeconds >= n);
                break;
            case "ticks":
                var ticks = (ulong)Math.Max(0, Math.Ceiling(n));
                request.Check = (r, _, _) => CompleteWhen(r, _console.Now.Steps - r.StartSteps >= ticks);
                break;
            case "log":
                StartLogWait(request);
                return;
            default:
                request.Complete(_log, "wait needs \"for\": loaded, frames, ticks, seconds, or log");
                return;
        }

        request.Check(request, request.StartSeconds, request.StartFrame);
    }

    private void StartLogWait(ControlRequest request)
    {
        Regex pattern;
        try
        {
            pattern = new Regex(request.String("pattern", "") ?? "", RegexOptions.CultureInvariant, PatternTimeout);
        }
        catch (ArgumentException exception)
        {
            request.Complete(_log, "wait for log needs \"pattern\", a regular expression: " + exception.Message);
            return;
        }

        // From the line given, or from the request's start; each check reads only the lines since the last.
        var from = request.Whole("from") ?? request.LogFrom;
        var lines = new List<ControlLogLine>();
        request.Check = (r, _, _) =>
        {
            lines.Clear();
            from = _log.Read(from, LogLevel.Trace, lines);
            foreach (var line in lines)
            {
                bool matched;
                try
                {
                    matched = pattern.IsMatch(line.Text);
                }
                catch (RegexMatchTimeoutException)
                {
                    r.Complete(_log, "wait for log: the pattern took too long on a line");
                    return true;
                }

                if (matched)
                {
                    r.Complete(_log, fields: writer => writer.WriteString("matched", line.Text));
                    return true;
                }
            }

            return false;
        };
    }

    private bool CompleteWhen(ControlRequest request, bool done)
    {
        if (done)
        {
            request.Complete(_log);
        }

        return done;
    }

    private void StartGpu(ControlRequest request)
    {
        var frames = (long)Math.Clamp(request.Number("frames", 120), 1, 100_000);
        if (!_gpu.Enabled)
        {
            request.Complete(_log, "this device measures no GPU times");
            return;
        }

        _gpu.StartWindow();
        request.Check = (r, now, built) =>
        {
            if (_gpu.Window(GpuPass.Frame).Count < frames)
            {
                return false;
            }

            var seconds = now - r.StartSeconds;
            var framesBuilt = built - r.StartFrame;
            r.Complete(_log, fields: writer => WriteGpu(writer, seconds, framesBuilt));
            return true;
        };
    }

    private void WriteGpu(Utf8JsonWriter writer, double seconds, ulong frames)
    {
        writer.WriteNumber("seconds", Math.Round(seconds, 3));
        writer.WriteNumber("frames", frames);
        writer.WriteNumber("fps", seconds > 0 ? Math.Round(frames / seconds, 1) : 0);
        writer.WriteStartObject("passes");
        for (var i = 0; i < GpuTimings.PassCount; i++)
        {
            var pass = (GpuPass)i;
            var times = _gpu.Window(pass);
            if (times.Count == 0)
            {
                continue;
            }

            writer.WriteStartObject(pass.ToString());
            writer.WriteNumber("count", times.Count);
            writer.WriteNumber("avg_ms", Math.Round(times.AverageMs, 4));
            writer.WriteNumber("max_ms", Math.Round(times.MaxMs, 4));
            writer.WriteEndObject();
        }

        // The game's passes by name, those that ran.
        for (var i = 0; i < _gpu.GamePassNames.Count; i++)
        {
            var times = _gpu.GamePass(i);
            if (times.Count == 0)
            {
                continue;
            }

            writer.WriteStartObject("Game " + _gpu.GamePassNames[i]);
            writer.WriteNumber("count", times.Count);
            writer.WriteNumber("avg_ms", Math.Round(times.AverageMs, 4));
            writer.WriteNumber("max_ms", Math.Round(times.MaxMs, 4));
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// A screenshot (<c>path</c>, default a new file in the data folder's Screenshots/; <c>scale</c>, default 0.5;
    /// <c>crop</c>, [x, y, width, height] in screen pixels; <c>ui</c>, default true), replied to once the file is written.
    /// </summary>
    private void StartShot(ControlRequest request)
    {
        (int, int, int, int)? crop = null;
        if (request.Arguments.TryGetProperty("crop", out var cropValue) && cropValue.ValueKind != JsonValueKind.Null)
        {
            if (
                cropValue.ValueKind != JsonValueKind.Array
                || cropValue.GetArrayLength() != 4
                || cropValue.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out _))
            )
            {
                request.Complete(_log, "shot's \"crop\" is [x, y, width, height] in screen pixels");
                return;
            }

            var values = cropValue.EnumerateArray().Select(v => v.GetInt32()).ToArray();
            crop = (values[0], values[1], values[2], values[3]);
        }

        var path = request.String("path") ?? _screenshots.NewPath();
        var shot = new ScreenshotRequest(
            Path.GetFullPath(path),
            (float)request.Number("scale", 0.5),
            crop,
            request.Flag("ui", true)
        );
        if (_screenshots.Request(shot) is { } refusal)
        {
            request.Complete(_log, refusal);
            return;
        }

        // Timed out before a frame took it (frames stopped): taken back, so the next shot is not refused for it.
        request.Abandon = () => _screenshots.Withdraw(shot);
        request.Check = (r, _, _) =>
        {
            if (!shot.IsDone)
            {
                return false;
            }

            r.Complete(
                _log,
                shot.Error,
                shot.Error is null
                    ? writer =>
                    {
                        writer.WriteString("path", shot.Path);
                        writer.WriteNumber("width", shot.Width);
                        writer.WriteNumber("height", shot.Height);
                        writer.WriteNumber("screen_width", shot.ScreenSize.Width);
                        writer.WriteNumber("screen_height", shot.ScreenSize.Height);
                    }
                    : null
            );
            return true;
        };
    }

    /// <summary>A reload of the shaders, replied to once the pipelines are made again or the compiler's error.</summary>
    private void StartReload(ControlRequest request)
    {
        var reload = new ShaderReloadRequest();
        if (_shaderReloads.Request(reload) is { } refusal)
        {
            request.Complete(_log, refusal);
            return;
        }

        request.Check = (r, _, _) =>
        {
            if (!reload.IsDone)
            {
                return false;
            }

            r.Complete(
                _log,
                reload.Error,
                writer =>
                {
                    writer.WriteString("folder", _shaderReloads.SourceDirectory);
                    writer.WriteString("game_folder", _shaderReloads.GameSourceDirectory);
                    writer.WriteNumber("pipelines", reload.Pipelines);
                    writer.WriteNumber("ms", Math.Round(reload.Milliseconds, 1));
                }
            );
            return true;
        };
    }

    /// <summary>A list of input actions (<c>actions</c>, <see cref="InputScript"/>), replied to once the last has run.</summary>
    private void StartInput(ControlRequest request, double now)
    {
        if (
            !request.Arguments.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array
            || actions.EnumerateArray().Any(action => action.ValueKind != JsonValueKind.String)
        )
        {
            request.Complete(_log, "input needs \"actions\", a list of strings");
            return;
        }

        var lines = actions.EnumerateArray().Select(action => action.GetString()!).ToList();
        if (InputScript.Parse(_input, lines, out var error) is not { } script)
        {
            request.Complete(_log, error);
            return;
        }

        request.Abandon = script.Release;
        request.Check = (r, time, _) => CompleteWhen(r, script.Step(time));
        request.Check(request, now, request.StartFrame);
    }

    private void Status(ControlRequest request, double now, ulong frames)
    {
        var camera = _view.Camera;
        var role = _role();
        var loading = _loading.Phase;
        request.Complete(
            _log,
            fields: writer =>
            {
                writer.WriteNumber("frames", frames);
                writer.WriteNumber("seconds", Math.Round(now, 3));
                writer.WriteString("loading", loading.ToString());
                writer.WriteString("role", role.ToString());
                writer.WriteBoolean("playing", _playing());
                writer.WriteNumber("steps", _console.Now.Steps);
                if (_time is { } time)
                {
                    writer.WriteNumber("tick", time.Tick);
                    writer.WriteBoolean("paused", time.Paused);
                    writer.WriteNumber("time_scale", time.TimeScale);
                }

                writer.WriteNumber("render_scale", _view.RenderScale);
                writer.WriteStartArray("camera_position");
                writer.WriteNumberValue(Math.Round(camera.Position.X, 3));
                writer.WriteNumberValue(Math.Round(camera.Position.Y, 3));
                writer.WriteNumberValue(Math.Round(camera.Position.Z, 3));
                writer.WriteEndArray();
                var forward = camera.Forward;
                writer.WriteStartArray("camera_forward");
                writer.WriteNumberValue(Math.Round(forward.X, 4));
                writer.WriteNumberValue(Math.Round(forward.Y, 4));
                writer.WriteNumberValue(Math.Round(forward.Z, 4));
                writer.WriteEndArray();
                if (_world() is { } world)
                {
                    writer.WriteNumber("entities", world.Size);
                    WritePlayer(writer, world);
                }
            }
        );
    }

    /// <summary>The local player's position, velocity, and contact, when the match has one.</summary>
    private static void WritePlayer(Utf8JsonWriter writer, World world)
    {
        if (!EcsInspector.TryFindEntityOrPlayer(world, "player", out var player))
        {
            return;
        }

        writer.WriteStartObject("player");
        writer.WriteString("entity", EcsInspector.Name(player));
        foreach (var (name, value) in EcsInspector.Components(world, player))
        {
            if (name is "CharacterPosition" or "CharacterVelocity" or "CharacterContact" or "PlayerMode")
            {
                writer.WritePropertyName(name);
                EcsInspector.WriteJson(writer, value);
            }
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The <c>query</c> op: an entity's components (<c>entity</c>), the entities with a component and its value
    /// (<c>component</c>, at most <c>most</c>), or the archetypes and their counts.
    /// </summary>
    private void Query(ControlRequest request)
    {
        if (_world() is not { } world)
        {
            request.Complete(_log, "query: no match is played");
            return;
        }

        if (request.String("entity") is { } text)
        {
            if (!EcsInspector.TryFindEntityOrPlayer(world, text, out var entity))
            {
                request.Complete(_log, $"query: no living entity {text}");
                return;
            }

            var components = EcsInspector.Components(world, entity);
            request.Complete(
                _log,
                fields: writer =>
                {
                    writer.WriteString("entity", EcsInspector.Name(entity));
                    writer.WriteStartObject("components");
                    foreach (var (name, value) in components)
                    {
                        writer.WritePropertyName(name);
                        EcsInspector.WriteJson(writer, value);
                    }

                    writer.WriteEndObject();
                }
            );
            return;
        }

        if (request.String("component") is { } component)
        {
            if (!EcsInspector.TryFindComponent(world, component, out var type))
            {
                request.Complete(_log, $"query: no entity has a component named {component}");
                return;
            }

            var most = (int)Math.Clamp(request.Number("most", 100), 1, EcsInspector.MostListed);
            var entities = EcsInspector.EntitiesWith(world, type, int.MaxValue);
            request.Complete(
                _log,
                fields: writer =>
                {
                    writer.WriteString("component", type.Type.Name);
                    writer.WriteNumber("count", entities.Count);
                    writer.WriteStartArray("entities");
                    for (var i = 0; i < entities.Count && i < most; i++)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("entity", EcsInspector.Name(entities[i]));
                        writer.WritePropertyName("value");
                        EcsInspector.WriteJson(writer, EcsInspector.Component(world, entities[i], type));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
            );
            return;
        }

        var archetypes = EcsInspector.Archetypes(world);
        request.Complete(
            _log,
            fields: writer =>
            {
                writer.WriteNumber("entities", world.Size);
                writer.WriteStartArray("archetypes");
                foreach (var (count, components) in archetypes)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("count", count);
                    writer.WriteString("components", components);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }
        );
    }

    /// <summary>The <c>log</c> op, answered on the connection's thread: nothing in it touches the game.</summary>
    private string Log(ControlRequest request)
    {
        var level = request.String("level", "debug") ?? "debug";
        if (!TryParseLevel(level, out var least))
        {
            request.Complete(_log, "log's \"level\" is trace, debug, information, warning, error, or critical");
            return request.WaitForReply();
        }

        var lines = new List<ControlLogLine>();
        long next;
        if (request.Whole("from") is { } from)
        {
            next = _log.Read(from, least, lines);
        }
        else
        {
            next = _log.Read(0, least, lines);
            var tail = (int)Math.Clamp(request.Number("tail", DefaultTail), 0, ControlLog.Capacity);
            if (lines.Count > tail)
            {
                lines.RemoveRange(0, lines.Count - tail);
            }
        }

        // The reply's own log is empty: the lines asked for are its "lines".
        request.LogFrom = long.MaxValue;
        request.Complete(
            _log,
            fields: writer =>
            {
                writer.WriteNumber("next", next);
                ControlRequest.WriteLog(writer, "lines", lines);
            }
        );
        return request.WaitForReply();
    }

    private static bool TryParseLevel(string text, out LogLevel level)
    {
        switch (text.ToUpperInvariant())
        {
            case "TRACE":
            case "VERBOSE":
                level = LogLevel.Trace;
                return true;
            case "DEBUG":
                level = LogLevel.Debug;
                return true;
            case "INFORMATION":
            case "INFO":
                level = LogLevel.Information;
                return true;
            case "WARNING":
            case "WARN":
                level = LogLevel.Warning;
                return true;
            case "ERROR":
                level = LogLevel.Error;
                return true;
            case "CRITICAL":
            case "FATAL":
                level = LogLevel.Critical;
                return true;
            default:
                level = LogLevel.None;
                return false;
        }
    }

    private void Accept()
    {
        var listener = _listener!;
        while (!_stopping)
        {
            TcpClient client;
            try
            {
                client = listener.AcceptTcpClient();
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // Stopped.
                return;
            }

            var connection = new Connection(this, client);
            bool full;
            lock (_connections)
            {
                if (_stopping)
                {
                    client.Dispose();
                    return;
                }

                full = _connections.Count >= MostConnections;
                if (!full)
                {
                    _connections.Add(connection);
                }
            }

            if (full)
            {
                // Told why, then closed.
                Refuse(client);
                continue;
            }

            connection.Start();
        }
    }

    /// <summary>Tells a tool past <see cref="MostConnections"/> why, and closes it.</summary>
    private static void Refuse(TcpClient client)
    {
        try
        {
            var reply = Encoding.UTF8.GetBytes(
                ControlRequest.ErrorReply($"too many connections: at most {MostConnections}") + "\n"
            );
            client.GetStream().Write(reply);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            // Gone already.
        }
        finally
        {
            client.Dispose();
        }
    }

    private void Remove(Connection connection)
    {
        lock (_connections)
        {
            _connections.Remove(connection);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Control: listening on 127.0.0.1:{Port}")]
    private partial void LogListening(int port);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Control: port {Port} is not available ({Reason}); the game goes on without the control port"
    )]
    private partial void LogPortUnavailable(int port, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Control: taking back a {Op} request failed")]
    private partial void LogAbandonFailed(Exception exception, string op);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Control: a connection ended ({Reason})")]
    private partial void LogConnectionEnded(string reason);

    /// <summary>One tool's connection, on its own thread: a request line in, its reply line out, in turn.</summary>
    private sealed class Connection(ControlService service, TcpClient client)
    {
        private Thread? _running;

        /// <summary>
        /// The next line, without its end, read into <paramref name="buffer"/>; past <see cref="MostLineChars"/> it stops
        /// reading and returns what it has, longer than that. Null at the end of the stream.
        /// </summary>
        internal static string? ReadLine(TextReader reader, StringBuilder buffer)
        {
            buffer.Clear();
            while (true)
            {
                var next = reader.Read();
                if (next < 0)
                {
                    return buffer.Length > 0 ? buffer.ToString() : null;
                }

                if (next == '\n')
                {
                    if (buffer.Length > 0 && buffer[^1] == '\r')
                    {
                        buffer.Length--;
                    }

                    return buffer.ToString();
                }

                buffer.Append((char)next);
                if (buffer.Length > MostLineChars)
                {
                    return buffer.ToString();
                }
            }
        }

        /// <summary>The reply to one request line; a request that throws is answered with the error.</summary>
        private string Answer(string line)
        {
            try
            {
                var request = ControlRequest.Parse(line, out var error);
                if (request is null)
                {
                    return ControlRequest.ErrorReply(error!);
                }

                if (request.Op == "log")
                {
                    return service.Log(request);
                }

                service.Enqueue(request);
                return request.WaitForReply();
            }
            catch (Exception exception)
                when (exception is not (IOException or SocketException or ObjectDisposedException))
            {
                return ControlRequest.ErrorReply("failed: " + exception.Message);
            }
        }

        public void Start()
        {
            _running = new Thread(Serve) { Name = "Control connection", IsBackground = true };
            _running.Start();
        }

        /// <summary>Stops reading, lets the reply being written go out, then closes. Any thread.</summary>
        public void Stop()
        {
            try
            {
                // Null once the connection's thread has closed it.
                client.Client?.Shutdown(SocketShutdown.Receive);
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
            {
                // Already closed.
            }

            if (_running is { } running && !running.Join(TimeSpan.FromSeconds(1)))
            {
                client.Dispose();
            }
        }

        private void Serve()
        {
            var reason = "closed by the tool";
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                using var writer = new StreamWriter(stream, new UTF8Encoding(false))
                {
                    NewLine = "\n",
                    AutoFlush = true,
                };
                var buffer = new StringBuilder();
                while (ReadLine(reader, buffer) is { } line)
                {
                    if (line.Length > MostLineChars)
                    {
                        writer.WriteLine(ControlRequest.ErrorReply($"a line longer than {MostLineChars} characters"));
                        reason = "a line too long";
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    writer.WriteLine(Answer(line));
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
                reason = exception.Message;
            }
            finally
            {
                client.Dispose();
                service.Remove(this);
                service.LogConnectionEnded(reason);
            }
        }
    }
}
