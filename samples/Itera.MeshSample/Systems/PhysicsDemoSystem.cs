using System.Numerics;
using Arch.Core;
using Arch.System;
using BepuPhysics;
using Itera.Engine.Audio;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Maths;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Modding;

namespace Itera.MeshSample.Systems;

/// <summary>
/// Runs the yard's physics corner (<see cref="PhysicsDemo"/>; fixed group, after <see cref="TriggerCharacterSystem"/>,
/// whose comings and goings it reads that step, and before the physics step, whose trigger and impact events it reads
/// at the next): at the start, the trigger zone and the floating and slow balls; every
/// <see cref="PhysicsDemo.ReleaseSeconds"/>, a rubber ball at the ice ramp's top and one over the rubber pad (the oldest
/// past <see cref="PhysicsDemo.MostReleased"/> removed); a chime at the zone's middle as a body or a character comes into
/// it (at most one a step); and a clang where a ball that reports its impacts lands at 1 m/s or more, as loud as it was
/// fast (full at 8 m/s; at most <see cref="MostClangs"/> a step). Its balls are loose bodies with their entities, as the
/// console's balls are (<see cref="LooseBody"/>, <see cref="BodyPose"/>, <see cref="BodyBox"/>: drawn, read after each
/// step, pushed by the player, read by the mover), in the balls' layer; it destroys a released ball's entity as it
/// removes the ball. Allocates nothing but as it lets a ball go.
/// </summary>
public sealed class PhysicsDemoSystem(
    World world,
    PhysicsWorld physics,
    PhysicsLayers layers,
    LooseBodies bodies,
    CharacterTriggers characters,
    AudioService audio,
    WorldOrigin origin
) : BaseSystem<World, FixedStepTime>(world), IFixedUpdateSystem
{
    /// <summary>The most clangs a step plays.</summary>
    public const int MostClangs = 4;

    /// <summary>The least speed of a landing that clangs, metres a second.</summary>
    public const float LeastClangSpeed = 1;

    /// <summary>The speed of a landing that clangs at its full gain, metres a second.</summary>
    public const float FullClangSpeed = 8;

    private readonly (Entity Entity, BodyHandle Body, long Born)[] _released = new (Entity, BodyHandle, long)[
        PhysicsDemo.MostReleased
    ];
    private readonly List<(BodyHandle Body, long Born)> _standing = [];
    private int _releasedCount;
    private int _next;
    private float _sinceRelease;
    private int _clang = -1;
    private int _chime = -1;
    private PhysicsLayerId _balls;

    /// <summary>The trigger zone.</summary>
    public TriggerId Zone { get; private set; }

    /// <summary>Chimes played since the match began.</summary>
    public int Chimes { get; private set; }

    /// <summary>Clangs played since the match began, and the gain of the last.</summary>
    public (int Count, float LastGain) Clangs { get; private set; }

    /// <summary>True when <paramref name="body"/> is one of the corner's balls (still in the match).</summary>
    public bool Owns(BodyHandle body)
    {
        for (var i = 0; i < _releasedCount; i++)
        {
            if (_released[i].Body == body && bodies.Holds(body, _released[i].Born))
            {
                return true;
            }
        }

        foreach (var (standing, born) in _standing)
        {
            if (standing == body && bodies.Holds(body, born))
            {
                return true;
            }
        }

        return false;
    }

    public override void Initialize()
    {
        _clang = Find(SampleSounds.Clang);
        _chime = Find(SampleSounds.ZoneChime);
        _balls = layers.TryFind(PhysicsDemo.BallsLayer, out var balls)
            ? balls
            : throw new InvalidOperationException($"No physics layer is named {PhysicsDemo.BallsLayer}.");
        Zone = physics.AddTrigger(
            TriggerShape.Box(PhysicsDemo.ZoneSize),
            origin.ToLocal(PhysicsDemo.ZoneMiddle),
            Quaternion.Identity
        );
        foreach (var place in PhysicsDemo.FloatingPlaces)
        {
            Stand(place, PhysicsDemo.FloatingBall, 0, PhysicsMarks.None);
        }

        foreach (var place in PhysicsDemo.SlowPlaces)
        {
            Stand(place, PhysicsDemo.SlowBall, PhysicsDemo.SlowGravity, PhysicsMarks.ContactEvents);
        }
    }

    public override void Update(in FixedStepTime step)
    {
        Listen();
        _sinceRelease += step.StepSeconds;
        if (_sinceRelease >= PhysicsDemo.ReleaseSeconds)
        {
            _sinceRelease = 0;
            Release(PhysicsDemo.RampRelease);
            Release(PhysicsDemo.PadRelease);
        }
    }

    /// <summary>The last physics step's comings into the zone and landings, and this step's characters'.</summary>
    private void Listen()
    {
        var chime = false;
        foreach (var change in physics.TriggerEvents)
        {
            chime |= change.Change == TriggerChange.Entered && change.Trigger == Zone;
        }

        foreach (var change in characters.Events)
        {
            chime |= change.Change == TriggerChange.Entered && change.Trigger == Zone;
        }

        if (chime)
        {
            audio.Play(
                new SoundEvent
                {
                    Recipe = _chime,
                    Position = origin.ToLocal(PhysicsDemo.ZoneMiddle),
                    Level = 1,
                }
            );
            Chimes++;
        }

        var played = 0;
        foreach (var impact in physics.ImpactEvents)
        {
            if (impact.Change != ImpactChange.Began || impact.Speed < LeastClangSpeed || played == MostClangs)
            {
                continue;
            }

            var gain = Math.Clamp(impact.Speed / FullClangSpeed, 0.15f, 1);
            audio.Play(
                new SoundEvent
                {
                    Recipe = _clang,
                    Position = impact.Point,
                    Level = 1,
                    Gain = gain,
                }
            );
            played++;
            Clangs = (Clangs.Count + 1, gain);
        }
    }

    /// <summary>Lets a rubber ball go at <paramref name="place"/>, the oldest released going past the most kept.</summary>
    private void Release(WorldPoint place)
    {
        if (_releasedCount == PhysicsDemo.MostReleased)
        {
            var (entity, oldest, born) = _released[_next];
            if (World.IsAlive(entity))
            {
                World.Destroy(entity);
            }

            if (bodies.Holds(oldest, born))
            {
                bodies.Remove(oldest);
            }
        }

        if (!Ball(place, PhysicsDemo.RubberBall, 1, PhysicsMarks.ContactEvents, out var ball, out var made))
        {
            return;
        }

        _released[_next] = (made, ball, bodies.BornOf(ball));
        _next = (_next + 1) % PhysicsDemo.MostReleased;
        _releasedCount = Math.Min(_releasedCount + 1, PhysicsDemo.MostReleased);
    }

    private void Stand(WorldPoint place, byte material, float gravity, PhysicsMarks marks)
    {
        if (Ball(place, material, gravity, marks, out var ball, out _))
        {
            _standing.Add((ball, bodies.BornOf(ball)));
        }
    }

    /// <summary>
    /// A ball of the corner: a loose body of <paramref name="material"/> in the balls' layer, with its entity, as the
    /// console's balls have.
    /// </summary>
    private bool Ball(
        WorldPoint place,
        byte material,
        float gravity,
        PhysicsMarks marks,
        out BodyHandle ball,
        out Entity entity
    )
    {
        var at = origin.ToLocal(place);
        entity = Entity.Null;
        if (!bodies.TryCreateBall(PhysicsDemo.BallRadius, material, at, Vector3.Zero, out ball))
        {
            return false;
        }

        bodies.BoxOf(ball, out var min, out var max);
        entity = World.Create(
            new LooseBody
            {
                Body = ball,
                BallRadius = bodies.BallRadiusOf(ball),
                Born = bodies.BornOf(ball),
            },
            new BodyPose
            {
                Position = at,
                PreviousPosition = at,
                Orientation = Quaternion.Identity,
                PreviousOrientation = Quaternion.Identity,
            },
            new BodyBox { Min = min, Max = max }
        );

        physics.SetLayer(ball, _balls);
        physics.SetMarks(ball, marks);
        if (gravity != 1)
        {
            physics.SetGravityScale(ball, gravity);
        }

        return true;
    }

    private int Find(string name) =>
        audio.Recipes.TryFind(name, out var recipe)
            ? recipe
            : throw new InvalidOperationException($"No sound recipe is named {name}.");
}
