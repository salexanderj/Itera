using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;

namespace Itera.Engine.Physics;

/// <summary>
/// A contact the narrow phase saw touching in a step: the two sides as Bepu paired them, the pair as the engine follows
/// it (<see cref="Key"/>), the deepest contact's depth, normal (from B to A), and place (from A's centre, and B's centre
/// from A's), the speed they came together at there as they met (their speed before the step, and what the match's
/// gravity adds or takes over the gap between them), and the bounciness of the bouncier side. Ordered by pair.
/// </summary>
internal struct ContactRecord : IComparable<ContactRecord>
{
    public CollidableReference A;
    public CollidableReference B;
    public ulong Key;
    public float Depth;
    public Vector3 Normal;
    public Vector3 OffsetA;
    public Vector3 OffsetB;
    public float Approach;
    public float Bounciness;

    public readonly int CompareTo(ContactRecord other) => Key.CompareTo(other.Key);
}

/// <summary>
/// The contacts of pairs the engine follows, found by the narrow phase on Bepu's worker threads (each worker into a
/// buffer of its own, <see cref="Record"/>) and taken on the main thread after the step (<see cref="EndStep"/>): sorted by
/// pair, so the result does not depend on the threads below the capacities, and set against the last step's to find the
/// pairs that began touching. A pair is two bodies, or a body and a static; every voxel static counts as one, the voxel
/// ground (<see cref="VoxelGround"/>), so a body that rolls from one voxel cell's static into the next has not begun
/// touching anything. Today it follows the pairs with a bouncy side: a pair that begins touching at
/// <see cref="MinimumBounceSpeed"/> or more is given a separating speed of its bounciness times the speed it came together
/// at (Bepu has no coefficient of restitution; its contacts stop the pair, and the engine pushes it apart), at the contact,
/// by the pair's masses and inertias. Fixed capacities; a contact past them is dropped and counted. Allocates nothing
/// after it is made.
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

    private readonly int _perWorker;
    private readonly ContactRecord[][] _workers;
    private readonly int[] _counts;
    private readonly ContactRecord[] _pairs = new ContactRecord[MostPairs];
    private ulong[] _previous = new ulong[MostPairs];
    private ulong[] _current = new ulong[MostPairs];
    private int _previousCount;
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

    /// <summary>The simulation's bodies, for their velocities as the narrow phase meets them.</summary>
    public Bodies? Bodies { get; set; }

    /// <summary>The step's length, seconds: a pair counts as touching when it would meet within it.</summary>
    public float StepSeconds { get; private set; }

    /// <summary>The match's gravity in the step: what it adds to a pair's speed over the gap before they meet.</summary>
    public Vector3 Gravity { get; private set; }

    /// <summary>Contacts dropped because a worker's buffer or the pairs followed were full.</summary>
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
    /// After the step: gathers the workers' contacts by pair, bounces each bouncy pair that began touching, and keeps the
    /// pairs for the next step. Main thread.
    /// </summary>
    public void EndStep(Simulation simulation)
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
            if (kept > 0 && _current[kept - 1] == pairs[i].Key)
            {
                if (pairs[i].Depth > pairs[kept - 1].Depth)
                {
                    pairs[kept - 1] = pairs[i];
                }

                continue;
            }

            pairs[kept] = pairs[i];
            _current[kept] = pairs[i].Key;
            kept++;
        }

        var previous = _previous.AsSpan(0, _previousCount);
        for (var i = 0; i < kept; i++)
        {
            ref readonly var pair = ref pairs[i];
            if (
                pair.Bounciness > 0
                && pair.Approach >= MinimumBounceSpeed
                && previous.BinarySearch(_current[i]) < 0
                && Bounce(simulation, in pair)
            )
            {
                Bounces++;
            }
        }

        (_previous, _current) = (_current, _previous);
        _previousCount = kept;
    }

    /// <summary>
    /// Forgets every pair <paramref name="side"/> was in (<see cref="BodySide"/>, <see cref="StaticSide"/>), which is
    /// being removed, so a body or static that takes its handle next has begun touching nothing yet. Main thread, between
    /// steps.
    /// </summary>
    public void Forget(uint side)
    {
        var kept = 0;
        for (var i = 0; i < _previousCount; i++)
        {
            var key = _previous[i];
            if ((uint)(key >> 32) != side && (uint)key != side)
            {
                _previous[kept++] = key;
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
