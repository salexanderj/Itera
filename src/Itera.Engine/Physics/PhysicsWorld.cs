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
    private bool _disposed;

    /// <param name="logger">Logger.</param>
    /// <param name="options">The engine's options, for <c>Engine:Physics:Threads</c>; none for the processors' count.</param>
    public PhysicsWorld(ILogger<PhysicsWorld> logger, IOptions<EngineOptions>? options = null)
    {
        _logger = logger;
        _threads = options?.Value.Physics.Threads ?? 0;
    }

    /// <summary>Bepu's worker threads asked for; 0 for <see cref="ThreadCount"/> of the processors.</summary>
    private readonly int _threads;

    /// <summary>Gravity applied to every dynamic body, in metres per second squared.</summary>
    public static Vector3 Gravity => new(0f, -9.81f, 0f);

    /// <summary>The share of its linear velocity a body loses each second (<see cref="PoseIntegratorCallbacks"/>).</summary>
    public const float LinearDamping = 0.03f;

    /// <summary>The simulation. Throws before <see cref="Initialize"/>.</summary>
    public Simulation Simulation =>
        _simulation ?? throw new InvalidOperationException("The physics world has not been initialised.");

    /// <summary>What each body stands for, which decides what it touches.</summary>
    internal BodyKinds Kinds => _kinds;

    /// <summary>What a body stands for: a thrown ball or lifted piece, a stand-in, or a body cut off a character.</summary>
    public BodyKind KindOf(BodyHandle body) => _kinds[body.Value];

    /// <summary>The pool Bepu shapes and statics take their memory from. Throws before <see cref="Initialize"/>.</summary>
    /// <summary>True between <see cref="Initialize"/> and <see cref="Dispose"/>.</summary>
    internal bool IsInitialized => _pool is not null;

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
        _simulation = Simulation.Create(
            _pool,
            new NarrowPhaseCallbacks(_kinds),
            new PoseIntegratorCallbacks(Gravity, LinearDamping),
            new SolveDescription(8, 1)
        );

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
        Simulation.Timestep(stepSeconds, _dispatcher);
    }

    /// <summary>
    /// Moves every body (awake, asleep, or kinematic) and every static by -<paramref name="metres"/>, for an origin that
    /// moved by <paramref name="metres"/> (whole metres): what was at a local place is then at that place less them.
    /// Nothing wakes or changes but the poses and the broad phase's bounds; velocities, joints, and contacts are relative,
    /// and keep. Main thread, between steps. Returns how many bodies and statics moved.
    /// </summary>
    public (int Bodies, int Statics) Shift(Vector3 metres)
    {
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
        _kinds.Set(body, BodyKind.StandIn);
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
    /// the velocity it was placed with, after gravity and damping. Times the character's mass, it is the hit's impulse.
    /// </summary>
    public Vector3 PushOn(BodyHandle body, Vector3 placedVelocity, float stepSeconds)
    {
        var expected = (placedVelocity + Gravity * stepSeconds) * MathF.Pow(1 - LinearDamping, stepSeconds);
        return Simulation.Bodies[body].Velocity.Linear - expected;
    }

    /// <summary>Removes a stand-in and its shape.</summary>
    public void RemoveStandIn(BodyHandle body)
    {
        var shape = Simulation.Bodies[body].Collidable.Shape;
        Simulation.Bodies.Remove(body);
        Simulation.Shapes.Remove(shape);
        _kinds.Set(body, BodyKind.Loose);
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
}
