using Arch.Core;
using Itera.Engine.Physics;

namespace Itera.Engine.Systems;

/// <summary>A character that came into or went out of a trigger zone in the last fixed step.</summary>
/// <param name="Change">Entered or left.</param>
/// <param name="Trigger">The zone.</param>
/// <param name="Character">The character's entity.</param>
/// <param name="Removed">It left because the entity, its sensor, or the zone went.</param>
public readonly record struct CharacterTriggerEvent(
    TriggerChange Change,
    TriggerId Trigger,
    Entity Character,
    bool Removed
);

/// <summary>
/// The trigger zones the characters with a <c>TriggerSensor</c> are in, and what changed at the last fixed step
/// (<see cref="TriggerCharacterSystem"/> writes them after the characters move: the systems after it read them in that
/// step, the others at the next). Sorted by character and zone. The match's (scoped). Fixed capacities; a pair or event
/// past them is dropped and counted. Main thread; allocates nothing after it is made.
/// </summary>
public sealed class CharacterTriggers
{
    /// <summary>The character and zone pairs followed at once.</summary>
    public const int MostPairs = 1024;

    private Pair[] _previous = new Pair[MostPairs];
    private Pair[] _current = new Pair[MostPairs];
    private int _previousCount;
    private int _currentCount;
    private readonly CharacterTriggerEvent[] _events = new CharacterTriggerEvent[2 * MostPairs];
    private int _eventCount;

    public CharacterTriggers()
    {
        // The sort makes its helper at its first use: here, not in a step.
        _current.AsSpan(0, 2).Sort();
    }

    /// <summary>The last step's events.</summary>
    public ReadOnlySpan<CharacterTriggerEvent> Events => _events.AsSpan(0, _eventCount);

    /// <summary>Pairs and events dropped past the buffers since the match began.</summary>
    public long Dropped { get; private set; }

    /// <summary>True when <paramref name="character"/> was in <paramref name="trigger"/> at the last step.</summary>
    public bool IsInside(Entity character, TriggerId trigger) =>
        _previous.AsSpan(0, _previousCount).BinarySearch(new Pair(character, trigger)) >= 0;

    /// <summary>Before the step's tests.</summary>
    internal void BeginStep() => _currentCount = 0;

    /// <summary>Notes that <paramref name="character"/> overlaps <paramref name="trigger"/> this step.</summary>
    internal void Add(Entity character, TriggerId trigger)
    {
        if (_currentCount == MostPairs)
        {
            Dropped++;
            return;
        }

        _current[_currentCount++] = new Pair(character, trigger);
    }

    /// <summary>
    /// After the step's tests: the pairs that began and ended (one that ended because its entity, its sensor, or its zone
    /// went is marked removed), sorted; the step's pairs kept for the next.
    /// </summary>
    internal void EndStep(World world, PhysicsWorld physics)
    {
        var current = _current.AsSpan(0, _currentCount);
        current.Sort();
        _eventCount = 0;
        int a = 0,
            b = 0;
        while (a < _previousCount || b < _currentCount)
        {
            var order =
                a == _previousCount ? 1
                : b == _currentCount ? -1
                : _previous[a].CompareTo(_current[b]);
            if (order < 0)
            {
                var gone = _previous[a++];
                var removed =
                    !world.IsAlive(gone.Character)
                    || !world.Has<Components.TriggerSensor>(gone.Character)
                    || !physics.HoldsTrigger(gone.Trigger);
                Add(new CharacterTriggerEvent(TriggerChange.Left, gone.Trigger, gone.Character, removed));
            }
            else if (order > 0)
            {
                var came = _current[b++];
                Add(new CharacterTriggerEvent(TriggerChange.Entered, came.Trigger, came.Character, false));
            }
            else
            {
                a++;
                b++;
            }
        }

        (_previous, _current) = (_current, _previous);
        _previousCount = _currentCount;
    }

    private void Add(in CharacterTriggerEvent change)
    {
        if (_eventCount == _events.Length)
        {
            Dropped++;
            return;
        }

        _events[_eventCount++] = change;
    }

    /// <summary>A character in a zone, ordered by the entity's id and version, then the zone's handle and birth.</summary>
    private readonly record struct Pair(Entity Character, TriggerId Trigger) : IComparable<Pair>
    {
        public int CompareTo(Pair other)
        {
            var order = Character.Id.CompareTo(other.Character.Id);
            if (order == 0)
            {
                order = Character.Version.CompareTo(other.Character.Version);
            }

            if (order == 0)
            {
                order = Trigger.Body.Value.CompareTo(other.Trigger.Body.Value);
            }

            return order != 0 ? order : Trigger.Born.CompareTo(other.Trigger.Born);
        }
    }
}
