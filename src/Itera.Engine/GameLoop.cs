using System.Diagnostics;
using System.Runtime.InteropServices;
using Itera.Engine.Assets;
using Itera.Engine.Audio;
using Itera.Engine.Commands;
using Itera.Engine.Control;
using Itera.Engine.Diagnostics;
using Itera.Engine.Input;
using Itera.Engine.Loading;
using Itera.Engine.Matches;
using Itera.Engine.Modding;
using Itera.Engine.Networking;
using Itera.Engine.Particles;
using Itera.Engine.Rendering;
using Itera.Engine.Rendering.GpuData;
using Itera.Engine.Rendering.Memory;
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Rendering.Shaders;
using Itera.Engine.Settings;
using Itera.Engine.Time;
using Itera.Engine.UI;
using Itera.Engine.UI.Menu;
using Itera.Engine.Voxels;
using Itera.Engine.Windowing;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine;

/// <summary>
/// Initialises the run's services in a fixed order, runs the main loop on the calling thread, and shuts everything down
/// in reverse. Within the run it starts and ends matches: each match's services live in a DI scope of their own
/// (<see cref="MatchServices"/>), started by the match's steps and stopped in reverse when the match ends. Windowing,
/// input, ECS, physics, network, and mod callbacks stay on this thread. Drawing runs on <see cref="RenderThread"/>,
/// which asks this thread for each frame through <see cref="FrameExchange"/>; it runs for the whole run, and no match
/// step makes a Vulkan call.
/// </summary>
public sealed partial class GameLoop
{
    /// <summary>How long shutdown waits for the render thread before leaving the graphics objects to the OS.</summary>
    private static readonly TimeSpan RenderThreadStopTimeout = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _services;
    private readonly ILogger<GameLoop> _logger;
    private readonly EngineOptions _options;
    private readonly LogSession _logSession;
    private readonly FrameStats _stats;
    private readonly GameClock _clock;
    private readonly FixedStepAccumulator _accumulator;
    private readonly WindowService _window;
    private readonly InputService _input;
    private readonly VulkanContext _vulkan;
    private readonly VulkanSwapchain _swapchain;
    private readonly FrameSync _frameSync;
    private readonly WorldMeshes _worldMeshes;
    private readonly MeshView _meshView;
    private readonly MeshPass _meshPass;
    private readonly DebugLinePass _linePass;
    private readonly UiPass _uiPass;
    private readonly UpscalePass _upscalePass;
    private readonly SmaaPass _smaaPass;
    private readonly LoadingPass _loadingPass;
    private readonly LoadingScreen _loading;
    private readonly ParticlePass _particlePass;
    private readonly ModelInstances _modelInstances;
    private readonly MeshInstances _meshInstances;
    private readonly MeshAssetPass _meshAssetPass;
    private readonly MeshAssetDraws _meshAssetDraws;
    private readonly PointLights _pointLights;
    private readonly DepthTarget _depth;
    private readonly DebugLines _debugLines;
    private readonly GpuTimer _gpuTimer;
    private readonly GpuMemoryAllocator _gpuMemory;
    private readonly StagingBuffer _staging;
    private readonly RenderThread _renderThread;
    private readonly FrameExchange _exchange;
    private readonly RenderView _renderView;
    private readonly DynamicRenderScale _dynamicScale;
    private readonly ShaderCompiler _shaders;
    private readonly NetworkService _network;
    private readonly IServiceScopeFactory _scopes;
    private readonly MatchControl _matches;
    private readonly ParticleEmitter _particles;
    private readonly SystemTimes _systemTimes;
    private readonly MatchCommands _matchCommands;
    private readonly MenuLayer _menu;
    private readonly PlayerSettings _playerSettings;
    private readonly SettingsCommands _settingsCommands;
    private bool _loopEnded;
    private readonly MatchFlow _flow;
    private double _noMatchStart;
    private readonly SteamService _steam;
    private readonly UISurface _ui;
    private readonly DevConsole _console;
    private readonly EngineCommands _engineCommands;
    private readonly ParticleCommands _particleCommands;
    private readonly ModLoader _mods;
    private readonly MeshAssets _meshAssets;
    private readonly EngineSteps _steps;
    private readonly float _maxViewDistance;
    private readonly AudioService _audio;
    private readonly SoundCommands _soundCommands;
    private readonly ControlService _control;
    private readonly Screenshots _screenshots;
    private readonly ScreenshotPass _screenshotPass;
    private readonly ShaderReloads _shaderReloads;
    private readonly NoGpuWorld _noGpuWorld;
    private bool _renderThreadFaulted;
    private bool _renderThreadFaultReported;
    private bool _modelsDroppedReported;
    private bool _meshesDroppedReported;
    private bool _worldOnGpu;
    private readonly ExitSignalPolicy _exitSignals = new();
    private volatile bool _cancelRequested;
    private PosixSignal _signal;
    private string _phase = "startup";
    private double _lastFrameSeconds;
    private ulong _frameIndex;

    /// <summary>Fixed steps run since the run began, over every match (the console's <c>wait ticks</c>).</summary>
    private ulong _stepsRun;
    private long _resetSeen;

    public GameLoop(
        IServiceProvider services,
        ILogger<GameLoop> logger,
        IOptions<EngineOptions> options,
        LogSession logSession,
        FrameStats stats,
        GameClock clock,
        FixedStepAccumulator accumulator,
        WindowService window,
        InputService input,
        VulkanContext vulkan,
        VulkanSwapchain swapchain,
        FrameSync frameSync,
        WorldMeshes worldMeshes,
        MeshView meshView,
        MeshPass meshPass,
        DebugLinePass linePass,
        UiPass uiPass,
        UpscalePass upscalePass,
        SmaaPass smaaPass,
        LoadingPass loadingPass,
        LoadingScreen loading,
        ParticlePass particlePass,
        ModelInstances modelInstances,
        MeshInstances meshInstances,
        MeshAssetPass meshAssetPass,
        MeshAssetDraws meshAssetDraws,
        PointLights pointLights,
        DepthTarget depth,
        DebugLines debugLines,
        GpuTimer gpuTimer,
        GpuMemoryAllocator gpuMemory,
        StagingBuffer staging,
        RenderThread renderThread,
        FrameExchange exchange,
        RenderView renderView,
        DynamicRenderScale dynamicScale,
        ShaderCompiler shaders,
        NetworkService network,
        SteamService steam,
        UISurface ui,
        DevConsole console,
        EngineCommands engineCommands,
        ParticleCommands particleCommands,
        AudioService audio,
        SoundCommands soundCommands,
        ModLoader mods,
        MeshAssets meshAssets,
        ControlService control,
        Screenshots screenshots,
        ScreenshotPass screenshotPass,
        ShaderReloads shaderReloads,
        IServiceScopeFactory scopes,
        MatchControl matches,
        ParticleEmitter particles,
        SystemTimes systemTimes,
        MatchCommands matchCommands,
        MenuLayer menu,
        PlayerSettings playerSettings,
        SettingsCommands settingsCommands
    )
    {
        _settingsCommands = settingsCommands;
        _playerSettings = playerSettings;
        _services = services;
        _logger = logger;
        _options = options.Value;
        _logSession = logSession;
        _stats = stats;
        _clock = clock;
        _accumulator = accumulator;
        _window = window;
        _input = input;
        _vulkan = vulkan;
        _swapchain = swapchain;
        _frameSync = frameSync;
        _worldMeshes = worldMeshes;
        _meshView = meshView;
        _meshPass = meshPass;
        _linePass = linePass;
        _uiPass = uiPass;
        _upscalePass = upscalePass;
        _smaaPass = smaaPass;
        _loadingPass = loadingPass;
        _loading = loading;
        _particlePass = particlePass;
        _modelInstances = modelInstances;
        _meshInstances = meshInstances;
        _meshAssetPass = meshAssetPass;
        _meshAssetDraws = meshAssetDraws;
        _pointLights = pointLights;
        _depth = depth;
        _debugLines = debugLines;
        _gpuTimer = gpuTimer;
        _gpuMemory = gpuMemory;
        _staging = staging;
        _renderThread = renderThread;
        _exchange = exchange;
        _renderView = renderView;
        _dynamicScale = dynamicScale;
        _shaders = shaders;
        _network = network;
        _steam = steam;
        _ui = ui;
        _console = console;
        _engineCommands = engineCommands;
        _particleCommands = particleCommands;
        _audio = audio;
        _soundCommands = soundCommands;
        _mods = mods;
        _meshAssets = meshAssets;
        _control = control;
        _screenshots = screenshots;
        _screenshotPass = screenshotPass;
        _shaderReloads = shaderReloads;
        _noGpuWorld = services.GetRequiredService<NoGpuWorld>();
        _scopes = scopes;
        _matches = matches;
        _particles = particles;
        _systemTimes = systemTimes;
        _matchCommands = matchCommands;
        _menu = menu;
        _steps = new EngineSteps(CreateSteps(), logger);
        _flow = new MatchFlow(
            scopes,
            matches,
            systemTimes,
            new MatchFlowHooks(
                // Without graphics no world goes to the GPU.
                window.Graphics
                    ? new MatchGpu(BeginMatchGpu, EndMatchGpu)
                    : new MatchGpu(_ => { }, _pointLights.Clear),
                OnWorldStarted,
                OnMatchEnded
            ),
            () => _clock.TotalSeconds,
            name => _phase = name,
            logger
        );
        // The meshes reach the coarsest level's radius, or the view distance; fog hides where they stop.
        var voxels = _options.Voxels;
        _maxViewDistance = (float)VoxelWorld.ViewRadiusMetres(voxels);
        if (_options.Renderer.ViewDistanceMetres > 0)
        {
            _maxViewDistance = Math.Min(_maxViewDistance, _options.Renderer.ViewDistanceMetres);
        }
    }

    /// <summary>
    /// Runs until the window closes, the menu's Quit or the console's <c>quit</c>, a match the options started ends, or
    /// SIGINT (Ctrl+C), SIGTERM, or SIGHUP arrives (Escape opens the menu). Returns the process exit
    /// code: 0 for a normal exit, the code <c>quit CODE</c> gave, 1 when the startup script failed with
    /// <c>Engine:Console:ExitOnFailure</c>, and 1 when initialisation or the loop threw. The exception is logged at
    /// Critical either way.
    /// </summary>
    public int Run()
    {
        var uptime = Stopwatch.StartNew();
        var exitCode = 0;
        // SIGHUP is the terminal closing on Linux and macOS: the run ends cleanly, as for the others. On Windows the game is
        // a windowed program with no console (WinExe), so it ends through its window; there SIGINT is Ctrl+C
        // and SIGHUP closing a console, which reach only a console build, and SIGTERM a shutdown event only services get.
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        using var hangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);
        try
        {
            SessionInfo.Capture().LogTo(_logger, _logSession, _options);
            Initialize();
            Loop();
            // The loop ended without an exception, so the world may be saved.
            _loopEnded = true;
            // The session's code is read first: it logs why a session ended badly.
            var sessionCode = _flow.SessionExitCode();
            exitCode = _window.ExitCode != 0 ? _window.ExitCode : sessionCode;
        }
        catch (SessionStartException ex)
        {
            // Not a fault of this game: no host, a refusal, a world it could not accept, or no Steam to host with.
            LogStartFailed(ex.Message);
            exitCode = 1;
        }
        catch (Exception ex)
        {
            LogFatal(ex, _phase, _frameIndex, _accumulator.Tick);
            exitCode = 1;
        }
        finally
        {
            Shutdown();
            if (_renderThreadFaulted)
            {
                // The render thread's exception was logged by its shutdown step; the run was not clean.
                exitCode = 1;
            }

            LogShutdownComplete(uptime.Elapsed.TotalSeconds, exitCode);
        }

        return exitCode;
    }

    /// <summary>
    /// The first signal asks the loop to stop and shut down cleanly. A later one, outside the duplicate window, is not
    /// cancelled, so the runtime ends the process at once. See <see cref="ExitSignalPolicy"/>.
    /// </summary>
    private void OnSignal(PosixSignalContext context)
    {
        switch (_exitSignals.OnSignal(Stopwatch.GetTimestamp()))
        {
            case ExitSignalAction.RequestExit:
                context.Cancel = true;
                _signal = context.Signal;
                _cancelRequested = true;
                LogSignal(context.Signal);
                // A loop that hangs inside a mod never reads the request; the log names the mod.
                if (_mods.CurrentCall is { } call)
                {
                    LogSignalInMod(call);
                }

                break;
            case ExitSignalAction.Ignore:
                context.Cancel = true;
                break;
            case ExitSignalAction.Terminate:
                break;
        }
    }

    /// <summary>
    /// The run's services in initialisation order, each with the action that shuts it down: what lives from start to
    /// exit, whatever matches are played (a match's own steps are <see cref="MatchServices"/>'). This and that are the
    /// only places the order is written: <see cref="Initialize"/> walks it forward and <see cref="Shutdown"/> walks it
    /// back, after the match has ended. Steam comes first, before the Vulkan instance, whose layers it adds. The player's
    /// settings file is read before the window, which opens full screen by it, and the swapchain, by its VSync. The renderer
    /// makes every pass and the mesh heap, sized from the options alone, so starting a match needs no Vulkan call. The
    /// game's step makes its run-wide registries (its sounds' and particles' recipes and its other
    /// definitions) before the mods add theirs, and the audio thread starts after both, since it takes the recipes once.
    /// The render thread starts after everything it draws, and the control port after it, once the engine's commands
    /// are registered; the port stops first, so no request arrives during shutdown. Until the render thread has stopped,
    /// this thread makes no Vulkan call. Steps marked as graphics are skipped at shutdown when the render thread could
    /// not be stopped.
    /// </summary>
    private EngineStep[] CreateSteps()
    {
        EngineStep[] steps =
        [
            new("network", _network.Initialize, () => { }),
            new("steam", _steam.Initialize, _steam.Dispose),
            new("settings file", _playerSettings.Initialize, () => { }),
            new("window", () => _window.Initialize(_playerSettings.Fullscreen), _window.Dispose, Graphics: true),
            new("vulkan context", () => _vulkan.Initialize(_window.SurfaceSource), _vulkan.Dispose, Graphics: true),
            new("swapchain", InitializeSwapchain, _swapchain.Dispose, Graphics: true),
            new("frame sync", () => _frameSync.Initialize(_swapchain.Images.Count), _frameSync.Dispose, Graphics: true),
            new("gpu memory", InitializeGpuMemory, ShutdownGpuMemory, Graphics: true),
            new("input", _input.Initialize, _input.Dispose),
            new("shader compiler", InitializeShaders, _shaders.Dispose),
            new("audio", InitializeAudio, ShutdownAudio),
            new("settings", _playerSettings.Apply, _playerSettings.Save),
            new("renderer", InitializeRenderer, ShutdownRenderer, Graphics: true),
            new("ui", InitializeUi, ShutdownUi),
            new("game", StartGame, () => { }),
            new("mods", () => _mods.LoadAll(), ShutdownMods),
            // After the game's and the mods' startups, which declare their models; textures only with graphics.
            new("mesh assets", () => _meshAssets.LoadAll(_window.Graphics), () => { }),
            new("mesh assets on the gpu", _meshAssetPass.Initialize, _meshAssetPass.Dispose, Graphics: true),
            new("audio thread", _audio.Start, () => _audio.Stop()),
            new("render thread", () => _renderThread.Start(_window.FramebufferSize), StopRenderThread),
            new("control", _control.Initialize, _control.Dispose),
        ];
        // Without graphics (Engine:Window:Mode=None) nothing of Vulkan starts: the window step stands in for a window.
        return _window.Graphics
            ? steps
            :
            [
                .. steps.Where(step =>
                    !(step.Graphics && step.Name != "window") && step.Name is not ("shader compiler" or "render thread")
                ),
            ];
    }

    /// <summary>The swapchain, presenting as the player's VSync setting asks (settings.json, or the options).</summary>
    private void InitializeSwapchain()
    {
        _swapchain.SetVSync(_playerSettings.VSync);
        _swapchain.Initialize(_window.FramebufferSize);
    }

    private void Initialize()
    {
        var total = Stopwatch.StartNew();
        _steps.Initialize(name => _phase = name);
        _phase = "main loop";
        LogReady(_window.Graphics ? _vulkan.DeviceName : "no graphics", total.Elapsed.TotalMilliseconds);
    }

    /// <summary>The game's run-wide registries (<see cref="IRunStartup"/>), made once, before the mods and the audio thread.</summary>
    private void StartGame()
    {
        foreach (var startup in _services.GetServices<IRunStartup>())
        {
            startup.Start();
        }
    }

    /// <summary>
    /// Opens the audio device (or none, when <c>Engine:Audio:Enabled</c> is false) and registers the engine's recipes;
    /// the thread that plays them starts near the end, once the game has registered its own.
    /// </summary>
    private void InitializeAudio()
    {
        _audio.Initialize(
            _options.Audio.Enabled ? _services.GetRequiredService<IAudioDevice>() : new NullAudioDevice()
        );
    }

    private void ShutdownAudio()
    {
        _audio.Stop();
        if (_options.Audio.Enabled)
        {
            _services.GetRequiredService<IAudioDevice>().Dispose();
        }
    }

    private void InitializeGpuMemory()
    {
        _gpuMemory.Initialize();
        _staging.Initialize();
    }

    private void ShutdownGpuMemory()
    {
        _staging.Dispose();
        _gpuMemory.Dispose();
    }

    /// <summary>
    /// Creates every object the render thread draws with, once per run; the world meshes follow a match's world only
    /// from <see cref="WorldMeshes.BeginMatch"/>. The shader compiler exists.
    /// </summary>
    private void InitializeRenderer()
    {
        _depth.Initialize();
        // The player's anti-aliasing (settings.json, or the options).
        _meshPass.RequestSamples(_playerSettings.MsaaSamples);
        _meshPass.Initialize();
        _worldMeshes.Initialize();
        _particlePass.Initialize();
        _linePass.Initialize();
        _uiPass.Initialize();
        _upscalePass.Initialize();
        _smaaPass.Initialize();
        _loadingPass.Initialize();
        _gpuTimer.Initialize();
    }

    private void ShutdownRenderer()
    {
        _screenshotPass.Dispose();
        _gpuTimer.Dispose();
        _loadingPass.Dispose();
        _smaaPass.Dispose();
        _upscalePass.Dispose();
        _uiPass.Dispose();
        _linePass.Dispose();
        _particlePass.Dispose();
        _worldMeshes.Dispose();
        _meshPass.Dispose();
        _depth.Dispose();
    }

    private void InitializeShaders()
    {
        _shaders.Initialize();
        var spirv = _shaders.Compile("smoke-test.vert");
        LogShaderSmokeTest(spirv.Length / 4);
    }

    private void InitializeUi()
    {
        // The swapchain's size, which the frame requests carry; each frame's request resizes the surface after that.
        // Without graphics, the options' size.
        var size = _window.FramebufferSize;
        var (width, height) = _window.Graphics
            ? ((int)_swapchain.Extent.Width, (int)_swapchain.Extent.Height)
            : (size.X, size.Y);
        _ui.Initialize(width, height);
        // The match's layers and systems add their commands when a match starts, owned by it (MatchServices).
        _engineCommands.Register();
        _services.GetRequiredService<TimeCommands>().Register();
        _matchCommands.Register();
        _settingsCommands.Register();
        _particleCommands.Register();
        _soundCommands.Register();
        _input.SetKeyboardFocus(_console);
        _input.SetMenu(_menu);
    }

    private void ShutdownUi()
    {
        _ui.Dispose();
    }

    private void ShutdownMods()
    {
        _mods.NotifyGameExit();
        _mods.UnloadAll();
    }

    /// <summary>
    /// Stops the render thread, then waits for the GPU. When the thread does not stop in time it is stuck inside a
    /// Vulkan call (a present that blocks while the window is hidden), so the window and every Vulkan object are left
    /// to the OS: destroying them under that call is unsafe. The other shutdown steps still run. A thread that ended
    /// with an exception while stopping is reported (Error, exit code 1) instead of looking like a clean run; one the
    /// loop already reported is not reported again.
    /// </summary>
    private void StopRenderThread()
    {
        if (_renderThread.Stop(RenderThreadStopTimeout))
        {
            // The wait comes first, also after a fault: frames the thread submitted before it failed may still run, and
            // the next shutdown steps destroy what they use.
            var faulted = _renderThread.IsFaulted;
            try
            {
                _vulkan.WaitIdle();
            }
            catch (VulkanException ex) when (faulted)
            {
                // The fault has most likely lost the device too; destroying objects on a lost device is allowed. The
                // fault itself is what gets reported.
                LogWaitAfterFaultFailed(ex.Result);
            }

            if (faulted && !_renderThreadFaultReported)
            {
                // The thread ended with an exception after the loop stopped asking for frames, so the loop never saw it.
                // Rethrown here, the shutdown step logs it, the other steps still run, and the exit code is 1.
                _renderThreadFaulted = true;
                _renderThread.ThrowIfFaulted();
            }

            return;
        }

        _steps.GraphicsAbandoned = true;
        _vulkan.Abandon();
        _window.Abandon();
        LogRenderThreadStuck(RenderThreadStopTimeout.TotalSeconds);
    }

    private void Loop()
    {
        _clock.Start();
        _stats.Start(_clock.TotalSeconds, _renderThread.AllocatedBytes);
        _frameIndex = 0;
        _lastFrameSeconds = 0d;
        _noMatchStart = _clock.TotalSeconds;
        // The match the options start; with none, the menu. Steam's "Join game" starts the menu with the friend's lobby as
        // the address: the menu joins it at once, and comes back with the reason if it cannot.
        _flow.Begin(_options, _matchCommands);

        while (!_window.ExitRequested && !_cancelRequested && !_window.IsClosing && !_flow.SessionEndsRun)
        {
            CheckRenderThread();
            if (!_options.FreeRun)
            {
                WaitForWork();
            }

            var deltaSeconds = _clock.Tick();
            if (_options.FreeRun)
            {
                // Back to back: each iteration as many steps as the cap allows, at once, whatever time passed.
                deltaSeconds = _accumulator.StepSeconds * _options.MaxFixedStepsPerFrame / _accumulator.TimeScale;
            }
            _steam.RunCallbacks();
            var request = default(RenderFrameRequest);
            try
            {
                Iterate(deltaSeconds, ref request);
            }
            catch (Exception ex) when (ex is not VulkanException && _flow.EndsAfterFault)
            {
                // An exception in a match the menu or the console started ends that match, unsaved, and the run goes
                // back to the menu with the reason. A device's failure still ends the run.
                _flow.EndAfterFault(ex);
                if (_exchange.RequestTaken)
                {
                    // The render thread waits for the frame it asked for.
                    BuildFrameWithoutWorld(in request);
                }
            }

            PublishAudio();
            _stats.Update(_clock.TotalSeconds, _renderThread.AllocatedBytes);
        }

        var reason =
            _flow.SessionEndsRun ? _flow.Match!.Session.EndReason
            : _cancelRequested ? _signal.ToString()
            : _window.ExitReason ?? "window closing";
        LogLoopEnded(_frameIndex, _accumulator.Tick, _accumulator.DroppedSteps, reason);
    }

    /// <summary>
    /// One iteration's work after the wait: the session, input, the match's start or end, streaming, the console and
    /// the control port, the fixed steps, and the frame when the render thread asks for one (<paramref name="request"/>,
    /// taken here: until Publish, this thread may write the memory of its slot).
    /// </summary>
    private void Iterate(float deltaSeconds, ref RenderFrameRequest request)
    {
        _flow.PollSession(_frameIndex);
        _input.Update();
        // Matches start and end here, between iterations' work, never during a step or a frame.
        _flow.HandleRequests(_frameIndex);
        var playing = _flow.Playing;
        playing?.Terrain?.Voxels.Update(_clock.TotalSeconds);
        // Console commands run here, between fixed steps and outside any query, also while no frames are drawn. Those
        // given at start wait for the match's world and systems, which most of them need.
        _console.HoldStartup = playing is null;
        var loaded = playing is not null && _matches.Phase == MatchPhase.Playing && _loading.Phase == LoadingPhase.Done;
        _console.RunPending(new ConsoleMoment(_stepsRun, _frameIndex, _clock.TotalSeconds, loaded));
        if (_console.StartupScriptFailed && _options.Console.ExitOnFailure)
        {
            _window.RequestExit("console startup script failed", 1);
        }

        // The control port's requests too: its commands, its waits, and what they wait for.
        _control.RunPending(_clock.TotalSeconds, _frameIndex);
        _flow.StartWorldWhenShown(_frameIndex);
        var fixedSteps = playing is not null ? RunFixedSteps(deltaSeconds, playing) : 0;
        AfterFixedSteps(fixedSteps);
        if (!_window.Graphics)
        {
            // No render thread asks for frames: the frame group runs once after the steps.
            if (fixedSteps > 0 && _flow.Playing is { } headless)
            {
                BuildFrameWithoutGraphics(headless);
            }

            return;
        }

        // A frame is built only when the render thread asks for one, right after its fence wait and acquire.
        if (_exchange.TryTakeRequest(out request))
        {
            // The input that came while the fixed steps ran reaches this frame too.
            _window.PollEvents();
            if (_flow.Playing is { } match)
            {
                BuildFrame(in request, match);
            }
            else
            {
                BuildFrameWithoutWorld(in request);
            }
        }
    }

    /// <summary>The flow started a match's world: the loading screen covers the picture until it is ready.</summary>
    private void OnWorldStarted(MatchServices match)
    {
        // A save's or a host's reset, before the first column loaded, is covered by the first loading screen.
        _resetSeen = match.Terrain?.Voxels.LastReset ?? _resetSeen;
        ApplyLoading();
    }

    /// <summary>A match ended: what the run kept of it (the view, the title, the particles) is put back.</summary>
    private void OnMatchEnded()
    {
        _renderView.ResetAfterMatch();
        _window.SetTitle(_window.BaseTitle);
        _particles.Clear();
        _noMatchStart = _clock.TotalSeconds;
        ApplyLoading();
    }

    /// <summary>Rethrows the render thread's exception on this thread, where the loop reports it.</summary>
    private void CheckRenderThread()
    {
        if (_renderThread.IsFaulted)
        {
            _phase = "render thread";
            // Logged at Critical by Run; StopRenderThread does not report it a second time.
            _renderThreadFaultReported = true;
            _renderThread.ThrowIfFaulted();
        }
    }

    /// <summary>Handles the window's events, sleeping first until the next fixed step when no frame is asked for.</summary>
    private void WaitForWork()
    {
        // Sleep until the next fixed step is due, after handling the window events that wait. The render thread's
        // request for a frame wakes this wait at once. While presentation blocks, no request comes and the loop still
        // runs every step. Without a match no time is added, so the wait is at most a step (at a time scale below 1 it is
        // longer: a step of game time).
        if (_exchange.HasRequest)
        {
            _window.PollEvents();
        }
        else
        {
            _window.WaitEvents(_accumulator.SecondsUntilNextStep);
        }
    }

    /// <summary>
    /// Runs the fixed steps the time since the last iteration makes due: the fixed group, the large edits' share, the
    /// voxel colliders, Bepu, the bodies as voxels, the mods, navigation, and the session. Returns how many ran.
    /// </summary>
    private int RunFixedSteps(float deltaSeconds, MatchServices match)
    {
        var fixedSteps = 0;
        _accumulator.Add(deltaSeconds);
        while (_accumulator.TryConsumeStep(out var step))
        {
            _input.BeginTick();
            match.RunFixedStep(in step, _options.Voxels.EditColumnsPerStep, _stats, _mods);
            fixedSteps++;
            _stepsRun++;
        }
        return fixedSteps;
    }

    /// <summary>Counts the steps, starts the loading screen again after a reset, and moves the loading screen on.</summary>
    private void AfterFixedSteps(int fixedSteps)
    {
        // Steps dropped while the loading screen covers the picture (the cut warm-up holds this thread for a moment)
        // change nothing anyone sees; the loading screen's own line reports them.
        var dropped = _accumulator.LastDropped;
        if (_loading.Active)
        {
            _loading.CountDroppedSteps(dropped);
            dropped = 0;
        }

        _stats.RecordSteps(fixedSteps, dropped);
        // A reset of the world (the console's, the host's, or a joined save's) unloaded every column: the loading screen
        // covers the picture until they are generated again.
        if (_flow.Playing?.Terrain is { } terrain && terrain.Voxels.LastReset != _resetSeen)
        {
            _resetSeen = terrain.Voxels.LastReset;
            _loading.BeginAgain(_clock.TotalSeconds);
        }

        // After the steps, which move navigation's fields and character kinds on.
        if (_loading.Phase != LoadingPhase.Done)
        {
            _loading.Update(_clock.TotalSeconds);
        }

        if (_flow.Playing is not null)
        {
            _matches.Phase = _loading.Active ? MatchPhase.Loading : MatchPhase.Playing;
        }

        ApplyLoading();
    }

    /// <summary>
    /// Builds the frame <paramref name="request"/> asks for, right after the render thread's fence wait and acquire: the
    /// frame group, the mods, the UI, the view, the uploads and the passes' slot memory, then publishes it. Until
    /// Publish, this thread may write the memory of the request's slot.
    /// </summary>
    private void BuildFrame(in RenderFrameRequest request, MatchServices match)
    {
        var now = _clock.TotalSeconds;
        var frameSeconds = now - _lastFrameSeconds;
        _lastFrameSeconds = now;
        var frameDelta = (float)Math.Min(frameSeconds, GameClock.MaxDeltaSeconds);
        var frame = new FrameTime(frameDelta, now, _frameIndex, _accumulator.Alpha);
        _input.BeginFrame();
        _debugLines.Clear();
        _modelInstances.Clear();
        _meshInstances.Clear();
        _pointLights.BeginFrame(now);
        match.Ecs.Update(in frame);
        _mods.Update(in frame);
        if (_modelInstances.Dropped > 0 && !_modelsDroppedReported)
        {
            _modelsDroppedReported = true;
            LogModelsDropped(_modelInstances.Dropped, ModelInstances.MaxModels);
        }

        if (_meshInstances.Dropped > 0 && !_meshesDroppedReported)
        {
            _meshesDroppedReported = true;
            LogMeshesDropped(_meshInstances.Dropped, MeshInstances.MaxInstances);
        }
        var ui = DrawUi(in frame, in request);
        _dynamicScale.Update(now);
        var view = CreateView(in request);
        var loading = _loading.Drawn ? new LoadingDraw(_loading.Alpha, _loading.Seconds) : default;
        // The models are drawn with the world, in the frame's view, whose point lights the world's glowing blocks and the
        // events give.
        _meshView.Begin(request.Slot, in view);
        // Without terrain no world is followed: nothing to upload or draw of it.
        var copies = _worldOnGpu ? _worldMeshes.Upload(request.Slot, now, in view, _modelInstances.Draws) : 0;
        var meshAssets = _meshAssetDraws.Write(
            request.Slot,
            _meshView,
            _meshInstances.Instances,
            _meshInstances.Palettes
        );
        _meshView.End(request.Slot, _pointLights.Frame(now));
        if (_debugLines.Enabled)
        {
            match.Terrain?.Colliders.AddOutlines(_debugLines);
        }

        var lines = _linePass.Write(request.Slot, _debugLines.Vertices);
        _particleCommands.UpdateStress();
        var particles = _particlePass.Write(request.Slot, in frame, in view);
        var uiRows = WriteUi(in request, in ui);
        _exchange.Publish(
            new RenderFrame(
                frame,
                request.Slot,
                view,
                copies,
                lines,
                ui.RowStart,
                uiRows,
                ui.Visible,
                _renderView.RenderScale,
                particles,
                loading,
                _screenshots.TakeForFrame(),
                _shaderReloads.TakeForFrame(),
                _renderView.ScreenDim,
                _renderView.AntiAliasDrawn,
                _renderView.AntiAliasPicture,
                _worldOnGpu ? _worldMeshes.Frame : default,
                meshAssets
            )
        );
        _frameIndex++;
        _stats.RecordFrame(frameSeconds);
    }

    /// <summary>
    /// A frame without graphics (<c>Engine:Window:Mode=None</c>): the frame group and the mods run, writing only the
    /// renderer's CPU-side objects, and nothing is drawn or uploaded; the loading screen counts it as an upload
    /// (<see cref="NoGpuWorld"/>).
    /// </summary>
    private void BuildFrameWithoutGraphics(MatchServices match)
    {
        var now = _clock.TotalSeconds;
        var frameSeconds = now - _lastFrameSeconds;
        _lastFrameSeconds = now;
        var frameDelta = (float)Math.Min(frameSeconds, GameClock.MaxDeltaSeconds);
        var frame = new FrameTime(frameDelta, now, _frameIndex, _accumulator.Alpha);
        _input.BeginFrame();
        _debugLines.Clear();
        _modelInstances.Clear();
        _meshInstances.Clear();
        _pointLights.BeginFrame(now);
        match.Ecs.Update(in frame);
        _mods.Update(in frame);
        _noGpuWorld.FrameBuilt();
        _frameIndex++;
        _stats.RecordFrame(frameSeconds);
    }

    /// <summary>The match's world goes to the GPU: the meshes follow its voxel terrain, when it has one.</summary>
    private void BeginMatchGpu(MatchServices match)
    {
        if (match.Terrain is { } terrain)
        {
            _worldMeshes.BeginMatch(terrain.Voxels, terrain.Models, match.TintMap);
            _worldOnGpu = true;
        }
    }

    /// <summary>The match's world leaves the GPU: the meshes stop following it, and its lights' flashes go.</summary>
    private void EndMatchGpu()
    {
        if (_worldOnGpu)
        {
            _worldMeshes.EndMatch();
            _worldOnGpu = false;
        }

        _pointLights.Clear();
    }

    /// <summary>
    /// A frame without a match's world (joining, starting, or none): the loading screen's ring covers the picture, with
    /// the UI over it. Nothing of a world is uploaded or drawn.
    /// </summary>
    private void BuildFrameWithoutWorld(in RenderFrameRequest request)
    {
        var now = _clock.TotalSeconds;
        var frameSeconds = now - _lastFrameSeconds;
        _lastFrameSeconds = now;
        var frameDelta = (float)Math.Min(frameSeconds, GameClock.MaxDeltaSeconds);
        var frame = new FrameTime(frameDelta, now, _frameIndex, _accumulator.Alpha);
        _input.BeginFrame();
        _debugLines.Clear();
        _modelInstances.Clear();
        _meshInstances.Clear();
        var ui = DrawUi(in frame, in request);
        _dynamicScale.Update(now);
        var view = CreateView(in request);
        var lines = _linePass.Write(request.Slot, _debugLines.Vertices);
        var particles = _particlePass.Write(request.Slot, in frame, in view);
        var uiRows = WriteUi(in request, in ui);
        _exchange.Publish(
            new RenderFrame(
                frame,
                request.Slot,
                view,
                MeshCopies: 0,
                DebugLineVertices: lines,
                UiRowStart: ui.RowStart,
                UiRows: uiRows,
                UiVisible: ui.Visible,
                RenderScale: _renderView.RenderScale,
                Particles: particles,
                Loading: new LoadingDraw(1, (float)(now - _noMatchStart)),
                Screenshot: _screenshots.TakeForFrame(),
                ShaderReload: _shaderReloads.TakeForFrame()
            )
        );
        _frameIndex++;
        _stats.RecordFrame(frameSeconds);
    }

    private ViewConstants CreateView(in RenderFrameRequest request)
    {
        var camera = _renderView.Camera;
        var atmosphere = _renderView.Atmosphere;
        return ViewConstants.Create(
            in camera,
            request.Width,
            request.Height,
            request.Slot,
            ViewConstants.DefaultSunDirection,
            in atmosphere,
            _maxViewDistance,
            _renderView.Shadows,
            _renderView.RenderScale,
            _renderView.DebugViews
        );
    }

    /// <summary>
    /// Draws the UI at the request's size: every row when the pass's buffers were made again since the last frame. The
    /// pass has no buffer for a screen too large for it, and it sends nothing then; the surface is not drawn again each
    /// frame for it.
    /// </summary>
    private UiDraw DrawUi(in FrameTime frame, in RenderFrameRequest request)
    {
        _ui.FollowBuffers(_uiPass.Generation);
        return _ui.Draw(in frame, (int)request.Width, (int)request.Height);
    }

    /// <summary>Copies the UI's changed rows into the slot's region.</summary>
    private int WriteUi(in RenderFrameRequest request, in UiDraw ui) =>
        _uiPass.Write(request.Slot, _ui.Pixels, _ui.Width, _ui.Height, ui.RowStart, ui.Rows);

    /// <summary>This iteration's sounds, heard from where the frame systems last put the camera.</summary>
    private void PublishAudio()
    {
        var listener = _renderView.Camera;
        _audio.Publish(new ListenerPose(listener.Position, listener.Forward, listener.Up), _clock.TotalSeconds);
    }

    /// <summary>While the loading screen covers the picture, no match is played, or the menu shows, the game gets no input.</summary>
    private void ApplyLoading() =>
        _input.SetGameInputBlocked(_loading.Active || _matches.Phase != MatchPhase.Playing || _menu.IsOpen);

    /// <summary>
    /// Ends the match (its steps in reverse, the save written when <see cref="SavesAtEnd"/> says so), then shuts down
    /// every run step whose initialisation started, in reverse order (<see cref="EngineSteps.Shutdown"/>).
    /// </summary>
    private void Shutdown()
    {
        _flow.End(SavesAtEnd(_loopEnded, _renderThreadFaultReported));
        _phase = "shutdown";
        _steps.Shutdown();
    }

    /// <summary>
    /// True when the match's world is saved as the run ends: after a loop that ended without an exception, or when the
    /// render thread's fault ended it: the world lives
    /// on this thread and is whole. Another exception of the loop may have left it half changed, so it is not saved.
    /// </summary>
    internal static bool SavesAtEnd(bool loopEnded, bool renderThreadFault) => loopEnded || renderThreadFault;

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Models: {Dropped} model(s) past the {Most} a frame can draw were not drawn (reported once)"
    )]
    private partial void LogModelsDropped(int dropped, int most);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: {Dropped} instance(s) past the {Most} a frame can draw were not drawn (reported once)"
    )]
    private partial void LogMeshesDropped(int dropped, int most);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shaderc smoke test: compiled {Words} words of SPIR-V")]
    private partial void LogShaderSmokeTest(int words);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Engine ready on {Device} after {Ms:F0} ms; entering main loop"
    )]
    private partial void LogReady(string device, double ms);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "{Signal} received; shutting down (send it again to stop at once)"
    )]
    private partial void LogSignal(PosixSignal signal);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The loop is inside mod {Call}; if it does not return, the next signal stops the game at once"
    )]
    private partial void LogSignalInMod(string call);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Main loop ended after {Frames} frame(s) and {Ticks} fixed tick(s) ({Dropped} dropped); reason: {Reason}"
    )]
    private partial void LogLoopEnded(ulong frames, ulong ticks, ulong dropped, string reason);

    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "Unrecoverable error during {Phase} at frame {Frame}, tick {Tick}; shutting down"
    )]
    private partial void LogFatal(Exception exception, string phase, ulong frame, ulong tick);

    [LoggerMessage(Level = LogLevel.Error, Message = "Session: {Reason} Shutting down.")]
    private partial void LogStartFailed(string reason);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The render thread did not stop within {Seconds:F0} s; it is blocked inside presentation (for example, the window is hidden). The window and Vulkan objects are left for the OS to free at exit"
    )]
    private partial void LogRenderThreadStuck(double seconds);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Waiting for the GPU after the render thread's fault failed ({Result}); shutting down without it"
    )]
    private partial void LogWaitAfterFaultFailed(Silk.NET.Vulkan.Result result);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Shutdown complete after {Uptime:F1} s; exit code {ExitCode}"
    )]
    private partial void LogShutdownComplete(double uptime, int exitCode);
}
