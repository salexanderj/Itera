using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="PlayArea"/>: round a voxel grid's bounds, or round bounds a game gives without terrain, with the margin on
/// every side; the default without terrain is a 10 cm grid's; a body has fallen out 10 m under the world's floor; bounds
/// that hold nothing are refused.
/// </summary>
public sealed class PlayAreaTests
{
    [Fact]
    public void Bounds_without_terrain_make_the_box_with_the_margin_round_them()
    {
        var area = new PlayArea(worldEdgeMetres: 200, worldFloorMetres: -20, worldTopMetres: 60, marginMetres: 100);

        Assert.Equal((200f, 300f), (area.WorldEdgeMetres, area.EdgeMetres));
        Assert.Equal((-20f, -120f, 160f), (area.WorldFloorMetres, area.FloorMetres, area.TopMetres));
        Assert.Equal(-30, area.FallLimitMetres);
        Assert.True(area.Contains(new Vector3(-300, 160, 299)));
        Assert.False(area.Contains(new Vector3(0, -121, 0)));
        Assert.True(area.HoldsEye(new Vector3(295, 0, 0)));
        Assert.False(area.HoldsEye(new Vector3(296, 0, 0)));
    }

    [Fact]
    public void The_default_without_terrain_is_a_10_cm_grids()
    {
        var grid = new PlayArea(new VoxelGrid(10));
        var area = PlayArea.WithoutTerrain;

        Assert.Equal(
            (grid.EdgeMetres, grid.FloorMetres, grid.TopMetres, grid.FallLimitMetres),
            (area.EdgeMetres, area.FloorMetres, area.TopMetres, area.FallLimitMetres)
        );
        Assert.Equal(512, area.WorldEdgeMetres);
        Assert.Equal(-51.2f, area.WorldFloorMetres, 1e-4f);
        Assert.Equal(102.4f + PlayArea.DefaultMarginMetres, area.TopMetres, 1e-3f);
        Assert.Equal(LooseBodies.FallLimitMetres(new VoxelGrid(10)), area.FallLimitMetres);
    }

    [Theory]
    [InlineData(0, -10, 10, 0)]
    [InlineData(100, 10, 10, 0)]
    [InlineData(100, -10, 10, -1)]
    [InlineData(float.NaN, -10, 10, 0)]
    [InlineData(100, float.NegativeInfinity, 10, 0)]
    public void Bounds_that_hold_nothing_are_refused(float edge, float floor, float top, float margin)
    {
        Assert.Throws<ArgumentException>(() => new PlayArea(edge, floor, top, margin));
    }
}
