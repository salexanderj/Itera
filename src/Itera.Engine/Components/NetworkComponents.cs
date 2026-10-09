namespace Itera.Engine.Components;

/// <summary>
/// The stable id a session host gives an entity it sends to clients. Entities are local to each game; this id is what
/// every game shares.
/// </summary>
public struct NetEntity
{
    public uint Id;
}
