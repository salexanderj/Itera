using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Modding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="TriggerCharacterSystem"/>: a character walking through a zone enters and leaves once; the capsule's edge
/// against a sphere; a destroyed entity, a sensor taken off, and a removed zone give a removed leave, and a character
/// that turns bodiless a plain one; a zone whose layer does not touch the characters' sees none until its layer is
/// changed; and an update
/// allocates nothing.
/// </summary>
public sealed class TriggerCharacterSystemTests : IDisposable
{
    private static readonly FixedStepTime Step = new(1f / 60, 1);
    private static readonly QueryDescription s_sensors = new QueryDescription().WithAll<
        TriggerSensor,
        CharacterPosition
    >();

    private readonly World _world = World.Create();
    private readonly PhysicsWorld _physics;
    private readonly CharacterTriggers _triggers = new();
    private readonly TriggerCharacterSystem _system;

    public TriggerCharacterSystemTests()
    {
        _physics = Physics(null);
        _system = new TriggerCharacterSystem(_world, _physics, _triggers);
        _system.Initialize();
    }

    public void Dispose()
    {
        _system.Dispose();
        _physics.Dispose();
        _world.Dispose();
    }

    [Fact]
    public void A_character_walking_through_a_zone_enters_and_leaves_once()
    {
        var zone = _physics.AddTrigger(
            TriggerShape.Box(new Vector3(1, 2, 1)),
            new Vector3(0, 1, 0),
            Quaternion.Identity
        );
        var character = Character(new Vector3(-2, 0, 0));
        var events = new List<(int Step, CharacterTriggerEvent Event)>();

        // 5 cm a step along x: its 25.5 cm body meets the zone's side at x = -0.5 and leaves it past x = 0.5.
        for (var i = 0; i <= 80; i++)
        {
            Place(new Vector3(-2 + i * 0.05f, 0, 0));
            _system.Update(in Step);
            foreach (var change in _triggers.Events)
            {
                events.Add((i, change));
            }
        }

        Assert.Equal(
            [
                (25, new CharacterTriggerEvent(TriggerChange.Entered, zone, character, false)),
                (56, new CharacterTriggerEvent(TriggerChange.Left, zone, character, false)),
            ],
            events
        );
    }

    [Theory]
    [InlineData(1.25f, true)]
    [InlineData(1.26f, false)]
    public void A_character_is_in_a_sphere_while_its_capsule_reaches_it(float distance, bool inside)
    {
        // A sphere of 1 m at 1 m up: the capsule's axis passes it at the distance, its radius 0.255 m.
        var zone = _physics.AddTrigger(TriggerShape.Sphere(1), new Vector3(0, 1, 0), Quaternion.Identity);
        var character = Character(new Vector3(distance, 0, 0));

        _system.Update(in Step);

        Assert.Equal(inside, _triggers.IsInside(character, zone));
    }

    [Fact]
    public void A_destroyed_entity_a_sensor_taken_off_and_a_removed_zone_leave_removed_and_bodiless_leaves_plainly()
    {
        var zone = _physics.AddTrigger(TriggerShape.Box(new Vector3(10, 4, 10)), Vector3.Zero, Quaternion.Identity);
        var destroyed = Character(new Vector3(-3, 0, 0));
        var unsensed = Character(new Vector3(-1, 0, 0));
        var bodiless = Character(new Vector3(1, 0, 0));
        var kept = Character(new Vector3(3, 0, 0));
        _system.Update(in Step);
        Assert.Equal(4, _triggers.Events.Length);

        _world.Destroy(destroyed);
        _world.Remove<TriggerSensor>(unsensed);
        _world.Add<Bodiless>(bodiless);
        _system.Update(in Step);
        var first = _triggers.Events.ToArray();
        _physics.RemoveTrigger(zone);
        _system.Update(in Step);
        var second = _triggers.Events.ToArray();

        Assert.Equal(
            [
                new CharacterTriggerEvent(TriggerChange.Left, zone, destroyed, true),
                new CharacterTriggerEvent(TriggerChange.Left, zone, unsensed, true),
                new CharacterTriggerEvent(TriggerChange.Left, zone, bodiless, false),
            ],
            first.OrderBy(change => change.Character.Id)
        );
        Assert.Equal([new CharacterTriggerEvent(TriggerChange.Left, zone, kept, true)], second);
    }

    [Fact]
    public void A_zone_whose_layer_does_not_touch_the_characters_sees_none()
    {
        var layers = new PhysicsLayers();
        var characters = layers.Register("characters");
        var balls = layers.Register("balls only");
        layers.CharacterLayer = characters;
        layers.SetTouches(balls, characters, false);
        using var physics = Physics(layers);
        var triggers = new CharacterTriggers();
        using var system = new TriggerCharacterSystem(_world, physics, triggers);
        system.Initialize();
        var hidden = physics.AddTrigger(TriggerShape.Sphere(2), Vector3.Zero, Quaternion.Identity, balls);
        var seen = physics.AddTrigger(TriggerShape.Sphere(2), Vector3.Zero, Quaternion.Identity, characters);
        var character = Character(Vector3.Zero);

        system.Update(in Step);
        var first = triggers.Events.ToArray();
        // The hidden zone put in the characters' layer: they see it from the next step.
        physics.SetLayer(hidden.Body, characters);
        system.Update(in Step);

        Assert.Equal([new CharacterTriggerEvent(TriggerChange.Entered, seen, character, false)], first);
        Assert.Equal(
            [new CharacterTriggerEvent(TriggerChange.Entered, hidden, character, false)],
            triggers.Events.ToArray()
        );
    }

    [Fact]
    public void Update_allocates_nothing()
    {
        _physics.AddTrigger(TriggerShape.Capsule(1, 2), new Vector3(0, 1, 0), Quaternion.Identity);
        _physics.AddTrigger(TriggerShape.Box(Vector3.One), new Vector3(3, 0, 0), Quaternion.Identity);
        for (var i = 0; i < 6; i++)
        {
            Character(new Vector3(i - 2, 0, 0));
        }

        _system.Update(in Step);

        Assert.Equal(0, SystemAllocation.BytesAllocated(_system, Step));
    }

    private Entity Character(Vector3 feet) =>
        _world.Create(
            new TriggerSensor(),
            new CharacterPosition { Feet = feet, PreviousFeet = feet },
            CharacterSize.ForHeight(1.7f)
        );

    private void Place(Vector3 feet) =>
        _world.Query(in s_sensors, (ref CharacterPosition position) => position.Feet = feet);

    private static PhysicsWorld Physics(PhysicsLayers? layers)
    {
        var options = new EngineOptions();
        options.Physics.Threads = 1;
        var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, Options.Create(options), null, layers);
        physics.Initialize();
        return physics;
    }
}
