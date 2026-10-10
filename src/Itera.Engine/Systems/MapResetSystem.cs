using Arch.Core;
using Arch.System;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Particles;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// The console's <c>cleanup</c> and <c>reset</c> (fixed group, first; <see cref="CommandScope.Match"/>): the command
/// asks, and the next step starts with the work.
/// <para>
/// <c>cleanup</c> removes every loose body (thrown balls, which never go back into the grid, and <see cref="CutPiece"/>
/// bodies) and clears the particles. A host's removals reach its clients as bodies gone.
/// </para>
/// <para>
/// <c>reset</c> puts the world back as it started: it cleans up first (so no body settles into the grid after the
/// reset), then resets the voxel world to the seed's when there is one (<see cref="VoxelWorld.Reset"/>, which clients
/// and saves follow; the loading screen covers the picture while the view is generated again), and counts it in <see cref="WorldResets"/>,
/// which the later systems of the same step read. A game's systems that watch <see cref="WorldResets"/> put their own
/// things back.
/// </para>
/// Reads <see cref="LooseBody"/>; destroys loose-body entities (each body leaves the simulation first, and the entities
/// go after the query).
/// </summary>
public sealed class MapResetSystem : BaseSystem<World, FixedStepTime>, IFixedUpdateSystem
{
    // A client's mirrors of the host's bodies go when the host says; a client cannot run these commands anyway.
    private static readonly QueryDescription s_bodies = new QueryDescription()
        .WithAll<LooseBody>()
        .WithNone<RemoteBody>();

    private readonly LooseBodies _bodies;
    private readonly ParticleEmitter _particles;
    private readonly VoxelWorld? _voxels;
    private readonly WorldResets _resets;
    private readonly ConsoleCommands? _commands;
    private readonly List<Entity> _found = [];
    private bool _cleanup;
    private bool _reset;
    private bool _disposed;

    /// <param name="world">The ECS world.</param>
    /// <param name="bodies">The loose bodies.</param>
    /// <param name="particles">The particles.</param>
    /// <param name="voxels">The voxel world, which <c>reset</c> resets; null without voxel terrain.</param>
    /// <param name="resets">Where resets are counted for the systems that put things back.</param>
    /// <param name="commands">The console's commands, which get <c>cleanup</c> and <c>reset</c>; none in tests.</param>
    public MapResetSystem(
        World world,
        LooseBodies bodies,
        ParticleEmitter particles,
        VoxelWorld? voxels,
        WorldResets resets,
        ConsoleCommands? commands = null
    )
        : base(world)
    {
        _bodies = bodies;
        _particles = particles;
        _voxels = voxels;
        _resets = resets;
        _commands = commands;
    }

    /// <summary>Asks the next step to remove every loose body and clear the particles (<c>cleanup</c>).</summary>
    public void RequestCleanup() => _cleanup = true;

    /// <summary>Asks the next step to put the world back as the seed made it (<c>reset</c>).</summary>
    public void RequestReset() => _reset = true;

    public override void Initialize()
    {
        EcsChecks.RequireAll<LooseBody>(in s_bodies);
        _commands?.Register(
            "cleanup",
            "",
            "Removes every loose body (thrown balls and cut pieces) and every particle.",
            CommandScope.Match,
            context =>
            {
                RequestCleanup();
                context.Print("cleanup: loose bodies and particles removed");
            }
        );
        _commands?.Register(
            "reset",
            "",
            "Puts the world back as the seed made it (every edit undone) and removes every loose body and particle; a game's own things go back as its systems decide.",
            CommandScope.Match,
            context =>
            {
                RequestReset();
                context.Print(
                    "reset: the world goes back to the seed's; the loading screen covers it while it is made again"
                );
            }
        );
    }

    public override void Update(in FixedStepTime step)
    {
        if (_cleanup || _reset)
        {
            _cleanup = false;
            Cleanup();
        }

        if (_reset)
        {
            _reset = false;
            _voxels?.Reset();
            _resets.Add();
        }
    }

    /// <summary>Removes every loose body and clears the particles.</summary>
    private void Cleanup()
    {
        _particles.Clear();
        foreach (ref var chunk in World.Query(in s_bodies))
        {
            var loose = chunk.GetSpan<LooseBody>();
            foreach (var index in chunk)
            {
                _bodies.Remove(loose[index].Body);
                _found.Add(chunk.Entity(index));
            }
        }

        foreach (var entity in _found)
        {
            World.Destroy(entity);
        }

        _found.Clear();
    }

    public override void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        base.Dispose();
    }
}
