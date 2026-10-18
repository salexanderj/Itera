using System.Numerics;
using Arch.Core;

namespace Itera.Engine.Spatial;

/// <summary>
/// One thing a <see cref="SpatialGrid"/> holds: an entity standing in an upright cylinder, and the tags a game gives it.
/// </summary>
/// <param name="Entity">The entity.</param>
/// <param name="Feet">The middle of the cylinder's base, in local metres as the match's origin is now.</param>
/// <param name="Radius">The cylinder's radius in metres, 0 or more.</param>
/// <param name="Height">The cylinder's height above its feet in metres, 0 or more.</param>
/// <param name="Tags">The game's bits for what the entity is (its kind, its side, its state when it was set).</param>
public readonly record struct SpatialEntry(Entity Entity, Vector3 Feet, float Radius, float Height, ulong Tags);

/// <summary>An entry a query found, and the distance from what was asked to its cylinder (0 where they meet).</summary>
public readonly record struct SpatialHit(SpatialEntry Entry, float Distance);

/// <summary>
/// The tags an entry must carry for a query to find it: all of <paramref name="All"/>, none of <paramref name="None"/>,
/// and one of <paramref name="Any"/> (any tags at all when it is 0). The default finds every entry.
/// </summary>
/// <param name="All">The entry needs every one of these bits.</param>
/// <param name="None">The entry needs none of these bits.</param>
/// <param name="Any">The entry needs one of these bits, unless this is 0.</param>
public readonly record struct SpatialMatch(ulong All, ulong None = 0, ulong Any = 0)
{
    /// <summary>True when an entry with <paramref name="tags"/> is found.</summary>
    public bool Matches(ulong tags) => (tags & All) == All && (tags & None) == 0 && (Any == 0 || (tags & Any) != 0);
}

/// <summary>
/// Receives what <see cref="SpatialGrid.Within{TVisitor}"/>, <see cref="SpatialGrid.Across{TVisitor}"/>, and
/// <see cref="SpatialGrid.Along{TVisitor}"/> find, in no set order. A game gives a struct (a ref struct may hold spans), so the call is inlined and nothing is boxed.
/// </summary>
public interface ISpatialVisitor
{
    /// <summary>
    /// Takes an entry found and the distance from what was asked to its cylinder; returns false to end the query.
    /// </summary>
    bool Visit(in SpatialEntry entry, float distance);
}

/// <summary>Which entries <see cref="SpatialGrid.Nearest{TFilter}(Vector3, float, SpatialMatch, in TFilter, Span{SpatialHit})"/> may keep, past their tags: a game's own test.</summary>
public interface ISpatialFilter
{
    /// <summary>True when <paramref name="entry"/> may be kept.</summary>
    bool Includes(in SpatialEntry entry);
}

/// <summary>A filter that keeps every entry.</summary>
public readonly struct EveryEntry : ISpatialFilter
{
    public bool Includes(in SpatialEntry entry) => true;
}
