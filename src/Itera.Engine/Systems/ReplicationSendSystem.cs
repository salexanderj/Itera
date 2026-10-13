using Arch.Core;
using Arch.System;
using Itera.Engine.Commands;
using Itera.Engine.Ecs;
using Itera.Engine.Replication;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// On a host (fixed group, last: after every system that moves what it sends), the replication's step
/// (<see cref="ReplicationService"/>): the clients that joined or left, their replies, and every few steps the
/// replicated entities' states captured and each client's packets sent. Nothing offline or on a client. The console's
/// <c>replication</c> is its command. Reads the replicated entities through their kinds; writes no components.
/// </summary>
public sealed class ReplicationSendSystem(World world, ReplicationService replication, ConsoleCommands? commands = null)
    : BaseSystem<World, FixedStepTime>(world),
        IFixedUpdateSystem
{
    public override void Initialize()
    {
        if (commands is not null)
        {
            replication.RegisterCommands(commands);
        }
    }

    public override void Update(in FixedStepTime step) => replication.HostStep(step.Tick);
}
