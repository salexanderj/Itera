using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;
using BepuUtilities.Memory;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Physics;

/// <summary>Owns the Bepu simulation, its memory pool, and its thread dispatcher.</summary>
public sealed partial class PhysicsWorld : IDisposable
{
    private readonly ILogger<PhysicsWorld> _logger;
    private BufferPool? _pool;
    private ThreadDispatcher? _dispatcher;
    private Simulation? _simulation;

    // Per level, the box of each size up to a cell's edge, made when first asked for.
    private readonly TypedIndex[]?[] _voxelBoxes = new TypedIndex[VoxelConstants.MaxLevel + 1][];
    private readonly bool[]?[] _voxelBoxMade = new bool[VoxelConstants.MaxLevel + 1][];
    private readonly Dictionary<(int X, int Y, int Z), TypedIndex> _largeVoxelBoxes = [];
    private readonly Dictionary<int, TypedIndex> _surfaceBoxes = [];
    private readonly BodyKinds _kinds = new();
    private readonly PhysicsSettings _settings;
    private readonly IntegrationSettings _integration;
    private readonly PhysicsMaterials? _materials;
    private readonly PhysicsLayers? _layers;
    private PhysicsContacts? _contacts;
    private bool _disposed;

    // What the last step ran with, for reading what bodies did to a stand-in in it (PushOn).
    private Vector3 _stepGravity;
    private float _stepLinearDamping;
    private int _stepSubsteps = 1;

    /// <param name="logger">Logger.</param>
    /// <param name="options">
    /// The engine's options (<c>Engine:Physics</c>: threads, substeps, iterations, damping); none for the defaults and a
    /// quarter of the processors.
    /// </param>
    /// <param name="materials">The run's physics materials, sealed as the match begins; none for the engine's alone.</param>
    /// <param name="layers">The run's collision layers, sealed as the match begins; none for the default alone.</param>
    public PhysicsWorld(
        ILogger<PhysicsWorld> logger,
        IOptions<EngineOptions>? options = null,
        PhysicsMaterials? materials = null,
        PhysicsLayers? layers = null
    )
    {
        _logger = logger;
        _materials = materials;
        _layers = layers;
        _settings = options?.Value.Physics ?? new PhysicsSettings();
        _threads = _settings.Threads;
        _integration = new IntegrationSettings(_kinds)
        {
            Gravity = new Vector3(_settings.Gravity.X, _settings.Gravity.Y, _settings.Gravity.Z),
            LinearDamping = _settings.LinearDamping,
            AngularDamping = _settings.AngularDamping,
            Substeps = _settings.Substeps,
        };
    }

    /// <summary>Bepu's worker threads asked for; 0 for <see cref="ThreadCount"/> of the processors.</summary>
    private readonly int _threads;

    /// <summary>The Earth's gravity, the default: 9.81 metres per second squared, down.</summary>
    public static Vector3 DefaultGravity => new(0f, -StandardGravity, 0f);

    /// <summary>The strength of the Earth's gravity, metres per second squared: what <see cref="GravityShare"/> is a share of.</summary>
    public const float StandardGravity = 9.81f;

    /// <summary>The match's gravity on every dynamic body, metres per second squared (<see cref="SetGravity"/>).</summary>
    public Vector3 Gravity => _integration.Gravity;

    /// <summary>
    /// The match's gravity's strength as a share of the Earth's (<see cref="StandardGravity"/>): exactly 1 at the default.
    /// Characters fall by their own gravity times it.
    /// </summary>
    public float GravityShare => _integration.Gravity.Length() / StandardGravity;

    /// <summary>The share of its linear velocity a body loses each second (<see cref="SetDamping"/>).</summary>
    public float LinearDamping => _integration.LinearDamping;

    /// <summary>The share of its angular velocity a body loses each second (<see cref="SetDamping"/>).</summary>
    public float AngularDamping => _integration.AngularDamping;

    /// <summary>The bounces the engine set since the match began (<see cref="PhysicsMaterial.Bounciness"/>).</summary>
    public long Bounces => _contacts?.Bounces ?? 0;

    /// <summary>
    /// The pairs with a side marked <see cref="PhysicsMarks.ContactEvents"/> that began or stopped touching in the last
    /// step (those of sides removed since the step before first, then by pair), for the systems of the next step to read.
    /// Main thread.
    /// </summary>
    public ReadOnlySpan<ImpactEvent> ImpactEvents => _contacts is null ? default : _contacts.Events;

    /// <summary>
    /// The friction of <paramref name="material"/> (<see cref="PhysicsMaterial.Friction"/>); 1, the default's, for a world
    /// with no registry or a material it does not have.
    /// </summary>
    public float FrictionOf(PhysicsMaterialId material) =>
        _materials is { } materials && material.Value < materials.Count ? materials[material].Friction : 1;

    /// <summary>The run's physics materials, if it has a registry (a test's world may have none).</summary>
    internal PhysicsMaterials? Materials => _materials;

    /// <summary>Contacts not followed because there were too many in a step.</summary>
    public long ContactsDropped => _contacts?.Dropped ?? 0;

    /// <summary>The solver's substeps in each step (<see cref="SetSolver"/>).</summary>
    public int Substeps => Simulation.Solver.SubstepCount;

    /// <summary>The solver's velocity iterations in each substep (<see cref="SetSolver"/>).</summary>
    public int VelocityIterations => Simulation.Solver.VelocityIterationCount;

    /// <summary>The simulation. Throws before <see cref="Initialize"/>.</summary>
    public Simulation Simulation =>
        _simulation ?? throw new InvalidOperationException("The physics world has not been initialised.");

    /// <summary>What each body stands for, which decides what it touches.</summary>
    internal BodyKinds Kinds => _kinds;

    /// <summary>What a body stands for: a thrown ball or lifted piece, a stand-in, or a body cut off a character.</summary>
    public BodyKind KindOf(BodyHandle body) => _kinds[body.Value];

    /// <summary>True between <see cref="Initialize"/> and <see cref="Dispose"/>.</summary>
    internal bool IsInitialized => _pool is not null;

    /// <summary>The pool Bepu shapes and statics take their memory from. Throws before <see cref="Initialize"/>.</summary>
    internal BufferPool Pool =>
        _pool ?? throw new InvalidOperationException("The physics world has not been initialised.");

    /// <summary>
    /// The shape of a box of <paramref name="x"/> by <paramref name="y"/> by <paramref name="z"/> level-0 voxels. Each
    /// size is added to the simulation's shapes once and kept, so compounds of voxel boxes share their children and
    /// removing a compound frees only the compound. Main thread.
    /// </summary>
    internal TypedIndex VoxelBox(int x, int y, int z) => VoxelBox(VoxelGrid.Unit, 0, x, y, z);

    /// <summary>
    /// <see cref="VoxelBox(int, int, int)"/> with the size in voxels of <paramref name="level"/> of
    /// <paramref name="grid"/> (4 cm at level 2 of a 1 cm grid). Sizes up to a collision cell's edge along every axis are
    /// kept in a table per level, larger ones by size; the tables are for one grid, the run's world's. Main thread.
    /// </summary>
    internal TypedIndex VoxelBox(VoxelGrid grid, int level, int x, int y, int z)
    {
        var metres = grid.EdgeMetres(0);
        const int Small = 1 << VoxelConstants.CellShift;
        if (x <= Small && y <= Small && z <= Small)
        {
            var boxes = _voxelBoxes[level] ??= new TypedIndex[Small * Small * Small];
            var made = _voxelBoxMade[level] ??= new bool[boxes.Length];
            var slot = (x - 1) + Small * ((y - 1) + Small * (z - 1));
            if (!made[slot])
            {
                boxes[slot] = AddVoxelBox(x << level, y << level, z << level, metres);
                made[slot] = true;
            }

            return boxes[slot];
        }

        ref var large = ref CollectionsMarshal.GetValueRefOrAddDefault(
            _largeVoxelBoxes,
            (x << level, y << level, z << level),
            out var exists
        );
        if (!exists)
        {
            large = AddVoxelBox(x << level, y << level, z << level, metres);
        }

        return large;
    }

    public void Initialize()
    {
        _pool = new BufferPool();
        var threadCount = _threads > 0 ? _threads : ThreadCount(Environment.ProcessorCount);
        _dispatcher = new ThreadDispatcher(threadCount);
        // Never the (int, int) tuple to SolveDescription: in Bepu 2.5.0-beta.29 it swaps its two values.
        _contacts = new PhysicsContacts(threadCount);
        _triggers = new PhysicsTriggers(threadCount);
        _simulation = Simulation.Create(
            _pool,
            new NarrowPhaseCallbacks(_kinds, _contacts, _triggers),
            new PoseIntegratorCallbacks(_integration),
            new SolveDescription(_settings.VelocityIterations, _settings.Substeps)
        );
        _integration.Bodies = _simulation.Bodies;
        _contacts.Bodies = _simulation.Bodies;
        _contacts.Statics = _simulation.Statics;
        _kinds.Contacts = _materials?.Seal() ?? ContactMaterial.DefaultTable;
        _kinds.Touches = _layers?.Seal() ?? BodyKinds.NewTouches();
        _kinds.CharacterLayer = _layers?.CharacterLayer ?? PhysicsLayerId.Default;
        _kinds.VoxelLayer = _layers?.VoxelLayer ?? PhysicsLayerId.Default;

        LogInitialized(threadCount);
    }

    /// <summary>
    /// Bepu's worker threads for <paramref name="processors"/> logical processors: a quarter of them, 1 to 6. A pile of
    /// 238 cut pieces stepped about twice as fast on 4 threads as on 14 of a 16-thread processor (2026-09-27): the step's
    /// stages are short, and more threads spend the time waiting for each other, while the voxel workers take half the
    /// processors.
    /// </summary>
    public static int ThreadCount(int processors) => Math.Clamp(processors / 4, 1, 6);

    /// <summary>Advances the simulation by one fixed step.</summary>
    public void Step(float stepSeconds)
    {
        _stepGravity = _integration.Gravity;
        _stepLinearDamping = _integration.LinearDamping;
        _stepSubsteps = _integration.Substeps;
        _contacts!.BeginStep(stepSeconds, _stepGravity);
        _triggers!.BeginStep();
        Simulation.Timestep(stepSeconds, _dispatcher);
        _contacts.EndStep(Simulation, _kinds);
        EndTriggerStep();
        CheckRecords();
    }

    /// <summary>
    /// Sets the solver's velocity iterations in each substep (1 to <see cref="PhysicsSettings.MostVelocityIterations"/>)
    /// and its substeps in each step (1 to <see cref="PhysicsSettings.MostSubsteps"/>), from the next step on: more of
    /// either keeps fast, stacked, or jointed bodies steadier, at more cost. Main thread, between steps.
    /// </summary>
    public void SetSolver(int velocityIterations, int substeps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(velocityIterations, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(velocityIterations, PhysicsSettings.MostVelocityIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(substeps, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(substeps, PhysicsSettings.MostSubsteps);
        var solver = Simulation.Solver;
        if (substeps != solver.SubstepCount)
        {
            // The impulses kept to start the next solve are a substep's: kept as a second's, so the bodies do not jump.
            solver.ScaleAccumulatedImpulses(solver.SubstepCount / (float)substeps);
        }

        solver.VelocityIterationCount = velocityIterations;
        solver.SubstepCount = substeps;
        _integration.Substeps = substeps;
    }

    /// <summary>
    /// Sets the match's gravity on every dynamic body (metres per second squared, each part finite), from the next step
    /// on, and wakes every sleeping body, so none rests where it no longer would. Main thread, between steps.
    /// </summary>
    public void SetGravity(Vector3 gravity)
    {
        if (!float.IsFinite(gravity.X + gravity.Y + gravity.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(gravity), gravity, "Gravity is finite.");
        }

        _integration.Gravity = gravity;
        WakeAll();
    }

    /// <summary>Wakes every sleeping body.</summary>
    private void WakeAll()
    {
        var sets = Simulation.Bodies.Sets;
        for (var i = 1; i < sets.Length; i++)
        {
            if (sets[i].Allocated)
            {
                Simulation.Awakener.AwakenSet(i);
            }
        }
    }

    /// <summary>
    /// Sets the share of the match's gravity <paramref name="body"/> falls by (1 for all of it, 0 to float, negative to
    /// rise; finite), and wakes it. Main thread, between steps.
    /// </summary>
    public void SetGravityScale(BodyHandle body, float scale)
    {
        if (!float.IsFinite(scale))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "A gravity scale is finite.");
        }

        _kinds.SetGravityScale(body, scale);
        var reference = Simulation.Bodies[body];
        reference.Awake = true;
    }

    /// <summary>The share of the match's gravity <paramref name="body"/> falls by.</summary>
    public float GravityScaleOf(BodyHandle body) => _kinds.GravityScale(body.Value);

    /// <summary>
    /// Sets the shares of their speed (<paramref name="linear"/>) and spin (<paramref name="angular"/>) bodies lose each
    /// second, each 0 to 1 (1 stops every body at each step), from the next step on. Main thread, between steps.
    /// </summary>
    public void SetDamping(float linear, float angular)
    {
        if (!(linear is >= 0 and <= 1) || !(angular is >= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(linear), "Each damping lies between 0 and 1.");
        }

        _integration.LinearDamping = linear;
        _integration.AngularDamping = angular;
    }

    /// <summary>
    /// In Debug builds, that no body this world recorded was removed past <see cref="RemoveBody"/>: its record would pass
    /// to the next body that takes its handle.
    /// </summary>
    [Conditional("DEBUG")]
    private void CheckRecords()
    {
        var stale = _kinds.FirstStale(Simulation.Bodies);
        if (stale >= 0)
        {
            throw new InvalidOperationException(
                $"Body {stale} was removed without PhysicsWorld.RemoveBody: its record would pass to the next body that takes its handle."
            );
        }
    }

    /// <summary>
    /// Moves every body (awake, asleep, or kinematic) and every static by -<paramref name="metres"/>, for an origin that
    /// moved by <paramref name="metres"/> (whole metres): what was at a local place is then at that place less them.
    /// Nothing wakes or changes but the poses and the broad phase's bounds; velocities, joints, and contacts are relative,
    /// and keep. Main thread, between steps. Returns how many bodies and statics moved.
    /// </summary>
    public (int Bodies, int Statics) Shift(Vector3 metres)
    {
        _contacts?.Shift(metres);
        var bodies = Simulation.Bodies;
        var moved = 0;
        for (var index = 0; index < bodies.Sets.Length; index++)
        {
            ref var set = ref bodies.Sets[index];
            if (!set.Allocated)
            {
                continue;
            }

            for (var i = 0; i < set.Count; i++)
            {
                set.DynamicsState[i].Motion.Pose.Position -= metres;
                bodies.UpdateBounds(set.IndexToHandle[i]);
            }

            moved += set.Count;
        }

        var statics = Simulation.Statics;
        for (var i = 0; i < statics.Count; i++)
        {
            statics[i].Pose.Position -= metres;
            statics.UpdateBounds(statics.IndexToHandle[i]);
        }

        return (moved, statics.Count);
    }

    /// <summary>
    /// Adds a character's stand-in: an upright Bepu cylinder of the character's size and mass that never tips or sleeps,
    /// with its bottom at <paramref name="feet"/>. It touches thrown balls and lifted pieces only, not bodies cut off
    /// characters (<see cref="BodyKind.StandIn"/>): it pushes them and they push it. Place it every step with
    /// <see cref="PlaceStandIn"/> and read the push with <see cref="PushOn"/>.
    /// </summary>
    public BodyHandle AddStandIn(Vector3 feet, float radiusMetres, float heightMetres, float massKilograms)
    {
        var cylinder = new Cylinder(radiusMetres, heightMetres);
        var inertia = cylinder.ComputeInertia(massKilograms);
        inertia.InverseInertiaTensor = default;
        var body = Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(feet + new Vector3(0, heightMetres / 2, 0)),
                inertia,
                new CollidableDescription(Simulation.Shapes.Add(cylinder), 0.1f),
                new BodyActivityDescription(-1)
            )
        );
        _kinds.Set(body, BodyKind.StandIn, layer: _kinds.CharacterLayer);
        return body;
    }

    /// <summary>
    /// Puts a stand-in with its middle at <paramref name="centre"/>, moving at <paramref name="velocity"/> (the velocity its
    /// character wanted this step, before walls stopped it, so walking into a body pushes it), before a step.
    /// </summary>
    public void PlaceStandIn(BodyHandle body, Vector3 centre, Vector3 velocity)
    {
        var reference = Simulation.Bodies[body];
        reference.Pose.Position = centre;
        reference.Velocity.Linear = velocity;
        reference.Velocity.Angular = default;
        reference.Awake = true;
        reference.UpdateBounds();
    }

    /// <summary>
    /// The change of velocity bodies gave a stand-in in the last step of <paramref name="stepSeconds"/>: its velocity less
    /// the velocity it was placed with, after the gravity (times the stand-in's share) and damping that step ran with
    /// (once a substep while bodies touched it, as Bepu integrates a body with constraints). Times the character's mass,
    /// it is the hit's impulse.
    /// </summary>
    public Vector3 PushOn(BodyHandle body, Vector3 placedVelocity, float stepSeconds)
    {
        var reference = Simulation.Bodies[body];
        var gravity = _stepGravity * _kinds.GravityScale(body.Value);
        var keep = Math.Clamp(1 - _stepLinearDamping, 0, 1);
        Vector3 expected;
        if (_stepSubsteps > 1 && reference.Constraints.Count > 0)
        {
            // v_S = v0 d^dt + g h (d^h + d^2h + ... + d^Sh), h = dt / S.
            var substep = stepSeconds / _stepSubsteps;
            var each = MathF.Pow(keep, substep);
            var power = 1f;
            var sum = 0f;
            for (var k = 0; k < _stepSubsteps; k++)
            {
                power *= each;
                sum += power;
            }

            expected = placedVelocity * MathF.Pow(keep, stepSeconds) + gravity * (substep * sum);
        }
        else
        {
            expected = (placedVelocity + gravity * stepSeconds) * MathF.Pow(keep, stepSeconds);
        }

        return reference.Velocity.Linear - expected;
    }

    /// <summary>Removes a stand-in and its shape.</summary>
    public void RemoveStandIn(BodyHandle body)
    {
        var shape = Simulation.Bodies[body].Collidable.Shape;
        RemoveBody(body);
        Simulation.Shapes.Remove(shape);
    }

    /// <summary>A box of (x, y, z) level-0 voxels of <paramref name="metres"/> each.</summary>
    private TypedIndex AddVoxelBox(int x, int y, int z, float metres) =>
        Simulation.Shapes.Add(new Box(x * metres, y * metres, z * metres));

    /// <summary>
    /// The shape of a box a level-0 voxel of <paramref name="grid"/> across and <paramref name="millimetres"/> high: the
    /// top of a column under the ground's surface where its density puts it (<see cref="VoxelColliders"/>). Each height
    /// is added once and kept. Main thread.
    /// </summary>
    internal TypedIndex SurfaceBox(VoxelGrid grid, int millimetres)
    {
        ref var box = ref CollectionsMarshal.GetValueRefOrAddDefault(_surfaceBoxes, millimetres, out var exists);
        if (!exists)
        {
            var edge = grid.EdgeMetres(0);
            box = Simulation.Shapes.Add(new Box(edge, millimetres / 1000f, edge));
        }

        return box;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _simulation?.Dispose();
        _simulation = null;
        _dispatcher?.Dispose();
        _dispatcher = null;
        _pool?.Clear();
        _pool = null;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Physics simulation created: 0 bodies, {Threads} worker thread(s)"
    )]
    private partial void LogInitialized(int threads);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Physics: the trigger zones' buffers were full ({Pairs} overlaps, at least {PerWorker} a worker thread); overlaps past them were dropped and read as left and entered again (once a match)"
    )]
    private partial void LogTriggerDrops(int pairs, int perWorker);
}
