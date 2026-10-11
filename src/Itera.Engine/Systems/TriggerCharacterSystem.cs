using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Physics;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Tests the characters the trigger zones see (fixed group, after the characters move and their stand-ins:
/// <see cref="PlayerMovementSystem"/>, <see cref="StandInSystem"/>): each entity with a <see cref="TriggerSensor"/>, not
/// <see cref="Bodiless"/>, as an upright capsule of its <see cref="CharacterSize"/> at its feet, against every zone of
/// the match's physics (<see cref="PhysicsWorld.AddTrigger"/>) whose layer touches the characters'
/// (<see cref="PhysicsWorld.CharacterLayer"/>), and writes what came and went to the match's
/// <see cref="CharacterTriggers"/>. Reads <see cref="CharacterPosition"/> and <see cref="CharacterSize"/>; makes no
/// structural change; allocates nothing.
/// </summary>
public sealed class TriggerCharacterSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    private static readonly QueryDescription s_sensors = new QueryDescription()
        .WithAll<TriggerSensor, CharacterPosition, CharacterSize>()
        .WithNone<Bodiless>();

    private readonly PhysicsWorld _physics;
    private readonly CharacterTriggers _triggers;

    public TriggerCharacterSystem(World world, PhysicsWorld physics, CharacterTriggers triggers)
        : base(world)
    {
        _physics = physics;
        _triggers = triggers;
    }

    public override void Initialize() =>
        EcsChecks.RequireAll<TriggerSensor, CharacterPosition, CharacterSize>(in s_sensors);

    public override void Update(in FixedStepTime step)
    {
        _triggers.BeginStep();
        var zones = _physics.Triggers;
        if (zones.Count > 0)
        {
            var bodies = _physics.Simulation.Bodies;
            var characters = _physics.CharacterLayer;
            foreach (ref var chunk in World.Query(in s_sensors))
            {
                var positions = chunk.GetSpan<CharacterPosition>();
                var sizes = chunk.GetSpan<CharacterSize>();
                foreach (var index in chunk)
                {
                    // The capsule's segment: a radius up from the feet to a radius under the head.
                    var size = sizes[index];
                    var feet = positions[index].Feet;
                    var radius = size.RadiusMetres;
                    var low = MathF.Min(radius, size.HeightMetres * 0.5f);
                    var a = feet + new Vector3(0, low, 0);
                    var b = feet + new Vector3(0, MathF.Max(low, size.HeightMetres - radius), 0);
                    for (var i = 0; i < zones.Count; i++)
                    {
                        var zone = zones[i];
                        if (
                            _physics.Touches(_physics.LayerOf(zone.Id.Body), characters)
                            && TriggerOverlap.Capsule(a, b, radius, zone.Shape, bodies[zone.Id.Body].Pose)
                        )
                        {
                            _triggers.Add(chunk.Entity(index), zone.Id);
                        }
                    }
                }
            }
        }

        _triggers.EndStep(World, _physics);
    }
}
