using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;

namespace Itera.Engine.Physics;

/// <summary>Whether a pair began touching or stopped.</summary>
public enum ImpactChange : byte
{
    /// <summary>It began touching in the last step.</summary>
    Began = 0,

    /// <summary>It stopped touching in the last step, or one side was removed.</summary>
    Ended = 1,
}

/// <summary>What a side of an impact is.</summary>
public enum ImpactSideKind : byte
{
    /// <summary>A body (<see cref="ImpactSide.Body"/>).</summary>
    Body = 0,

    /// <summary>A game's static (<see cref="ImpactSide.Static"/>).</summary>
    Static = 1,

    /// <summary>The voxels: every voxel static counts as one.</summary>
    Voxels = 2,
}

/// <summary>A side of an impact: a body or static by its handle and which of those that took the handle it is, or the voxels.</summary>
public readonly record struct ImpactSide(ImpactSideKind Kind, int Handle, long Born)
{
    /// <summary>The side's body, when it is one.</summary>
    public BodyHandle Body => new(Handle);

    /// <summary>The side's static, when it is one.</summary>
    public StaticHandle Static => new(Handle);
}

/// <summary>
/// A pair with a side marked <see cref="PhysicsMarks.ContactEvents"/> that began or stopped touching in the last physics
/// step. Its sides are in a fixed order (bodies by handle, then statics, then the voxels).
/// </summary>
/// <param name="Change">Began or ended.</param>
/// <param name="A">One side.</param>
/// <param name="B">The other.</param>
/// <param name="Point">Began: where they met (local to the match's origin, moved with it).</param>
/// <param name="Normal">Began: the contact's normal, from B towards A.</param>
/// <param name="Speed">Began: the speed they came together at there, metres a second (0 or more).</param>
/// <param name="MaterialA">Began: A's physics material.</param>
/// <param name="MaterialB">Began: B's physics material.</param>
/// <param name="Removed">Ended: because a side was removed.</param>
public readonly record struct ImpactEvent(
    ImpactChange Change,
    ImpactSide A,
    ImpactSide B,
    Vector3 Point,
    Vector3 Normal,
    float Speed,
    PhysicsMaterialId MaterialA,
    PhysicsMaterialId MaterialB,
    bool Removed
);

/// <summary>
/// A contact the narrow phase saw touching in a step: the two sides as Bepu paired them, and as the engine follows them
/// (<see cref="SideA"/>, <see cref="SideB"/>, the pair's <see cref="Key"/>), the deepest contact's depth, normal (from B
/// to A), place (from A's centre, B's centre from A's, and the point, local), the speed they came together at there as
/// they met (their speed before the step, and what the match's gravity adds or takes over the gap between them), the
/// bounciness of the bouncier side, and whether a side reports its impacts. Ordered by pair.
/// </summary>
internal struct ContactRecord : IComparable<ContactRecord>
{
    public CollidableReference A;
    public CollidableReference B;
    public uint SideA;
    public uint SideB;
    public ulong Key;
    public float Depth;
    public Vector3 Normal;
    public Vector3 OffsetA;
    public Vector3 OffsetB;
    public Vector3 Point;
    public float Approach;
    public float Bounciness;
    public bool Reports;

    public readonly int CompareTo(ContactRecord other) => Key.CompareTo(other.Key);
}

/// <summary>
/// The contacts of pairs the engine follows, found by the narrow phase on Bepu's worker threads (each worker into a
/// buffer of its own, <see cref="Record"/>) and taken on the main thread after the step (<see cref="EndStep"/>): sorted by
/// pair, so the result does not depend on the threads below the capacities, and set against the last step's to find the
/// pairs that began and stopped touching. A pair is two bodies, or a body and a static; every voxel static counts as one,
/// the voxel ground (<see cref="VoxelGround"/>), so a body that rolls from one voxel cell's static into the next has not
/// begun touching anything; a pair that is no longer met only because its bodies sleep (Bepu does not test sleeping
/// bodies against statics or each other) still touches. It follows the pairs with a bouncy side or a side marked
/// <see cref="PhysicsMarks.ContactEvents"/>. A bouncy pair that begins touching at <see cref="MinimumBounceSpeed"/> or
/// more is given a separating speed of its bounciness times the speed it came together at (Bepu has no coefficient of
/// restitution; its contacts stop the pair, and the engine pushes it apart), at the contact, by the pair's masses and
/// inertias. A marked pair gives an <see cref="ImpactEvent"/> as it begins and as it ends (<see cref="Events"/>; one for
/// a side removed since the step before, first). Fixed capacities; a contact or event past them is dropped and counted.
/// Allocates nothing after it is made.
/// </summary>
internal sealed class PhysicsContacts
{
    /// <summary>The least contacts a worker records in a step; one worker records <see cref="MostPairs"/>.</summary>
    public const int LeastPerWorker = 1024;

    /// <summary>The pairs followed at once.</summary>
    public const int MostPairs = 4096;

    /// <summary>The side of a pair that stands for every voxel static.</summary>
    public const uint VoxelGround = uint.MaxValue;

    /// <summary>Marks a static's handle in a pair's side.</summary>
    private const uint StaticBit = 1u << 31;

    /// <summary>The least speed, metres a second, a pair comes together at to bounce: slower ones rest or roll.</summary>
    public const float MinimumBounceSpeed = 0.5f;

    /// <summary>How far apart, metres, a pair may be at the step's end, as its speeds foretell it, and count as touching.</summary>
    public const float TouchingMetres = 0.001f;

    /// <summary>The events a step gives at most.</summary>
    public const int MostEvents = 2 * MostPairs;

    private readonly int _perWorker;
    private readonly ContactRecord[][] _workers;
    private readonly int[] _counts;
    private readonly ContactRecord[] _pairs = new ContactRecord[MostPairs];
    private ulong[] _previous = new ulong[MostPairs];
    private bool[] _previousReports = new bool[MostPairs];
    private ulong[] _next = new ulong[MostPairs];
    private bool[] _nextReports = new bool[MostPairs];
    private int _previousCount;
    private readonly ImpactEvent[] _events = new ImpactEvent[MostEvents];
    private int _eventCount;
    private readonly ImpactEvent[] _pending = new ImpactEvent[MostPairs];
    private int _pendingCount;
    private long _dropped;

    public PhysicsContacts(int workers)
    {
        _perWorker = Math.Max(LeastPerWorker, MostPairs / workers);
        _workers = new ContactRecord[workers][];
        for (var i = 0; i < workers; i++)
        {
            _workers[i] = new ContactRecord[_perWorker];
        }

        _counts = new int[workers];
        // The sort makes its helper at its first use: here, not in a step.
        _pairs.AsSpan(0, 2).Sort();
    }

    /// <summary>The simulation's bodies, for their velocities and places as the narrow phase meets them.</summary>
    public Bodies? Bodies { get; set; }

    /// <summary>The simulation's statics, for their places as the narrow phase meets them.</summary>
    public Statics? Statics { get; set; }

    /// <summary>The last step's impact events.</summary>
    public ReadOnlySpan<ImpactEvent> Events => _events.AsSpan(0, _eventCount);

    /// <summary>The step's length, seconds: a pair counts as touching when it would meet within it.</summary>
    public float StepSeconds { get; private set; }

    /// <summary>The match's gravity in the step: what it adds to a pair's speed over the gap before they meet.</summary>
    public Vector3 Gravity { get; private set; }

    /// <summary>Contacts and events dropped because a buffer was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Bounces the engine set since the match began.</summary>
    public long Bounces { get; private set; }

    /// <summary>Before a step of <paramref name="stepSeconds"/> under <paramref name="gravity"/>: the workers' buffers emptied. Main thread.</summary>
    public void BeginStep(float stepSeconds, Vector3 gravity)
    {
        StepSeconds = stepSeconds;
        Gravity = gravity;
        Array.Clear(_counts);
    }

    /// <summary>Keeps a contact seen in this step. A Bepu worker thread, <paramref name="worker"/> its own.</summary>
    public void Record(int worker, in ContactRecord contact)
    {
        var count = _counts[worker];
        if (count == _perWorker)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _workers[worker][count] = contact;
        _counts[worker] = count + 1;
    }

    /// <summary>
    /// After the step: gathers the workers' contacts by pair; bounces each bouncy pair that began touching; gives the
    /// events of marked pairs that began and ended (after those of sides removed since the step before); and keeps the
    /// pairs, those only asleep among them, for the next step. Main thread.
    /// </summary>
    public void EndStep(Simulation simulation, BodyKinds kinds)
    {
        var count = 0;
        for (var w = 0; w < _workers.Length; w++)
        {
            var take = Math.Min(_counts[w], MostPairs - count);
            if (take < _counts[w])
            {
                Interlocked.Add(ref _dropped, _counts[w] - take);
            }

            _workers[w].AsSpan(0, take).CopyTo(_pairs.AsSpan(count));
            count += take;
        }

        var pairs = _pairs.AsSpan(0, count);
        pairs.Sort();
        var kept = 0;
        for (var i = 0; i < pairs.Length; i++)
        {
            // One a pair: the deepest (of a body on two voxel cells, the one under it, not the next cell's edge).
            if (kept > 0 && pairs[kept - 1].Key == pairs[i].Key)
            {
                if (pairs[i].Depth > pairs[kept - 1].Depth)
                {
                    pairs[kept - 1] = pairs[i];
                }

                continue;
            }

            pairs[kept++] = pairs[i];
        }

        _eventCount = 0;
        for (var i = 0; i < _pendingCount; i++)
        {
            Add(in _pending[i]);
        }

        _pendingCount = 0;
        // Both sets are sorted: walked together, a pair only in the new one began, one only in the old one ended unless
        // its bodies only sleep.
        int a = 0,
            b = 0,
            next = 0;
        while (a < _previousCount || b < kept)
        {
            if (b == kept || (a < _previousCount && _previous[a] < pairs[b].Key))
            {
                var key = _previous[a];
                if (Asleep(key, simulation.Bodies))
                {
                    Keep(ref next, key, _previousReports[a]);
                }
                else if (_previousReports[a])
                {
                    Add(Ended(key, kinds, false));
                }

                a++;
                continue;
            }

            ref readonly var pair = ref pairs[b];
            if (a == _previousCount || pair.Key < _previous[a])
            {
                if (pair.Bounciness > 0 && pair.Approach >= MinimumBounceSpeed && Bounce(simulation, in pair))
                {
                    Bounces++;
                }

                if (pair.Reports)
                {
                    Add(Began(in pair, kinds));
                }
            }
            else
            {
                a++;
            }

            Keep(ref next, pair.Key, pair.Reports);
            b++;
        }

        (_previous, _next) = (_next, _previous);
        (_previousReports, _nextReports) = (_nextReports, _previousReports);
        _previousCount = next;
    }

    /// <summary>Moves the events' points by -<paramref name="metres"/>, for an origin that moved by them. Main thread.</summary>
    public void Shift(Vector3 metres)
    {
        for (var i = 0; i < _eventCount; i++)
        {
            _events[i] = _events[i] with { Point = _events[i].Point - metres };
        }
    }

    /// <summary>
    /// Forgets every pair <paramref name="side"/> was in (<see cref="BodySide"/>, <see cref="StaticSide"/>), which is
    /// being removed (its record still kept), so a body or static that takes its handle next has begun touching nothing
    /// yet; a marked pair gives a removed <see cref="ImpactChange.Ended"/> at the next step's events. Main thread, between
    /// steps.
    /// </summary>
    public void Forget(uint side, BodyKinds kinds)
    {
        var kept = 0;
        for (var i = 0; i < _previousCount; i++)
        {
            var key = _previous[i];
            if ((uint)(key >> 32) != side && (uint)key != side)
            {
                _previous[kept] = key;
                _previousReports[kept++] = _previousReports[i];
            }
            else if (_previousReports[i])
            {
                if (_pendingCount < _pending.Length)
                {
                    _pending[_pendingCount++] = Ended(key, kinds, true);
                }
                else
                {
                    Interlocked.Increment(ref _dropped);
                }
            }
        }

        _previousCount = kept;
    }

    /// <summary>A body's side of a pair, whether it moves or is frozen.</summary>
    public static uint BodySide(BodyHandle body) => (uint)body.Value;

    /// <summary>A static's side of a pair; a voxel static's is <see cref="VoxelGround"/> (the narrow phase's choice).</summary>
    public static uint StaticSide(StaticHandle handle) => StaticBit | (uint)handle.Value;

    /// <summary>The pair of <paramref name="a"/> and <paramref name="b"/>, either way round.</summary>
    public static ulong KeyOf(uint a, uint b) => a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

    /// <summary>True when every body of the pair <paramref name="key"/> sleeps (Bepu no longer meets it).</summary>
    private static bool Asleep(ulong key, Bodies bodies) =>
        SideAsleep((uint)(key >> 32), bodies) && SideAsleep((uint)key, bodies);

    private static bool SideAsleep(uint side, Bodies bodies)
    {
        if ((side & StaticBit) != 0)
        {
            return true;
        }

        var body = new BodyHandle((int)side);
        return bodies.BodyExists(body) && !bodies[body].Awake;
    }

    private void Keep(ref int next, ulong key, bool reports)
    {
        _next[next] = key;
        _nextReports[next++] = reports;
    }

    /// <summary>The event of a marked pair that began: its sides in the key's order, the normal from the second.</summary>
    private static ImpactEvent Began(in ContactRecord pair, BodyKinds kinds)
    {
        var swap = pair.SideA > pair.SideB;
        var first = swap ? pair.B : pair.A;
        var second = swap ? pair.A : pair.B;
        return new ImpactEvent(
            ImpactChange.Began,
            SideOf(swap ? pair.SideB : pair.SideA, kinds),
            SideOf(swap ? pair.SideA : pair.SideB, kinds),
            pair.Point,
            swap ? -pair.Normal : pair.Normal,
            MathF.Max(pair.Approach, 0),
            MaterialOf(first, kinds),
            MaterialOf(second, kinds),
            false
        );
    }

    private static ImpactEvent Ended(ulong key, BodyKinds kinds, bool removed) =>
        new(
            ImpactChange.Ended,
            SideOf((uint)(key >> 32), kinds),
            SideOf((uint)key, kinds),
            default,
            default,
            0,
            default,
            default,
            removed
        );

    private static ImpactSide SideOf(uint side, BodyKinds kinds)
    {
        if (side == VoxelGround)
        {
            return new ImpactSide(ImpactSideKind.Voxels, -1, 0);
        }

        if ((side & StaticBit) != 0)
        {
            var handle = (int)(side & ~StaticBit);
            return new ImpactSide(ImpactSideKind.Static, handle, kinds.Static(handle).Born);
        }

        return new ImpactSide(ImpactSideKind.Body, (int)side, kinds.Born((int)side));
    }

    private static PhysicsMaterialId MaterialOf(CollidableReference collidable, BodyKinds kinds) =>
        collidable.Mobility == CollidableMobility.Static
            ? kinds.Static(collidable.StaticHandle.Value).Material
            : kinds.Body(collidable.BodyHandle.Value).Material;

    private void Add(in ImpactEvent change)
    {
        if (_eventCount == _events.Length)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _events[_eventCount++] = change;
    }

    /// <summary>
    /// Pushes the pair apart at the contact until it separates at its bounciness times its approach, by the impulse its
    /// masses and inertias take (a static or kinematic side takes none). True when it pushed.
    /// </summary>
    private static bool Bounce(Simulation simulation, in ContactRecord pair)
    {
        var normal = pair.Normal;
        var offsetB = pair.OffsetA - pair.OffsetB;
        var dynamicA = Dynamic(simulation, pair.A, out var a);
        var dynamicB = Dynamic(simulation, pair.B, out var b);
        var velocityA = VelocityAt(simulation, pair.A, pair.OffsetA);
        var velocityB = VelocityAt(simulation, pair.B, offsetB);
        var change = pair.Bounciness * pair.Approach - Vector3.Dot(velocityA - velocityB, normal);
        if (change <= 0 || (!dynamicA && !dynamicB))
        {
            return false;
        }

        var effective = 0f;
        if (dynamicA)
        {
            effective += Resistance(a, pair.OffsetA, normal);
        }

        if (dynamicB)
        {
            effective += Resistance(b, offsetB, normal);
        }

        if (!(effective > 0))
        {
            return false;
        }

        var impulse = normal * (change / effective);
        if (dynamicA)
        {
            a.ApplyImpulse(impulse, pair.OffsetA);
            a.Awake = true;
        }

        if (dynamicB)
        {
            b.ApplyImpulse(-impulse, offsetB);
            b.Awake = true;
        }

        return true;
    }

    /// <summary>How little a body gives to an impulse along <paramref name="normal"/> at <paramref name="offset"/>.</summary>
    private static float Resistance(BodyReference body, Vector3 offset, Vector3 normal)
    {
        body.ComputeInverseInertia(out var inverseInertia);
        Symmetric3x3.TransformWithoutOverlap(Vector3.Cross(offset, normal), inverseInertia, out var turned);
        return body.LocalInertia.InverseMass + Vector3.Dot(Vector3.Cross(turned, offset), normal);
    }

    private static bool Dynamic(Simulation simulation, CollidableReference collidable, out BodyReference body)
    {
        if (collidable.Mobility != CollidableMobility.Dynamic || !simulation.Bodies.BodyExists(collidable.BodyHandle))
        {
            body = default;
            return false;
        }

        body = simulation.Bodies[collidable.BodyHandle];
        return !body.Kinematic;
    }

    /// <summary>The velocity of <paramref name="collidable"/> at <paramref name="offset"/> from its centre; a static's is none.</summary>
    internal static Vector3 VelocityAt(Simulation simulation, CollidableReference collidable, Vector3 offset) =>
        VelocityAt(simulation.Bodies, collidable, offset);

    /// <summary>The velocity of <paramref name="collidable"/> at <paramref name="offset"/> from its centre; a static's is none.</summary>
    internal static Vector3 VelocityAt(Bodies bodies, CollidableReference collidable, Vector3 offset)
    {
        if (collidable.Mobility == CollidableMobility.Static || !bodies.BodyExists(collidable.BodyHandle))
        {
            return Vector3.Zero;
        }

        bodies[collidable.BodyHandle].GetVelocityForOffset(offset, out var velocity);
        return velocity;
    }
}
