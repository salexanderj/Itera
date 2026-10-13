using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Replication;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// On a client (fixed group, after the input), the reply to the host each step (<see cref="ReplicationService"/>): which
/// snapshots arrived. Nothing offline or on a host. Reads and writes no components.
/// </summary>
public sealed class ReplicationReplySystem(World world, ReplicationService replication)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    public override void Update(in FixedStepTime step) => replication.SendReply();
}
