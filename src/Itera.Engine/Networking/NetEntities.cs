using Arch.Core;

namespace Itera.Engine.Networking;

/// <summary>
/// The ids that players' bodies go by in messages: each session slot's own, at the top of the positive int range (some
/// messages write ids as positive variable-length ints), which the ids the host hands out (counted from 1) never reach.
/// Every game registers its own player and its copies of the others under them (<see cref="NetEntities"/>).
/// </summary>
public static class PlayerIds
{
    /// <summary>The first player id: slot 0's.</summary>
    public const uint First = 0x7FFFFF00;

    /// <summary>The id of the player in session slot <paramref name="slot"/>.</summary>
    public static uint Of(int slot) => First + (uint)slot;

    /// <summary>The slot of player id <paramref name="id"/>, or -1 for an id that is not a player's.</summary>
    public static int SlotOf(uint id) => id >= First && id - First < SessionService.MaxSlots ? (int)(id - First) : -1;
}

/// <summary>
/// The entities of one game that carry a <see cref="Components.NetEntity"/> id, by id: on a host the ones it sends, on a
/// client the mirrors it made. Each entry has a kind, a byte the game defines (for example 1 for bodies, 2 for
/// characters), so that kinds share one id space without being mistaken for each other. Every lookup checks the entity
/// is still alive, since a stored handle may outlive its entity. The host also hands out the ids. Main thread.
/// </summary>
public sealed class NetEntities
{
    private readonly Dictionary<uint, (Entity Entity, byte Kind)> _byId = [];
    private uint _lastId;

    /// <summary>A new id, on a host.</summary>
    public uint NextId() => ++_lastId;

    public void Add(uint id, Entity entity, byte kind) => _byId[id] = (entity, kind);

    public void Remove(uint id) => _byId.Remove(id);

    /// <summary>
    /// Forgets <paramref name="id"/> when it is not known or names an entity of <paramref name="kind"/>: a host's message
    /// about one kind never unmaps an entity of another.
    /// </summary>
    public void Remove(uint id, byte kind)
    {
        if (!_byId.TryGetValue(id, out var found) || found.Kind == kind)
        {
            _byId.Remove(id);
        }
    }

    /// <summary>True when <paramref name="id"/> is known here, even if its entity has gone.</summary>
    public bool Contains(uint id) => _byId.ContainsKey(id);

    /// <summary>The live entity with <paramref name="id"/>.</summary>
    public bool TryGet(World world, uint id, out Entity entity) => TryGet(world, id, out entity, out _);

    /// <summary>
    /// The live entity with <paramref name="id"/>, and its kind. All kinds share one id space, so code that reads an
    /// entity's components by an id from the network checks the kind first: components read through an id of another
    /// kind read memory the entity does not own (the Arch package checks nothing).
    /// </summary>
    public bool TryGet(World world, uint id, out Entity entity, out byte kind)
    {
        if (_byId.TryGetValue(id, out var found) && world.IsAlive(found.Entity))
        {
            entity = found.Entity;
            kind = found.Kind;
            return true;
        }

        entity = Entity.Null;
        kind = default;
        return false;
    }

    /// <summary>The live entity with <paramref name="id"/> when it is of <paramref name="kind"/>.</summary>
    public bool TryGet(World world, uint id, byte kind, out Entity entity)
    {
        if (TryGet(world, id, out entity, out var found) && found == kind)
        {
            return true;
        }

        entity = Entity.Null;
        return false;
    }

    /// <summary>
    /// The id of <paramref name="entity"/>, alive or not (an entity destroyed since keeps its id until its removal is
    /// sent), compared as a whole entity so a recycled one does not match. A scan: for rare events.
    /// </summary>
    public bool TryFindId(Entity entity, out uint id)
    {
        foreach (var (found, (each, _)) in _byId)
        {
            if (each == entity)
            {
                id = found;
                return true;
            }
        }

        id = 0;
        return false;
    }

    /// <summary>Adds to <paramref name="gone"/> the ids of <paramref name="kind"/> whose entities are no longer alive.</summary>
    public void CollectGone(World world, byte kind, List<uint> gone)
    {
        foreach (var (id, (entity, entityKind)) in _byId)
        {
            if (entityKind == kind && !world.IsAlive(entity))
            {
                gone.Add(id);
            }
        }
    }

    /// <summary>Adds to <paramref name="alive"/> every live entity of <paramref name="kind"/> with its id.</summary>
    public void CollectAlive(World world, byte kind, List<(uint Id, Entity Entity)> alive)
    {
        foreach (var (id, (entity, entityKind)) in _byId)
        {
            if (entityKind == kind && world.IsAlive(entity))
            {
                alive.Add((id, entity));
            }
        }
    }
}
