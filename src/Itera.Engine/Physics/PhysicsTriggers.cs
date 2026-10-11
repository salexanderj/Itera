using System.Numerics;
using BepuPhysics;

namespace Itera.Engine.Physics;

/// <summary>The kind of a trigger zone's shape.</summary>
public enum TriggerShapeKind : byte
{
    /// <summary>A box: <see cref="TriggerShape.Size"/> its edges along its local x, y, and z.</summary>
    Box = 0,

    /// <summary>A sphere: <see cref="TriggerShape.Size"/>'s x its radius.</summary>
    Sphere = 1,

    /// <summary>A capsule along its local y: <see cref="TriggerShape.Size"/>'s x its radius, y its length between the ends' centres.</summary>
    Capsule = 2,
}

/// <summary>A trigger zone's shape (<see cref="PhysicsWorld.AddTrigger"/>), in metres.</summary>
/// <param name="Kind">Box, sphere, or capsule.</param>
/// <param name="Size">A box's edges; a sphere's radius in x; a capsule's radius in x and length in y.</param>
public readonly record struct TriggerShape(TriggerShapeKind Kind, Vector3 Size)
{
    /// <summary>A box of edges <paramref name="size"/>.</summary>
    public static TriggerShape Box(Vector3 size) => new(TriggerShapeKind.Box, size);

    /// <summary>A sphere of <paramref name="radius"/>.</summary>
    public static TriggerShape Sphere(float radius) => new(TriggerShapeKind.Sphere, new Vector3(radius, 0, 0));

    /// <summary>A capsule of <paramref name="radius"/> along its local y, <paramref name="length"/> between its ends' centres.</summary>
    public static TriggerShape Capsule(float radius, float length) =>
        new(TriggerShapeKind.Capsule, new Vector3(radius, length, 0));
}

/// <summary>A trigger zone: its body, and which of the bodies that took the handle it is.</summary>
public readonly record struct TriggerId(BodyHandle Body, long Born);

/// <summary>Whether something came into a trigger zone or went out of it.</summary>
public enum TriggerChange : byte
{
    /// <summary>It began to overlap the zone.</summary>
    Entered = 0,

    /// <summary>It stopped overlapping the zone, or it or the zone was removed.</summary>
    Left = 1,
}

/// <summary>A body that came into or went out of a trigger zone in the last physics step.</summary>
/// <param name="Change">Entered or left.</param>
/// <param name="Trigger">The zone.</param>
/// <param name="Body">The body (a handle another body may take once it is removed).</param>
/// <param name="BodyBorn">Which of the bodies that took the handle it is (<see cref="PhysicsWorld.BornOf"/>).</param>
/// <param name="Removed">It left because it or the zone was removed.</param>
public readonly record struct TriggerEvent(
    TriggerChange Change,
    TriggerId Trigger,
    BodyHandle Body,
    long BodyBorn,
    bool Removed
);

/// <summary>
/// The overlaps of trigger zones and bodies, found by the narrow phase on Bepu's worker threads (each worker into a
/// buffer of its own, <see cref="Record"/>) and taken on the main thread after the step (<see cref="EndStep"/>): sorted
/// by zone and body, so the events do not depend on the threads below the capacities, and set against the last step's
/// to give each step's <see cref="TriggerEvent"/>s, those of bodies and zones removed since the last step first. Fixed
/// capacities; an overlap or event past them is dropped and counted. Allocates nothing after it is made.
/// </summary>
internal sealed class PhysicsTriggers
{
    /// <summary>The least overlaps a worker records in a step; one worker records <see cref="MostPairs"/>.</summary>
    public const int LeastPerWorker = 1024;

    /// <summary>The overlaps followed at once.</summary>
    public const int MostPairs = 4096;

    /// <summary>The events a step gives at most.</summary>
    public const int MostEvents = 2 * MostPairs;

    private readonly int _perWorker;
    private readonly ulong[][] _workers;
    private readonly int[] _counts;
    private readonly ulong[] _gathered = new ulong[MostPairs];
    private ulong[] _previous = new ulong[MostPairs];
    private ulong[] _current = new ulong[MostPairs];
    private int _previousCount;
    private readonly TriggerEvent[] _events = new TriggerEvent[MostEvents];
    private int _eventCount;
    private readonly TriggerEvent[] _pending = new TriggerEvent[MostEvents];
    private int _pendingCount;
    private long _dropped;

    public PhysicsTriggers(int workers)
    {
        _perWorker = Math.Max(LeastPerWorker, MostPairs / workers);
        _workers = new ulong[workers][];
        for (var i = 0; i < workers; i++)
        {
            _workers[i] = new ulong[_perWorker];
        }

        _counts = new int[workers];
        // The sort makes its helper at its first use: here, not in a step.
        _gathered.AsSpan(0, 2).Sort();
    }

    /// <summary>The last step's events.</summary>
    public ReadOnlySpan<TriggerEvent> Events => _events.AsSpan(0, _eventCount);

    /// <summary>Overlaps and events dropped because a buffer was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Before a step: the workers' buffers emptied. Main thread.</summary>
    public void BeginStep() => Array.Clear(_counts);

    /// <summary>Keeps an overlap of zone <paramref name="trigger"/> and <paramref name="body"/>. A Bepu worker thread.</summary>
    public void Record(int worker, BodyHandle trigger, BodyHandle body)
    {
        var count = _counts[worker];
        if (count == _perWorker)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _workers[worker][count] = Key(trigger, body);
        _counts[worker] = count + 1;
    }

    /// <summary>
    /// After the step: the events of the bodies and zones removed since the last step, then those of the overlaps that
    /// began and ended. Main thread.
    /// </summary>
    public void EndStep(BodyKinds kinds)
    {
        var count = 0;
        for (var w = 0; w < _workers.Length; w++)
        {
            var take = Math.Min(_counts[w], MostPairs - count);
            if (take < _counts[w])
            {
                Interlocked.Add(ref _dropped, _counts[w] - take);
            }

            _workers[w].AsSpan(0, take).CopyTo(_gathered.AsSpan(count));
            count += take;
        }

        var gathered = _gathered.AsSpan(0, count);
        gathered.Sort();
        var kept = 0;
        for (var i = 0; i < gathered.Length; i++)
        {
            if (kept == 0 || _current[kept - 1] != gathered[i])
            {
                _current[kept++] = gathered[i];
            }
        }

        _eventCount = 0;
        for (var i = 0; i < _pendingCount; i++)
        {
            Add(_pending[i]);
        }

        _pendingCount = 0;
        // Both sets are sorted: walked together, a key only in the new one entered, one only in the old one left.
        int a = 0,
            b = 0;
        while (a < _previousCount || b < kept)
        {
            if (b == kept || (a < _previousCount && _previous[a] < _current[b]))
            {
                Add(Event(TriggerChange.Left, _previous[a++], kinds));
            }
            else if (a == _previousCount || _current[b] < _previous[a])
            {
                Add(Event(TriggerChange.Entered, _current[b++], kinds));
            }
            else
            {
                a++;
                b++;
            }
        }

        (_previous, _current) = (_current, _previous);
        _previousCount = kept;
    }

    /// <summary>
    /// Forgets the overlaps of <paramref name="body"/>, being removed (born <paramref name="born"/>), giving each a
    /// removed <see cref="TriggerChange.Left"/> at the next step's events. Main thread, between steps.
    /// </summary>
    public void ForgetBody(BodyHandle body, long born, BodyKinds kinds)
    {
        var kept = 0;
        for (var i = 0; i < _previousCount; i++)
        {
            var key = _previous[i];
            if ((int)(uint)key == body.Value)
            {
                Pend(new TriggerEvent(TriggerChange.Left, TriggerOf(key, kinds), body, born, true));
            }
            else
            {
                _previous[kept++] = key;
            }
        }

        _previousCount = kept;
    }

    /// <summary>
    /// Forgets the overlaps of zone <paramref name="trigger"/>, being removed, giving each a removed
    /// <see cref="TriggerChange.Left"/> at the next step's events. Main thread, between steps.
    /// </summary>
    public void ForgetTrigger(TriggerId trigger, BodyKinds kinds)
    {
        var kept = 0;
        for (var i = 0; i < _previousCount; i++)
        {
            var key = _previous[i];
            if ((int)(key >> 32) == trigger.Body.Value)
            {
                var body = new BodyHandle((int)(uint)key);
                Pend(new TriggerEvent(TriggerChange.Left, trigger, body, kinds.Born(body.Value), true));
            }
            else
            {
                _previous[kept++] = key;
            }
        }

        _previousCount = kept;
    }

    /// <summary>True when <paramref name="body"/> overlapped zone <paramref name="trigger"/> at the last step.</summary>
    public bool Holds(BodyHandle trigger, BodyHandle body) =>
        _previous.AsSpan(0, _previousCount).BinarySearch(Key(trigger, body)) >= 0;

    private static ulong Key(BodyHandle trigger, BodyHandle body) =>
        ((ulong)(uint)trigger.Value << 32) | (uint)body.Value;

    private static TriggerId TriggerOf(ulong key, BodyKinds kinds)
    {
        var trigger = (int)(key >> 32);
        return new TriggerId(new BodyHandle(trigger), kinds.Born(trigger));
    }

    private static TriggerEvent Event(TriggerChange change, ulong key, BodyKinds kinds)
    {
        var body = (int)(uint)key;
        return new TriggerEvent(change, TriggerOf(key, kinds), new BodyHandle(body), kinds.Born(body), false);
    }

    private void Add(in TriggerEvent change)
    {
        if (_eventCount == _events.Length)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _events[_eventCount++] = change;
    }

    private void Pend(in TriggerEvent change)
    {
        if (_pendingCount == _pending.Length)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _pending[_pendingCount++] = change;
    }
}
