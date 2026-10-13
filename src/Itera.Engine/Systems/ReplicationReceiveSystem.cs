using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Replication;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// At a step's start (fixed group, early: after the origin's move, before the input and the move), the replication's
/// (<see cref="ReplicationService"/>): on a client, its own player checked against the host's state (and corrected), the
/// entities the host removed gone, those it sent made, and each shown a little behind the host, through its kind
/// (<see cref="IReplicatedKind"/>); on a host, each client's next command written into its player. Nothing offline. Its structural changes are the kinds' spawns and despawns and each new entity's <c>Replicated</c>, outside any
/// query.
/// </summary>
public sealed class ReplicationReceiveSystem(World world, ReplicationService replication)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    public override void Update(in FixedStepTime step) => replication.StepStart(step.Tick);
}
