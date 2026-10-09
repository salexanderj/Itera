using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Physics;

namespace Itera.Engine.Systems;

/// <summary>
/// Makes room for new loose bodies when <see cref="LooseBodies.MaxBodies"/> exist: the oldest go first
/// (<see cref="LooseBodies.BornOf"/>), each removed from the simulation with its models, and its entity destroyed; a
/// ragdoll goes whole, every body of it with the oldest (a corpse that lost its hips would leave its limbs lying). A
/// thrown ball removed this way never goes back into the grid, and a cut body goes as it would after resting. Only the
/// world's owner (offline or a session host) makes room; a client's bodies are mirrors of the host's (<see cref="RemoteBody"/>),
/// which follow the host's removals. Inside a query, entities cannot be destroyed: <see cref="MakeRoom"/> then keeps
/// them, already without their bodies, until <see cref="DestroyRemoved"/> after the query. Main thread; allocates
/// nothing once its list has grown.
/// </summary>
public sealed class LooseBodyRoom(LooseBodies bodies)
{
    private static readonly QueryDescription s_bodies = new QueryDescription()
        .WithAll<LooseBody>()
        .WithNone<RemoteBody>();

    private readonly List<Entity> _removed = [];
    private readonly List<(Entity Entity, BepuPhysics.BodyHandle Body)> _members = [];

    /// <summary>
    /// Removes the oldest bodies until <paramref name="count"/> more fit, and returns whether they do (false when no
    /// body with an entity is left to remove). With <paramref name="insideQuery"/>, their entities wait for
    /// <see cref="DestroyRemoved"/>; otherwise they are destroyed at once.
    /// </summary>
    public bool MakeRoom(World world, int count, bool insideQuery)
    {
        while (bodies.Count + count > bodies.MaxBodies)
        {
            if (!TryOldest(world, out var entity, out var body))
            {
                return false;
            }

            var ragdoll = bodies.RagdollOf(body);
            Take(world, entity, body, insideQuery);
            if (ragdoll > 0)
            {
                CollectRagdoll(world, ragdoll);
                foreach (var (member, handle) in _members)
                {
                    Take(world, member, handle, insideQuery);
                }

                _members.Clear();
            }
        }

        return true;
    }

    /// <summary>Destroys the entities whose bodies <see cref="MakeRoom"/> removed inside a query. Call after the query.</summary>
    public void DestroyRemoved(World world)
    {
        foreach (var entity in _removed)
        {
            if (world.IsAlive(entity))
            {
                world.Destroy(entity);
            }
        }

        _removed.Clear();
    }

    /// <summary>Removes <paramref name="body"/>, and destroys its entity at once or after the query.</summary>
    private void Take(World world, Entity entity, BepuPhysics.BodyHandle body, bool insideQuery)
    {
        bodies.Remove(body);
        if (insideQuery)
        {
            _removed.Add(entity);
        }
        else
        {
            world.Destroy(entity);
        }
    }

    /// <summary>The bodies left of <paramref name="ragdoll"/> that have entities, into <see cref="_members"/>.</summary>
    private void CollectRagdoll(World world, int ragdoll)
    {
        foreach (ref var chunk in world.Query(in s_bodies))
        {
            var loose = chunk.GetSpan<LooseBody>();
            foreach (var index in chunk)
            {
                var handle = loose[index].Body;
                if (!bodies.Contains(handle) || bodies.RagdollOf(handle) != ragdoll)
                {
                    continue;
                }

                var entity = chunk.Entity(index);
                if (!_removed.Contains(entity))
                {
                    _members.Add((entity, handle));
                }
            }
        }
    }

    /// <summary>
    /// The entity and body of the oldest loose body with an entity, skipping those already removed (whose handles a new
    /// body may have taken since).
    /// </summary>
    private bool TryOldest(World world, out Entity oldest, out BepuPhysics.BodyHandle body)
    {
        oldest = Entity.Null;
        body = default;
        var born = long.MaxValue;
        foreach (ref var chunk in world.Query(in s_bodies))
        {
            var loose = chunk.GetSpan<LooseBody>();
            foreach (var index in chunk)
            {
                var handle = loose[index].Body;
                if (!bodies.Contains(handle) || bodies.BornOf(handle) >= born)
                {
                    continue;
                }

                var entity = chunk.Entity(index);
                if (_removed.Contains(entity))
                {
                    continue;
                }

                born = bodies.BornOf(handle);
                oldest = entity;
                body = handle;
            }
        }

        return oldest != Entity.Null;
    }
}
