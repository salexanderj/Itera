using System.Diagnostics;
using Itera.Engine.Commands;
using Itera.Engine.Diagnostics;
using Itera.Engine.Ecs;
using Itera.Engine.Loading;
using Itera.Engine.Maths;
using Itera.Engine.Modding;
using Itera.Engine.Navigation;
using Itera.Engine.Networking;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Rendering.Voxels;
using Itera.Engine.Saves;
using Itera.Engine.Time;
using Itera.Engine.UI;
using Itera.Engine.Voxels;
using Itera.Modding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Matches;

/// <summary>
/// The match's world on the GPU: what starts and stops it there. <c>GameLoop</c> hands the world meshes' calls; a test
/// without a GPU hands actions that do nothing.
/// </summary>
/// <param name="Begin">Starts following the match's world (<see cref="Rendering.Meshes.WorldMeshes.BeginMatch"/>).</param>
/// <param name="End">Stops following it (<see cref="Rendering.Meshes.WorldMeshes.EndMatch"/>).</param>
public sealed record MatchGpu(Action<MatchServices> Begin, Action End);

/// <summary>
/// One match's engine services, made in the match's DI scope (every service that holds the match's state is Scoped):
/// its session, physics, save, and ECS world, its voxel terrain when the game has one (<see cref="Terrain"/>), and the
/// steps that start and stop them. The order is written here and in <c>GameLoop</c>'s run steps only. Layers, systems, and loading tasks of the scope are resolved
/// at their steps, never sooner: some read the match's seed, which the session settles. Main thread.
/// </summary>
public sealed partial class MatchServices
{
    private readonly IServiceProvider _scope;
    private readonly MatchContext _context;
    private readonly UISurface _ui;
    private readonly ConsoleCommands _commands;
    private readonly LoadingScreen _loading;
    private readonly FixedStepAccumulator _accumulator;
    private readonly ParticleColours _colours;
    private readonly ModLoader _mods;
    private readonly ILogger<MatchServices> _logger;

    public MatchServices(
        IServiceProvider scope,
        MatchContext context,
        SessionService session,
        PhysicsWorld physics,
        SaveService saves,
        EcsWorld ecs,
        UISurface ui,
        ConsoleCommands commands,
        LoadingScreen loading,
        FixedStepAccumulator accumulator,
        ParticleColours colours,
        ModLoader mods,
        VoxelModels models,
        LooseBodyVoxels bodyVoxels,
        ILogger<MatchServices> logger,
        MatchVoxels? terrain = null
    )
    {
        _mods = mods;
        Models = models;
        BodyVoxels = bodyVoxels;
        _scope = scope;
        _context = context;
        Session = session;
        Terrain = terrain;
        Physics = physics;
        Saves = saves;
        Ecs = ecs;
        _ui = ui;
        _commands = commands;
        _loading = loading;
        _accumulator = accumulator;
        _colours = colours;
        _logger = logger;
    }

    /// <summary>What the match started as.</summary>
    public MatchStart Start => _context.Start;

    /// <summary>The scope's services, for the game's (resolved at their steps).</summary>
    public IServiceProvider Services => _scope;

    public SessionService Session { get; }

    /// <summary>The match's voxel terrain, or null for a game without one (no <c>AddIteraVoxels</c>).</summary>
    public MatchVoxels? Terrain { get; }

    /// <summary>The terrain's world. Throws in a match without terrain: code that may run there reads <see cref="Terrain"/>.</summary>
    public VoxelWorld Voxels => (Terrain ?? throw NoTerrain()).Voxels;

    /// <summary>The match's voxel models (in the world's memory with terrain).</summary>
    public VoxelModels Models { get; }

    public PhysicsWorld Physics { get; }

    /// <summary>The terrain's colliders; throws in a match without terrain.</summary>
    public VoxelColliders Colliders => (Terrain ?? throw NoTerrain()).Colliders;

    /// <summary>The loose bodies as the character mover's cells.</summary>
    public LooseBodyVoxels BodyVoxels { get; }

    /// <summary>Navigation over the terrain; throws in a match without terrain.</summary>
    public NavigationService Navigation => (Terrain ?? throw NoTerrain()).Navigation;

    public SaveService Saves { get; }

    public EcsWorld Ecs { get; }

    /// <summary>The match's tint map: its seed's, so read only once the session has settled the match.</summary>
    public IVoxelTintMap TintMap => _scope.GetRequiredService<IVoxelTintMap>();

    /// <summary>
    /// True once the match ended without an exception: only then is its world saved (a named save on a host or
    /// offline). <c>GameLoop</c> sets it before the match's steps shut down.
    /// </summary>
    public bool EndedCleanly { get; set; }

    /// <summary>The session's step: settles the match at once offline or hosting, or starts joining.</summary>
    internal IReadOnlyList<EngineStep> CreateSessionSteps() => [new("session", Session.Begin, Session.Stop)];

    /// <summary>
    /// One fixed step of the match, in the order the game runs it (the main loop calls it; tests call it too, so they run
    /// what the game runs): the fixed group, the bodies of the parts it cut off objects, the large edits' share (<paramref name="editColumns"/> columns), the voxel
    /// colliders, Bepu, the bodies as voxels, the mods (<paramref name="mods"/>), navigation's finished fields, and the
    /// session's step; the terrain's parts only with terrain. <paramref name="stats"/>, when given, takes the parts'
    /// timestamps.
    /// </summary>
    public void RunFixedStep(in FixedStepTime step, int editColumns, FrameStats? stats = null, ModLoader? mods = null)
    {
        var terrain = Terrain;
        var stepStart = Stopwatch.GetTimestamp();
        Ecs.FixedUpdate(in step);
        // The parts the step's edits cut off objects fall as bodies from this step on.
        terrain?.Fallen.Make();
        var editsStart = Stopwatch.GetTimestamp();
        // Large edits apply over steps, a share each step, before Bepu and the renderer read the change list.
        terrain?.Voxels.ApplyPendingEdits(editColumns);
        var collidersStart = Stopwatch.GetTimestamp();
        // The fixed group's edits and the bodies' positions decide which voxels Bepu sees in this step.
        terrain?.Colliders.Update(step.StepSeconds);
        var physicsStart = Stopwatch.GetTimestamp();
        Physics.Step(step.StepSeconds);
        var bodyVoxelsStart = Stopwatch.GetTimestamp();
        // The character mover reads loose bodies where this step left them.
        BodyVoxels.Refresh();
        stats?.RecordStepParts(
            stepStart,
            editsStart,
            collidersStart,
            physicsStart,
            bodyVoxelsStart,
            Stopwatch.GetTimestamp()
        );
        mods?.FixedUpdate(in step);
        // Fields the navigation thread finished are taken in between steps, never while a system reads them.
        terrain?.Navigation.AfterFixedStep();
        // The step's edits, the tools' and the mods', go to the clients in the order they were made.
        Session.AfterFixedStep();
    }

    /// <summary>
    /// Puts the match's first origin where the game's match starts (its <see cref="IOriginStart"/>, which may read the
    /// seed the session settled), before anything keeps a local position; without one it stays at the map's middle.
    /// </summary>
    private void StartOrigin()
    {
        if (_scope.GetService<IOriginStart>() is { } start)
        {
            _scope.GetRequiredService<WorldOrigin>().StartAt(start.Start);
        }
    }

    /// <summary>
    /// The match's other steps, once the session has settled the match (at once offline or hosting; after the download
    /// when joining). The voxel world starts from the match's seed; the joined world step loads the host's edits (or the
    /// named save's) into it, and without terrain only reads what the host sent; navigation starts with the seed and
    /// every edit so far; the object kinds' models are made once the world's memory exists; the save step writes the named save when the match ends, while physics and the world still
    /// exist; the world goes to the GPU; the match's console commands (registered as the HUD layers and systems are made)
    /// and HUD layers come before the systems; the mods hear of the match once its systems exist; the loading screen
    /// waits for the match's tasks. ECS comes after physics and voxels so a system's <c>Initialize</c> can use them, and
    /// its shutdown waits for character builds before the world's memory is freed. Without terrain the voxels,
    /// navigation, voxel physics, and objects steps are left out, and no world goes to the GPU.
    /// </summary>
    internal IReadOnlyList<EngineStep> CreateWorldSteps(MatchGpu gpu, Func<double> now)
    {
        var steps = new List<EngineStep>
        {
            new("origin", StartOrigin, () => { }),
            new("physics", Physics.Initialize, Physics.Dispose),
        };
        if (Terrain is { } terrain)
        {
            steps.Add(new("voxels", () => InitializeVoxels(terrain.Voxels), terrain.Voxels.Dispose));
        }

        steps.Add(new("joined world", Session.LoadJoinedWorld, () => { }));
        if (Terrain is { } world)
        {
            steps.Add(new("navigation", world.Navigation.Initialize, world.Navigation.Dispose));
            steps.Add(new("voxel physics", world.Colliders.Initialize, world.Colliders.Dispose));
            steps.Add(
                new(
                    "objects",
                    () =>
                    {
                        world.Objects.Initialize();
                        world.Placed.Initialize();
                        world.Damage.Initialize();
                    },
                    () => { }
                )
            );
        }

        steps.Add(new("save", () => { }, () => Saves.Save(EndedCleanly, Session.Role, Session.Match)));
        steps.Add(new("tint", BeginTint, () => _colours.Tint = null));
        steps.Add(new("world on the gpu", () => gpu.Begin(this), gpu.End));

        steps.Add(new("match ui", BeginUi, EndUi));
        steps.Add(new("ecs", InitializeEcs, Ecs.Dispose));
        steps.Add(new("mods", () => _mods.MatchStarted(_scope), _mods.MatchEnded));
        steps.Add(new("loading", () => BeginLoading(now()), _loading.End));
        return steps;
    }

    private void InitializeVoxels(VoxelWorld voxels)
    {
        voxels.Initialize(Session.Match.Seed);
        // Compiles the edit code in the background, so the first large edit in play does not stall.
        voxels.StartEditWarmUp();
    }

    private static InvalidOperationException NoTerrain() =>
        new("This match has no voxel terrain: the game did not call AddIteraVoxels.");

    private void BeginTint()
    {
        _colours.Tint = _scope.GetRequiredService<VoxelTintSampler>();
    }

    private void BeginUi()
    {
        // Commands registered from here (by the HUD layers as they are made, and by the systems) are the match's.
        _commands.BeginMatch();
        _ui.SetMatchLayers([.. _scope.GetServices<IMatchUiLayer>()]);
    }

    private void EndUi()
    {
        _ui.SetMatchLayers([]);
        _commands.EndMatch();
    }

    private void InitializeEcs()
    {
        Ecs.Initialize(_scope.GetServices<IFixedUpdateSystem>(), _scope.GetServices<IFrameUpdateSystem>());
        // The match's, as the systems' commands are: it goes when the match ends.
        EcsCommands.Register(_commands, Ecs.World);
    }

    private void BeginLoading(double now)
    {
        _loading.Initialize(_scope.GetServices<ILoadingTask>());
        _loading.Begin(now);
        // Time that passed while the match started is not the match's: no burst of steps to catch up.
        _accumulator.Restart();
        // A word, not the enum: the log templates format values that are not strings as JSON, in quotes.
        var role = Session.Role switch
        {
            SessionRole.Host => "host",
            SessionRole.Client => "client",
            _ => "offline",
        };
        LogStarted(role, Session.Match.Seed);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Match: started as {Role}, seed {Seed}")]
    private partial void LogStarted(string role, ulong seed);
}
