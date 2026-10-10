using System.Globalization;
using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Systems;

/// <summary>
/// The box a player, its eye, and what it aims at stay in: the match's world columns (its <see cref="VoxelGrid"/>'s
/// bounds, or bounds a game gives without voxel terrain, <see cref="WithoutTerrain"/> by default) and
/// <see cref="MarginMetres"/> round them on every side, from below the world's floor to above its top. The
/// fly camera and <c>tp</c> keep to it, and a host can drop a peer's state or request that lies outside it: a position
/// far outside the world makes column walks and edits very large. The margin leaves room to fly out and look at the
/// whole world. Immutable; any thread.
/// </summary>
public sealed class PlayArea
{
    /// <summary>How far past the world's edge, floor, and top the box reaches by default, in metres.</summary>
    public const float DefaultMarginMetres = 1000;

    /// <summary>
    /// How far inside the box the fly camera, <c>tp</c>, and a start pose keep the eye, in metres: room for the feet
    /// below it, so a player's states stay inside.
    /// </summary>
    public const float EyeInsetMetres = 5;

    /// <param name="grid">The match's grid, whose bounds the box holds.</param>
    /// <param name="marginMetres">How far past the world the box reaches, in metres.</param>
    public PlayArea(VoxelGrid grid, float marginMetres = DefaultMarginMetres)
        : this(
            -Metres(grid, grid.MinVoxelXZ),
            Metres(grid, grid.EndVoxelXZ),
            Metres(grid, grid.MinVoxelY),
            Metres(grid, grid.EndVoxelY),
            LooseBodies.FallLimitMetres(grid),
            marginMetres
        ) { }

    /// <summary>The box round a world of the given bounds, for a match without voxel terrain.</summary>
    /// <param name="worldEdgeMetres">How far the world reaches from the middle on x and z, in metres.</param>
    /// <param name="worldFloorMetres">The world's floor, in metres; a body 10 m below it has fallen out.</param>
    /// <param name="worldTopMetres">The world's top, in metres.</param>
    /// <param name="marginMetres">How far past the world the box reaches, in metres.</param>
    public PlayArea(
        float worldEdgeMetres,
        float worldFloorMetres,
        float worldTopMetres,
        float marginMetres = DefaultMarginMetres
    )
        : this(
            worldEdgeMetres,
            worldEdgeMetres,
            worldFloorMetres,
            worldTopMetres,
            worldFloorMetres - LooseBodies.FallMarginMetres,
            marginMetres
        )
    {
        if (
            !float.IsFinite(worldEdgeMetres)
            || worldEdgeMetres <= 0
            || !float.IsFinite(worldFloorMetres)
            || !float.IsFinite(worldTopMetres)
            || worldTopMetres <= worldFloorMetres
            || !float.IsFinite(marginMetres)
            || marginMetres < 0
        )
        {
            throw new ArgumentException(
                $"A play area needs an edge over 0, a top over its floor, and a margin of 0 or more, all finite (edge {worldEdgeMetres}, floor {worldFloorMetres}, top {worldTopMetres}, margin {marginMetres})."
            );
        }
    }

    private PlayArea(
        float worldEdgeMetres,
        float endMetres,
        float worldFloorMetres,
        float worldTopMetres,
        float fallLimitMetres,
        float marginMetres
    )
    {
        MarginMetres = marginMetres;
        WorldEdgeMetres = worldEdgeMetres;
        WorldFloorMetres = worldFloorMetres;
        EdgeMetres = endMetres + marginMetres;
        FloorMetres = WorldFloorMetres - marginMetres;
        TopMetres = worldTopMetres + marginMetres;
        FallLimitMetres = fallLimitMetres;
        EyeBounds = string.Create(
            CultureInfo.InvariantCulture,
            $"x and z within {EdgeMetres - EyeInsetMetres:F0} m of the middle, y from {FloorMetres + EyeInsetMetres:F0} to {TopMetres - EyeInsetMetres:F0} m"
        );
    }

    /// <summary>
    /// The engine's play area in a match without voxel terrain: the default bounds of a 10 cm voxel grid (±512 m across,
    /// from -51.2 m to 102.4 m) and the default margin. A game whose meshes reach past them registers its own.
    /// </summary>
    public static PlayArea WithoutTerrain { get; } = new(new VoxelGrid(10));

    /// <summary>How far past the world's edge, floor, and top the box reaches, in metres.</summary>
    public float MarginMetres { get; }

    /// <summary>The largest x and z inside the box, in metres; the smallest is its negative.</summary>
    public float EdgeMetres { get; }

    /// <summary>The lowest y inside the box, in metres.</summary>
    public float FloorMetres { get; }

    /// <summary>The highest y inside the box, in metres.</summary>
    public float TopMetres { get; }

    /// <summary>The world's edge, in metres from the middle.</summary>
    public float WorldEdgeMetres { get; }

    /// <summary>The world's floor, in metres.</summary>
    public float WorldFloorMetres { get; }

    /// <summary>Below this height, in metres, a body has fallen out of the world (<see cref="LooseBodies.FallLimitMetres"/>).</summary>
    public float FallLimitMetres { get; }

    /// <summary>The box an eye may be put in, for messages.</summary>
    public string EyeBounds { get; }

    /// <summary>True when <paramref name="metres"/> lies inside the box; false for NaN.</summary>
    public bool Contains(Vector3 metres) =>
        MathF.Abs(metres.X) <= EdgeMetres
        && MathF.Abs(metres.Z) <= EdgeMetres
        && metres.Y >= FloorMetres
        && metres.Y <= TopMetres;

    /// <summary>True when the point <paramref name="centimetres"/> (world coordinates) lies inside the box.</summary>
    public bool Contains(VoxelCoord centimetres)
    {
        var edge = (int)(EdgeMetres * VoxelConstants.CentimetresPerMetre);
        var floor = (int)(FloorMetres * VoxelConstants.CentimetresPerMetre);
        var top = (int)(TopMetres * VoxelConstants.CentimetresPerMetre);
        return centimetres.X >= -edge
            && centimetres.X <= edge
            && centimetres.Z >= -edge
            && centimetres.Z <= edge
            && centimetres.Y >= floor
            && centimetres.Y <= top;
    }

    /// <summary>True when an eye may be put at <paramref name="metres"/>: inside the box by <see cref="EyeInsetMetres"/>.</summary>
    public bool HoldsEye(Vector3 metres) =>
        MathF.Abs(metres.X) <= EdgeMetres - EyeInsetMetres
        && MathF.Abs(metres.Z) <= EdgeMetres - EyeInsetMetres
        && metres.Y >= FloorMetres + EyeInsetMetres
        && metres.Y <= TopMetres - EyeInsetMetres;

    /// <summary>The nearest point to <paramref name="metres"/> that <see cref="HoldsEye"/> allows. Finite numbers only.</summary>
    public Vector3 ClampEye(Vector3 metres) =>
        new(
            Math.Clamp(metres.X, -EdgeMetres + EyeInsetMetres, EdgeMetres - EyeInsetMetres),
            Math.Clamp(metres.Y, FloorMetres + EyeInsetMetres, TopMetres - EyeInsetMetres),
            Math.Clamp(metres.Z, -EdgeMetres + EyeInsetMetres, EdgeMetres - EyeInsetMetres)
        );

    private static float Metres(VoxelGrid grid, int voxel) =>
        grid.ToCentimetres(voxel) / (float)VoxelConstants.CentimetresPerMetre;
}
