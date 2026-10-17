using System.Numerics;
using Arch.Core;
using Arch.System;
using BepuPhysics;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Physics;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Keeps each character's stand-in (fixed group, after the tools, before <see cref="LooseBodySystem"/>): a character
/// gets one while a thrown ball (not a <see cref="CutPiece"/>, which touches only the voxels) comes within
/// <see cref="NearMetres"/> of its middle, and loses it when none does (and while a player flies the debug camera), so
/// stand-ins cost nothing when no such body is around. A stand-in is placed at the character's feet, as they are after
/// this step's move, before the physics step. It moves at the character's velocity plus at most
/// <see cref="PushAcceleration"/> times the step towards the velocity the character wanted
/// (<see cref="CharacterVelocity.Wanted"/>): a character pushes a body it walks into with about its own weight, as
/// footing allows, so it rolls a ball along but not a heavy block, however it runs at it. A <see cref="Bodiless"/>
/// character has no stand-in: one it had is removed at the next step, as is one whose character is destroyed or loses
/// its <see cref="CharacterStandIn"/>. Reads <see cref="CharacterPosition"/>, <see cref="CharacterSize"/>,
/// <see cref="CharacterVelocity"/>, and <see cref="PlayerMode"/>; writes <see cref="CharacterStandIn"/> (a field, not a
/// structural change).
/// </summary>
public sealed class StandInSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    /// <summary>How near a thrown ball must come to a character's middle to give it a stand-in, in metres.</summary>
    public const float NearMetres = 3;

    /// <summary>
    /// How fast, at most, a character's stand-in gains on the character's own velocity towards the one it wants, in metres
    /// per second squared: about gravity, so a character pushes with about its own weight.
    /// </summary>
    public const float PushAcceleration = 10;

    // A bodiless character has no body to push with.
    private static readonly QueryDescription s_characters = new QueryDescription()
        .WithAll<CharacterStandIn, CharacterPosition, CharacterSize, CharacterVelocity, PlayerMode>()
        .WithNone<Bodiless>();

    private static readonly QueryDescription s_bodiless = new QueryDescription().WithAll<CharacterStandIn, Bodiless>();

    private readonly PhysicsWorld _physics;
    private readonly LooseBodies _bodies;

    // The stand-ins the last step kept, and those this one keeps, by body and which body took the handle: one of the
    // last step's this one did not visit lost its character (destroyed, or its component removed) and is removed.
    private List<(BodyHandle Body, long Born)> _kept = [];
    private List<(BodyHandle Body, long Born)> _keeping = [];

    public StandInSystem(World world, PhysicsWorld physics, LooseBodies bodies)
        : base(world)
    {
        _physics = physics;
        _bodies = bodies;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<CharacterStandIn, CharacterPosition, CharacterSize, CharacterVelocity>(in s_characters);
        EcsChecks.RequireAll<PlayerMode>(in s_characters);
        EcsChecks.RequireAll<CharacterStandIn, Bodiless>(in s_bodiless);
    }

    public override void Update(in FixedStepTime step)
    {
        var job = new Keep
        {
            Physics = _physics,
            Bodies = _bodies,
            MaxGain = PushAcceleration * step.StepSeconds,
            Keeping = _keeping,
        };
        World.InlineQuery<Keep, CharacterStandIn, CharacterPosition, CharacterSize, CharacterVelocity, PlayerMode>(
            in s_characters,
            ref job
        );
        var drop = new Drop { Physics = _physics };
        World.InlineQuery<Drop, CharacterStandIn>(in s_bodiless, ref drop);
        RemoveLeft();
    }

    /// <summary>Removes each stand-in the last step kept that this step did not visit, while it is still the same body.</summary>
    private void RemoveLeft()
    {
        foreach (var kept in _kept)
        {
            if (!_keeping.Contains(kept) && _physics.Holds(kept.Body, kept.Born))
            {
                _physics.RemoveStandIn(kept.Body);
            }
        }

        (_kept, _keeping) = (_keeping, _kept);
        _keeping.Clear();
    }

    /// <summary>Removes the stand-in of a character that became bodiless.</summary>
    private struct Drop : IForEach<CharacterStandIn>
    {
        public PhysicsWorld Physics;

        public readonly void Update(ref CharacterStandIn standIn)
        {
            if (standIn.Present)
            {
                Physics.RemoveStandIn(standIn.Body);
                standIn = default;
            }
        }
    }

    private struct Keep : IForEach<CharacterStandIn, CharacterPosition, CharacterSize, CharacterVelocity, PlayerMode>
    {
        public PhysicsWorld Physics;
        public LooseBodies Bodies;
        public float MaxGain;
        public List<(BodyHandle Body, long Born)> Keeping;

        public readonly void Update(
            ref CharacterStandIn standIn,
            ref CharacterPosition position,
            ref CharacterSize size,
            ref CharacterVelocity velocity,
            ref PlayerMode mode
        )
        {
            var middle = position.Feet + new Vector3(0, size.HeightMetres / 2, 0);
            var near = !mode.Flying && Bodies.Count > 0 && Bodies.AnyPieceNear(middle, NearMetres);
            // A character whose height changed gets a stand-in of its new size.
            if (standIn.Present && (!near || standIn.HeightMetres != size.HeightMetres))
            {
                Physics.RemoveStandIn(standIn.Body);
                standIn = default;
            }

            if (near && !standIn.Present)
            {
                standIn.Body = Physics.AddStandIn(
                    position.Feet,
                    size.RadiusMetres,
                    size.HeightMetres,
                    size.MassKilograms
                );
                standIn.Present = true;
                standIn.HeightMetres = size.HeightMetres;
            }

            if (standIn.Present)
            {
                var gain = velocity.Wanted - velocity.MetresPerSecond;
                var length = gain.Length();
                if (length > MaxGain)
                {
                    gain *= MaxGain / length;
                }

                standIn.Placed = velocity.MetresPerSecond + gain;
                Physics.PlaceStandIn(standIn.Body, middle, standIn.Placed);
                Keeping.Add((standIn.Body, Physics.BornOf(standIn.Body)));
            }
        }
    }
}
