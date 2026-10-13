using Arch.Core;
using Arch.System;
using Itera.Engine.Ecs;
using Itera.Engine.Replication;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// On a client (fixed group, after the input, before the move), the reply to the host each step
/// (<see cref="ReplicationService"/>): which snapshots arrived, and its own player's last commands, this step's quantised
/// in place first so the host moves it by the same numbers. Nothing offline or on a host. Writes the predicted player's
/// <see cref="Components.PlayerCommand"/>.
/// </summary>
public sealed class ReplicationReplySystem(World world, ReplicationService replication)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    public override void Update(in FixedStepTime step) => replication.SendReply(step.Tick);
}
