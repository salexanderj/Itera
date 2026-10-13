using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Replication;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// On a client (fixed group, early: after the origin's move, before the systems that read what it shows), the
/// replication's step (<see cref="ReplicationService"/>): the entities the host removed go, those it sent are made, and
/// each is shown a little behind the host, through its kind (<see cref="IReplicatedKind"/>). Nothing offline or on a
/// host. Its structural changes are the kinds' spawns and despawns and each new entity's <c>Replicated</c>, outside any
/// query.
/// </summary>
public sealed class ReplicationReceiveSystem(World world, ReplicationService replication)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    public override void Update(in FixedStepTime step) => replication.ClientStep(step.Tick);
}
