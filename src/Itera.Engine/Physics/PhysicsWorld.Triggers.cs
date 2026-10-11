using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace Itera.Engine.Physics;

/// <summary>A trigger zone this world keeps: its id, its shape, and its Bepu shape (its layer is its record's).</summary>
internal readonly record struct TriggerEntry(TriggerId Id, TriggerShape Shape, TypedIndex BepuShape);

public sealed partial class PhysicsWorld
{
    /// <summary>The most trigger zones a match has at once.</summary>
    public const int MostTriggers = 256;

    private readonly List<TriggerEntry> _triggerList = new(MostTriggers);
    private PhysicsTriggers? _triggers;
    private bool _triggerDropsLogged;

    /// <summary>The trigger zones there are.</summary>
    public int TriggerCount => _triggerList.Count;

    /// <summary>
    /// The bodies that came into or went out of a trigger zone in the last step (those of bodies and zones removed since
    /// the step before first, then by zone and body), for the systems of the next step to read. Main thread.
    /// </summary>
    public ReadOnlySpan<TriggerEvent> TriggerEvents => _triggers is null ? default : _triggers.Events;

    /// <summary>Overlaps and events dropped past the triggers' buffers since the match began.</summary>
    public long TriggerEventsDropped => _triggers?.Dropped ?? 0;

    /// <summary>The zones, for the characters' test (<c>TriggerCharacterSystem</c>).</summary>
    internal List<TriggerEntry> Triggers => _triggerList;

    /// <summary>
    /// Adds a trigger zone of <paramref name="shape"/> at <paramref name="position"/>, turned by
    /// <paramref name="orientation"/> (local to the match's origin, moved with it), in <paramref name="layer"/>: a
    /// kinematic body that never sleeps and stops nothing. It sees every body whose layer touches its own (moving,
    /// asleep, frozen, or a client's mirror of the host's; not statics or stand-ins) that overlaps it, and the events
    /// (<see cref="TriggerEvents"/>) say which came in and went out at each step; characters with a <c>TriggerSensor</c>
    /// are tested by <c>TriggerCharacterSystem</c>. At most <see cref="MostTriggers"/>. Main thread, between steps.
    /// </summary>
    public TriggerId AddTrigger(
        in TriggerShape shape,
        Vector3 position,
        Quaternion orientation,
        PhysicsLayerId layer = default
    )
    {
        if (_triggerList.Count == MostTriggers)
        {
            throw new InvalidOperationException($"A match has at most {MostTriggers} trigger zones.");
        }

        CheckLayer(layer);
        CheckPose(position, orientation);
        var size = shape.Size;
        var bepu = shape.Kind switch
        {
            TriggerShapeKind.Box
                when size.X > 0 && size.Y > 0 && size.Z > 0 && float.IsFinite(size.X + size.Y + size.Z) =>
                Simulation.Shapes.Add(new Box(size.X, size.Y, size.Z)),
            TriggerShapeKind.Sphere when size.X > 0 && float.IsFinite(size.X) => Simulation.Shapes.Add(
                new Sphere(size.X)
            ),
            TriggerShapeKind.Capsule when size.X > 0 && size.Y >= 0 && float.IsFinite(size.X + size.Y) =>
                Simulation.Shapes.Add(new Capsule(size.X, size.Y)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "A trigger's size is finite and above 0."),
        };
        var body = Simulation.Bodies.Add(
            BodyDescription.CreateKinematic(
                new RigidPose(position, Quaternion.Normalize(orientation)),
                new CollidableDescription(bepu, 0.1f),
                // A negative threshold: it never sleeps, so it keeps meeting bodies asleep inside it.
                new BodyActivityDescription(-1)
            )
        );
        _kinds.Set(body, BodyKind.Trigger, layer: layer);
        var id = new TriggerId(body, _kinds.Born(body.Value));
        _triggerList.Add(new TriggerEntry(id, shape, bepu));
        return id;
    }

    /// <summary>
    /// Moves trigger zone <paramref name="trigger"/> to <paramref name="position"/>, turned by
    /// <paramref name="orientation"/>; the next step's events follow. Main thread, between steps.
    /// </summary>
    public void MoveTrigger(TriggerId trigger, Vector3 position, Quaternion orientation)
    {
        IndexOf(trigger);
        CheckPose(position, orientation);
        var reference = Simulation.Bodies[trigger.Body];
        reference.Pose = new RigidPose(position, Quaternion.Normalize(orientation));
        reference.UpdateBounds();
    }

    /// <summary>
    /// Removes trigger zone <paramref name="trigger"/>: every body in it leaves, removed, at the next step's events.
    /// Main thread, between steps.
    /// </summary>
    public void RemoveTrigger(TriggerId trigger)
    {
        var index = IndexOf(trigger);
        var entry = _triggerList[index];
        _triggerList.RemoveAt(index);
        _triggers?.ForgetTrigger(trigger, _kinds);
        Simulation.Bodies.Remove(trigger.Body);
        Simulation.Shapes.Remove(entry.BepuShape);
        _kinds.Forget(trigger.Body);
        _contacts?.Forget(PhysicsContacts.BodySide(trigger.Body), _kinds);
    }

    /// <summary>True when <paramref name="trigger"/> is a zone of this world.</summary>
    public bool HoldsTrigger(TriggerId trigger) => FindTrigger(trigger) >= 0;

    /// <summary>True when <paramref name="body"/> overlapped <paramref name="trigger"/> at the last step.</summary>
    public bool IsInside(TriggerId trigger, BodyHandle body) =>
        _triggers is not null && HoldsTrigger(trigger) && _triggers.Holds(trigger.Body, body);

    /// <summary>The shape and pose of <paramref name="trigger"/>.</summary>
    public (TriggerShape Shape, RigidPose Pose) TriggerOf(TriggerId trigger) =>
        (_triggerList[IndexOf(trigger)].Shape, Simulation.Bodies[trigger.Body].Pose);

    /// <summary>Throws for a position or turn that is not finite, or a turn of no length.</summary>
    private static void CheckPose(Vector3 position, Quaternion orientation)
    {
        if (
            !float.IsFinite(position.X + position.Y + position.Z)
            || !float.IsFinite(orientation.X + orientation.Y + orientation.Z + orientation.W)
            || !(orientation.LengthSquared() > 1e-12f)
        )
        {
            throw new ArgumentOutOfRangeException(nameof(position), "A zone's position and turn are finite numbers.");
        }
    }

    private int IndexOf(TriggerId trigger)
    {
        var index = FindTrigger(trigger);
        return index >= 0 ? index : throw new ArgumentException("No such trigger zone.", nameof(trigger));
    }

    private int FindTrigger(TriggerId trigger)
    {
        for (var i = 0; i < _triggerList.Count; i++)
        {
            if (_triggerList[i].Id == trigger)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>After a step: the triggers' events, and a Warning the first time the buffers dropped some.</summary>
    private void EndTriggerStep()
    {
        _triggers!.EndStep(_kinds);
        if (!_triggerDropsLogged && _triggers.Dropped > 0)
        {
            _triggerDropsLogged = true;
            LogTriggerDrops(PhysicsTriggers.MostPairs, PhysicsTriggers.LeastPerWorker);
        }
    }
}
