using System.Diagnostics;
using Arch.Buffer;
using Arch.Core;
using Arch.System;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Physics;
using Itera.Modding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Systems;

/// <summary>
/// Reads what the last physics step did (fixed group, right after the input; the physics step runs after the group):
/// each loose body's pivot and orientation into <see cref="BodyPose"/>, the pose before it kept as the previous one for
/// drawing between steps; and the push loose bodies gave each character's stand-in (<see cref="PhysicsWorld.PushOn"/>),
/// which goes to <see cref="CharacterVelocity.Push"/> for the movement system and is kept, with its impulse, in
/// <see cref="CharacterStandIn"/>. A grounded character keeps only the push's sideways and downward parts: whatever
/// holds it up, voxels or a body, the mover already stands it on. Reads <see cref="LooseBody"/>, <see cref="CharacterSize"/>, and <see cref="CharacterContact"/>; writes
/// <see cref="BodyPose"/>, <see cref="CharacterStandIn"/>, and <see cref="CharacterVelocity"/>.
/// <para>
/// It is the first system each step to read loose bodies, so it checks that each entity's body is still its own
/// (<see cref="LooseBodies.Holds"/>, by handle and birth stamp): a remover that took the body out of the simulation and
/// forgot the entity would otherwise make the next reader throw, or, once a new body took the handle, make two entities
/// drive one body. Such an entity is a bug: a Debug build throws; a Release build destroys it and logs an Error.
/// </para>
/// </summary>
public sealed partial class PhysicsReadSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    // A client's mirrors of the host's bodies take their poses from the host, not from Bepu.
    private static readonly QueryDescription s_bodies = new QueryDescription()
        .WithAll<LooseBody, BodyPose>()
        .WithNone<RemoteBody>();

    private static readonly QueryDescription s_standIns = new QueryDescription().WithAll<
        CharacterStandIn,
        CharacterVelocity,
        CharacterSize,
        CharacterContact
    >();

    private readonly LooseBodies _bodies;
    private readonly PhysicsWorld _physics;
    private readonly ILogger<PhysicsReadSystem> _logger;
    private readonly CommandBuffer _commands = new();
    private bool _disposed;

    public PhysicsReadSystem(
        World world,
        LooseBodies bodies,
        PhysicsWorld physics,
        ILogger<PhysicsReadSystem>? logger = null
    )
        : base(world)
    {
        _bodies = bodies;
        _physics = physics;
        _logger = logger ?? NullLogger<PhysicsReadSystem>.Instance;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<LooseBody, BodyPose>(in s_bodies);
        EcsChecks.RequireAll<CharacterStandIn, CharacterVelocity, CharacterSize, CharacterContact>(in s_standIns);
    }

    public override void Update(in FixedStepTime step)
    {
        var stale = false;
        foreach (ref var chunk in World.Query(in s_bodies))
        {
            var loose = chunk.GetSpan<LooseBody>();
            var poses = chunk.GetSpan<BodyPose>();
            foreach (var index in chunk)
            {
                ref readonly var body = ref loose[index];
                if (!_bodies.Holds(body.Body, body.Born))
                {
                    Stale(chunk.Entity(index), body.Body);
                    stale = true;
                    continue;
                }

                ref var pose = ref poses[index];
                pose.PreviousPosition = pose.Position;
                pose.PreviousOrientation = pose.Orientation;
                pose.Position = _bodies.PivotOf(body.Body);
                pose.Orientation = _bodies.OrientationOf(body.Body);
            }
        }

        if (stale)
        {
            _commands.Playback(World);
        }

        var pushes = new ReadPushes { Physics = _physics, Seconds = step.StepSeconds };
        World.InlineQuery<ReadPushes, CharacterStandIn, CharacterVelocity, CharacterSize, CharacterContact>(
            in s_standIns,
            ref pushes
        );
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _commands.Dispose();
        base.Dispose();
    }

    /// <summary>An entity whose body is gone or is another's: a bug in whatever removed the body.</summary>
    private void Stale(Entity entity, BepuPhysics.BodyHandle body)
    {
        ThrowInDebug(entity, body);
        LogStale(entity.Id, body.Value);
        _commands.Destroy(entity);
    }

    [Conditional("DEBUG")]
    private static void ThrowInDebug(Entity entity, BepuPhysics.BodyHandle body) =>
        throw new InvalidOperationException(
            $"Loose body entity {entity.Id} holds body {body.Value}, which is gone or another's: whatever removed the body "
                + "left its entity."
        );

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Physics: loose body entity {Entity} held body {Body}, which is gone or another's; the entity is destroyed"
    )]
    private partial void LogStale(int entity, int body);

    private struct ReadPushes : IForEach<CharacterStandIn, CharacterVelocity, CharacterSize, CharacterContact>
    {
        public PhysicsWorld Physics;
        public float Seconds;

        public readonly void Update(
            ref CharacterStandIn standIn,
            ref CharacterVelocity velocity,
            ref CharacterSize size,
            ref CharacterContact contact
        )
        {
            if (!standIn.Present)
            {
                standIn.Push = default;
                standIn.Impulse = 0;
                velocity.Push = default;
                return;
            }

            var push = Physics.PushOn(standIn.Body, standIn.Placed, Seconds);
            standIn.Push = push;
            standIn.Impulse = push.Length() * size.MassKilograms;
            if (contact.Grounded && push.Y > 0)
            {
                push.Y = 0;
            }

            velocity.Push = push;
        }
    }
}
