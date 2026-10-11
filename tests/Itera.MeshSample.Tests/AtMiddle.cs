using System.Numerics;
using Itera.Engine.Maths;

namespace Itera.MeshSample.Tests;

/// <summary>The sample's absolute points as local positions while the match's origin is at the map's middle.</summary>
internal static class AtMiddle
{
    /// <summary>The origin at the map's middle.</summary>
    public static WorldOrigin Origin { get; } = new();

    /// <summary><paramref name="point"/> local to the map's middle.</summary>
    public static Vector3 Local(WorldPoint point) => Origin.ToLocal(point);
}
