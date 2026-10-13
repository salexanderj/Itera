namespace Itera.Engine.Components;

/// <summary>
/// The stable id a session host gives an entity it sends to clients. Entities are local to each game; this id is what
/// every game shares.
/// </summary>
public struct NetEntity
{
    public uint Id;
}

/// <summary>
/// An entity the replication sends (on a host) or shows (on a client): its id, which every peer shares (a player's is its
/// slot's, <c>PlayerIds</c>), and its kind (<c>ReplicatedKinds</c>). The replication adds it; a game reads it.
/// </summary>
public struct Replicated
{
    public uint Id;
    public byte Kind;
}
