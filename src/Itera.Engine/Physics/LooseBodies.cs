using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Physics;

/// <summary>
/// Loose bodies: thrown balls made from nothing (<see cref="TryCreateBall"/>) and lifted pieces of voxels
/// (<see cref="VoxelWorld.TryLift"/>) that Bepu moves until they rest, when they go back into the grid
/// (<see cref="Settle"/>): a ball as a sphere filled, a piece as a piece edit. Both hold centimetres, whatever the
/// world's voxels. Bepu sees each body as the convex hull of its piece's
/// blocking voxels (passable foliage rides along but neither collides nor weighs), 2000 kg per cubic metre of voxels,
/// centred on the hull's middle; the piece's pivot is kept relative to that. A piece with hollows (a tree, later) will
/// need several hulls. Each body's piece is also a model (<see cref="VoxelModels"/>), so the renderer draws it where
/// the body is. A body cut off a character (item 4 part D) is made of several models instead
/// (<see cref="TryCreate(ReadOnlySpan{LoosePart}, float, Vector3, float, out BodyHandle)"/>): a compound of one hull
/// per model, with no piece in the edit log, so it never goes back into the grid (<see cref="IsCut"/>); it is removed
/// instead, with the models it owns. It touches only the voxels (<see cref="BodyKind.Cut"/>): not other bodies, thrown
/// pieces, or characters. Such bodies can be joined into a ragdoll (<see cref="TryCreateRagdoll"/>), whose bodies rest,
/// and so go, together. The hull of a part the body shares (a character kind's,
/// whose tree never changes) is kept by its tree, so the next body with that part builds its hull from the hull's own
/// corners (about 100) rather than every voxel box's (about 1,500). Entities hold only the body's handle; this service
/// keeps the rest. Main thread.
/// </summary>
public sealed partial class LooseBodies
{
    /// <summary>Mass of a cubic metre of blocking voxels, in kilograms.</summary>
    public const float DensityKilogramsPerCubicMetre = 2000;

    /// <summary>
    /// The most box corners a lifted piece's hull is built from as they are. The thrown ball (25 cm) has 3,104; the big
    /// throw's (73 cm) 33,512, whose hull took about 60 ms to build and had 1,274 faces to collide with. Past this, the
    /// hull is built from its support points in <see cref="HullDirections"/> directions instead
    /// (<see cref="SupportPoints(Span{Vector3})"/>). Measured 2026-09-28 with the big ball falling down a rough shaft dug in the ground:
    /// 1,044 ms of physics over 47 steps with 162 directions (320 faces), 353 ms over 32 with 42 (80 faces); a true sphere
    /// cost least but, with nothing to stop it rolling, one had not come to rest after 35 s.
    /// </summary>
    public const int MaxHullPoints = 4096;

    /// <summary>How many directions a large piece's hull is sampled in: an icosahedron's corners and edge middles.</summary>
    public const int HullDirections = 42;

    private static readonly Vector3[] s_hullDirections = IcosphereDirections(1);

    /// <summary>
    /// The most corners a character part's hull keeps, and the number of directions a larger one is sampled in
    /// (<see cref="SupportPoints(Span{Vector3})"/>, on the full hull's corners). Measured 2026-09-29 over every build's
    /// parts: the reduced hull lies within 2.4 cm of the full one for every build but the largest, whose 4 cm voxels lose up
    /// to 6.3 cm (its chest: 513 corners down to 132); 42 directions lost up to 16.6 cm. Its hulls had up to 606
    /// corners, and ten of its dead bodies took 5.3 ms a step to collide with the ground, 18 ms after digs under them.
    /// </summary>
    public const int PartHullDirections = 162;

    private static readonly Vector3[] s_partHullDirections = IcosphereDirections(2);

    /// <summary>
    /// How far, in radians, a ragdoll's joint lets its bodies twist about the joint's axis from how they stood. Without a
    /// limit a round limb between two joints (a large body's forearm or shin) turned about its own length for as long as it
    /// lay there, which kept every body it touched awake (2026-09-29).
    /// </summary>
    public const float RagdollTwistRadians = 0.6f;

    /// <summary>A body moving slower than this, in metres per second, and turning slower than <see cref="RestSpin"/> is at rest.</summary>
    public const float RestSpeed = 0.05f;

    /// <summary>A body turning slower than this, in radians per second, and moving slower than <see cref="RestSpeed"/> is at rest.</summary>
    public const float RestSpin = 0.5f;

    private const float Metres = 1f / VoxelConstants.CentimetresPerMetre;

    private readonly PhysicsWorld _physics;
    private readonly VoxelWorld _voxels;
    private readonly VoxelColliders _colliders;
    private readonly VoxelModels _models;
    private readonly MaterialSet _passable;
    private readonly int _maxBodies;
    private readonly ILogger<LooseBodies> _logger;
    private readonly VoxelBoxes _boxes = new();
    private readonly Dictionary<BodyHandle, Loose> _bodies = [];

    // Each thrown ball's piece, by radius and material, made once (VoxelWorld.MakeBall) and shared by every ball of them.
    private readonly Dictionary<(int Radius, byte Material), VoxelPiece> _balls = [];

    // The bodies of thrown balls and lifted pieces, which are few: characters look for them every step among up to 384
    // cut bodies (0.79 ms a step for 300 characters in Release).
    private readonly List<BodyHandle> _pieces = [];
    private readonly Dictionary<int, List<BodyHandle>> _ragdolls = [];
    private readonly Dictionary<int, List<RagdollLink>> _links = [];
    private int _lastRagdoll;
    private readonly Dictionary<SharedPart, SharedHull> _sharedHulls = [];
    private Vector3[] _points = new Vector3[1024];
    private readonly Vector3[] _support = new Vector3[PartHullDirections];
    private Vector3[] _hullCorners = new Vector3[PartHullDirections * 4];

    /// <summary>Bodies made since the start: the last one's <see cref="BornOf"/>.</summary>
    private long _births;

    public LooseBodies(
        PhysicsWorld physics,
        VoxelWorld voxels,
        VoxelColliders colliders,
        VoxelModels models,
        IVoxelPassability passability,
        IOptions<EngineOptions> options,
        ILogger<LooseBodies> logger
    )
    {
        _physics = physics;
        _voxels = voxels;
        _colliders = colliders;
        _models = models;
        _passable = MaterialSet.Passable(passability);
        _maxBodies = options.Value.Physics.MaxLooseBodies;
        _logger = logger;
        colliders.GroupThawed = Rejoin;
    }

    /// <summary>
    /// Below this height, in metres, a body has fallen out of a world of <paramref name="grid"/>: 10 m under its lowest
    /// voxel.
    /// </summary>
    public static float FallLimitMetres(VoxelGrid grid) => grid.ToCentimetres(grid.MinVoxelY) * Metres - 10;

    /// <summary>Loose bodies now.</summary>
    public int Count => _bodies.Count;

    /// <summary>The most loose bodies at once (<c>Engine:Physics:MaxLooseBodies</c>).</summary>
    public int MaxBodies => _maxBodies;

    /// <summary>
    /// Puts a mirror (either <c>TryCreateMirror</c>) with its pivot at <paramref name="pivotMetres"/>, turned by
    /// <paramref name="turn"/>, moving at <paramref name="velocity"/> (which only serves its contacts: Bepu moves it by
    /// that velocity within the step, so draw it from the pose given here, not from Bepu's). Keeps it awake: a sleeping
    /// kinematic body wakes nothing it moves into.
    /// </summary>
    public void MoveMirror(BodyHandle body, Vector3 pivotMetres, Quaternion turn, Vector3 velocity)
    {
        var loose = _bodies[body];
        var reference = _physics.Simulation.Bodies[body];
        reference.Pose = new RigidPose(pivotMetres - Vector3.Transform(loose.PivotOffset, turn), turn);
        reference.Velocity = new BodyVelocity(velocity);
        reference.Awake = true;
    }

    /// <summary>True for a mirror (<see cref="TryCreateMirror(long, Vector3, Quaternion, out BodyHandle)"/>).</summary>
    public bool IsMirror(BodyHandle body) => _physics.Simulation.Bodies[body].Kinematic;

    /// <summary>
    /// Writes where each loose body's piece is now into <paramref name="poses"/> (as many as fit) and returns how many:
    /// for the character mover, which reads bodies as voxels (<see cref="LooseBodyVoxels"/>).
    /// </summary>
    internal int CopyPoses(Span<LoosePose> poses)
    {
        var bodies = _physics.Simulation.Bodies;
        var count = 0;
        foreach (var (handle, loose) in _bodies)
        {
            if (count == poses.Length)
            {
                break;
            }

            // A body cut off a character is not read as voxels: characters pass through it.
            if (loose.Cut)
            {
                continue;
            }

            // In centimetres, as its piece's voxels are (a lift reads its piece a centimetre at a time).
            var pose = bodies[handle].Pose;
            var pivot =
                (pose.Position + Vector3.Transform(loose.PivotOffset, pose.Orientation))
                * VoxelConstants.CentimetresPerMetre;
            poses[count++] = new LoosePose(loose.Voxels, pivot, pose.Orientation);
        }

        return count;
    }

    /// <summary>
    /// True when the bounds of any thrown ball or lifted piece (not a body cut off a character, which characters pass
    /// through) come within <paramref name="distance"/> metres of <paramref name="point"/>.
    /// </summary>
    public bool AnyPieceNear(Vector3 point, float distance)
    {
        var bodies = _physics.Simulation.Bodies;
        foreach (var handle in _pieces)
        {
            var bounds = bodies[handle].BoundingBox;
            var nearest = Vector3.Clamp(point, bounds.Min, bounds.Max);
            if (Vector3.DistanceSquared(nearest, point) <= distance * distance)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The share of upward lift added to a blast's push before it is made a unit again: a blast throws up and out.</summary>
    public const float PushLift = 0.5f;

    /// <summary>
    /// Throws every loose body whose middle lies within <paramref name="radiusMetres"/> of <paramref name="centre"/> away
    /// from it (a blast; mirrors, which their host moves, are left alone): its velocity gains
    /// <paramref name="metresPerSecond"/> times the share of the radius it lies inside, along the way from the centre with
    /// <see cref="PushLift"/> of lift, and it wakes. Returns how many it threw. Allocates nothing.
    /// </summary>
    public int Push(Vector3 centre, float radiusMetres, float metresPerSecond)
    {
        if (!(radiusMetres > 0) || !(metresPerSecond > 0))
        {
            return 0;
        }

        var bodies = _physics.Simulation.Bodies;
        var pushed = 0;
        // Not over _bodies.Keys: its collection is made on first use.
        foreach (var (handle, _) in _bodies)
        {
            var reference = bodies[handle];
            if (reference.Kinematic)
            {
                continue;
            }

            var away = reference.Pose.Position - centre;
            var distance = away.Length();
            if (!(distance < radiusMetres))
            {
                continue;
            }

            var way = distance > 1e-4f ? away / distance : Vector3.UnitY;
            way = Vector3.Normalize(way + new Vector3(0, PushLift, 0));
            reference.Velocity.Linear += way * (metresPerSecond * (1 - distance / radiusMetres));
            reference.Awake = true;
            pushed++;
        }

        return pushed;
    }

    /// <summary>True when <paramref name="body"/> is a loose body of this service.</summary>
    public bool Contains(BodyHandle body) => _bodies.ContainsKey(body);

    /// <summary>The piece a loose body is made of (its lift's sequence); 0 for a thrown ball or a body cut off a character.</summary>
    public long PieceOf(BodyHandle body) => _bodies[body].Piece;

    /// <summary>A thrown ball's radius in centimetres (<see cref="TryCreateBall"/>); 0 for any other body.</summary>
    public int BallRadiusOf(BodyHandle body) => _bodies[body].BallRadius;

    /// <summary>The model that draws a loose body's piece (<see cref="VoxelModels"/>), at level 0 only; -1 for a cut body.</summary>
    public int ModelOf(BodyHandle body) => _bodies[body].Model;

    /// <summary>True for a body cut off a character, which never goes back into the grid.</summary>
    public bool IsCut(BodyHandle body) => _bodies[body].Cut;

    /// <summary>
    /// The models that draw a loose body, each with its box's corner from the body's pivot and its turn, in the body's
    /// axes: one for a piece, several for a body cut off a character.
    /// </summary>
    public ReadOnlySpan<LooseModel> PartsOf(BodyHandle body) => _bodies[body].Models;

    /// <summary>How far a cut body's models are blended towards their tint colour: its character's tint.</summary>
    public float TintOf(BodyHandle body) => _bodies[body].Tint;

    /// <summary>A loose body's mass in kilograms.</summary>
    public float MassOf(BodyHandle body) => _bodies[body].Mass;

    /// <summary>Adds every model a loose body draws to <paramref name="models"/>. Allocates nothing once it has grown.</summary>
    public void CollectModels(HashSet<int> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        foreach (var loose in _bodies.Values)
        {
            foreach (var part in loose.Models)
            {
                models.Add(part.Model);
            }
        }
    }

    /// <summary>The ragdoll the body belongs to (<see cref="TryCreateRagdoll"/>), or 0 for none.</summary>
    public int RagdollOf(BodyHandle body) => _bodies[body].Ragdoll;

    /// <summary>When <paramref name="body"/> was made: a number that grows with each body made, so the oldest is the least.</summary>
    public long BornOf(BodyHandle body) => _bodies[body].Born;

    /// <summary>
    /// True while <paramref name="body"/> is a loose body made at <paramref name="born"/> (<see cref="BornOf"/>; 0 for any):
    /// false once it is removed, also after a new body has taken its handle.
    /// </summary>
    public bool Holds(BodyHandle body, long born) =>
        _bodies.TryGetValue(body, out var loose) && (born == 0 || loose.Born == born);

    /// <summary>Where a loose body's piece pivot is now, in metres.</summary>
    public Vector3 PivotOf(BodyHandle body)
    {
        var pose = _physics.Simulation.Bodies[body].Pose;
        return pose.Position + Vector3.Transform(_bodies[body].PivotOffset, pose.Orientation);
    }

    /// <summary>
    /// The box of a loose body around its pivot, in the body's own axes, in metres: its piece's box, or the box holding
    /// every model of a cut body.
    /// </summary>
    public void BoxOf(BodyHandle body, out Vector3 min, out Vector3 max)
    {
        var loose = _bodies[body];
        min = loose.BoxMin;
        max = loose.BoxMax;
    }

    /// <summary>The box around a loose body in the world, in metres, from the last step.</summary>
    public void BoundsOf(BodyHandle body, out Vector3 min, out Vector3 max)
    {
        var bounds = _physics.Simulation.Bodies[body].BoundingBox;
        min = bounds.Min;
        max = bounds.Max;
    }

    /// <summary>The orientation of a loose body's piece: its turn since it was made or lifted.</summary>
    public Quaternion OrientationOf(BodyHandle body) => _physics.Simulation.Bodies[body].Pose.Orientation;

    /// <summary>
    /// The voxel level the body collides with: <see cref="VoxelColliders.CoarseLevel"/> for a giant's body of 4 cm parts
    /// (when the world streams that level), 0 for any other.
    /// </summary>
    public int CollisionLevel(BodyHandle body) => _physics.Kinds.Level(body.Value);

    /// <summary>
    /// True when the body moves and turns slowly enough to count as resting (or Bepu has put it to sleep); a body of a
    /// ragdoll, when every body of it does. A body frozen until its columns load (<see cref="VoxelColliders"/>) is
    /// waiting, not resting.
    /// </summary>
    public bool IsAtRest(BodyHandle body) => IsResting(body, frozenCounts: false);

    /// <summary>
    /// <see cref="IsAtRest"/>, except that a body frozen until its columns load counts as resting too: it does not move
    /// while frozen. For bodies that are removed after resting rather than put into the grid (those cut off a character),
    /// which would otherwise stay for as long as the player keeps away from them.
    /// </summary>
    public bool IsAtRestOrFrozen(BodyHandle body) => IsResting(body, frozenCounts: true);

    private bool IsResting(BodyHandle body, bool frozenCounts)
    {
        if (_bodies[body].Ragdoll is var ragdoll and > 0 && _ragdolls.TryGetValue(ragdoll, out var members))
        {
            foreach (var member in members)
            {
                if (!IsBodyAtRest(member, frozenCounts))
                {
                    return false;
                }
            }

            return true;
        }

        return IsBodyAtRest(body, frozenCounts);
    }

    /// <summary>True when one body moves and turns slowly enough to count as resting, whatever ragdoll it is in.</summary>
    private bool IsBodyAtRest(BodyHandle body, bool frozenCounts)
    {
        if (_colliders.IsFrozen(body))
        {
            return frozenCounts;
        }

        var reference = _physics.Simulation.Bodies[body];
        if (!reference.Awake)
        {
            return true;
        }

        var velocity = reference.Velocity;
        return velocity.Linear.LengthSquared() < RestSpeed * RestSpeed
            && velocity.Angular.LengthSquared() < RestSpin * RestSpin;
    }

    /// <summary>True when the body is below <see cref="FallLimitMetres"/> of the world's grid.</summary>
    public bool HasFallenOut(BodyHandle body) =>
        _physics.Simulation.Bodies[body].Pose.Position.Y < FallLimitMetres(_voxels.Grid);

    /// <summary>Removes a loose body without putting it back (a lifted piece stays in the log, unused).</summary>
    public void Remove(BodyHandle body) => RemoveBody(body, removeModel: true);

    private void RemoveBody(BodyHandle body, bool removeModel)
    {
        if (!_bodies.Remove(body, out var loose))
        {
            return;
        }

        if (!loose.Cut)
        {
            _pieces.Remove(body);
        }

        var simulation = _physics.Simulation;
        _colliders.Forget(body);
        if (loose.Ragdoll > 0 && _ragdolls.TryGetValue(loose.Ragdoll, out var members))
        {
            members.Remove(body);
            if (members.Count == 0)
            {
                _ragdolls.Remove(loose.Ragdoll);
                _links.Remove(loose.Ragdoll);
            }
        }

        // Bepu removes the body's joints with it.
        simulation.Bodies.Remove(body);
        if (loose.Cut)
        {
            foreach (var model in loose.Owned)
            {
                _models.RemoveAndFree(model);
            }

            simulation.Shapes.RecursivelyRemoveAndDispose(loose.Shape, _physics.Pool);
            return;
        }

        if (removeModel)
        {
            _models.Remove(loose.Model);
        }

        simulation.Shapes.RemoveAndDispose(loose.Shape, _physics.Pool);
    }

    /// <summary>What the service keeps for one loose body.</summary>
    /// <param name="Voxels">The piece (a ball's has sequence 0); none for a cut body.</param>
    /// <param name="PivotOffset">The pivot from the body's centre, in the body's own axes, in metres.</param>
    /// <param name="Shape">The body's convex hull, or a cut body's compound of hulls.</param>
    /// <param name="Mass">Kilograms.</param>
    /// <param name="Model">The model that draws the piece; -1 for a cut body.</param>
    /// <param name="Models">The models that draw the body, placed in it.</param>
    /// <param name="Owned">The models a cut body removes, with their trees, when it goes.</param>
    /// <param name="Cut">True for a body cut off a character.</param>
    /// <param name="Tint">A cut body's tint.</param>
    /// <param name="Ragdoll">The ragdoll it belongs to; 0 for none.</param>
    private readonly record struct Loose(
        VoxelPiece Voxels,
        Vector3 PivotOffset,
        TypedIndex Shape,
        float Mass,
        int Model,
        LooseModel[] Models,
        int[] Owned,
        bool Cut,
        float Tint,
        int Ragdoll
    )
    {
        /// <summary>The lowest corner of the box that holds the body, from its pivot in its axes, in metres.</summary>
        public Vector3 BoxMin { get; init; }

        /// <summary>The highest corner of that box.</summary>
        public Vector3 BoxMax { get; init; }

        /// <summary>When it was made (<see cref="BornOf"/>).</summary>
        public long Born { get; init; }

        /// <summary>A thrown ball's radius in centimetres, which it goes back into the grid as; 0 for any other body.</summary>
        public int BallRadius { get; init; }

        /// <summary>A thrown ball's material.</summary>
        public byte BallMaterial { get; init; }

        public long Piece => Voxels.Sequence;

        public int Radius => Voxels.Radius;
    }
}

/// <summary>
/// A model for a loose body made of several (<see cref="LooseBodies.TryCreate(ReadOnlySpan{LoosePart}, float, Vector3, float, out BodyHandle)"/>):
/// the model, its finest tree (whose blocking voxels make its hull) and box in that tree's voxels, where its box's corner
/// lies in the world (metres) and its turn there, whether the body owns it (removes it and frees its trees when it goes)
/// or shares it (a character kind's part), and the tree's level: 0 (1 cm voxels) for most, 2 (4 cm) for a giant's part.
/// </summary>
public readonly record struct LoosePart(
    int Model,
    ModelTree Tree,
    VoxelCoord Size,
    Vector3 Corner,
    Quaternion Turn,
    bool Owned,
    int Level = 0
);

/// <summary>
/// One of the models that draw a loose body (<see cref="LooseBodies.PartsOf"/>): its box's corner from the body's pivot
/// and its turn, both in the body's axes.
/// </summary>
public readonly record struct LooseModel(int Model, Vector3 Corner, Quaternion Turn);

/// <summary>Where a loose body's piece is: its pivot in level-0 voxels and its turn since it was lifted.</summary>
internal readonly record struct LoosePose(VoxelPiece Piece, Vector3 Pivot, Quaternion Orientation);

/// <summary>
/// A joint of a ragdoll (<see cref="LooseBodies.TryCreateRagdoll"/>): bodies <paramref name="BodyA"/> and
/// <paramref name="BodyB"/> (indices into the ragdoll's bodies) held together at <paramref name="Point"/> (metres, in
/// the world as the parts lie) by a ball socket, and kept within <paramref name="MaxSwingRadians"/> of their angle
/// about <paramref name="Axis"/> (the world direction the child part points along) by a swing limit.
/// </summary>
public readonly record struct RagdollJoint(int BodyA, int BodyB, Vector3 Point, Vector3 Axis, float MaxSwingRadians);

/// <summary>
/// One joint of a ragdoll as Bepu holds it: the two bodies and their ball socket, swing limit, and twist limit, kept so
/// the joint can be put back after a freeze (<see cref="LooseBodies"/>).
/// </summary>
internal readonly record struct RagdollLink(
    BepuPhysics.BodyHandle A,
    BepuPhysics.BodyHandle B,
    BepuPhysics.Constraints.BallSocket Ball,
    BepuPhysics.Constraints.SwingLimit Swing,
    BepuPhysics.Constraints.TwistLimit Twist
);
