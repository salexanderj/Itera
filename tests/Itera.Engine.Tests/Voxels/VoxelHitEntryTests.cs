using System.Numerics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Tests.Voxels;

/// <summary>
/// <see cref="VoxelHit.Entry"/> on a world of 10 cm voxels: the centimetre where a ray entered the struck voxel, half a
/// centimetre in, not the voxel's low corner up to 10 cm away; inside the struck voxel, so its material is read there.
/// </summary>
public sealed class VoxelHitEntryTests : IDisposable
{
    private readonly VoxelWorld _world = TestWorlds.Load(
        new DensityTerrainGenerator(new VoxelGrid(10)),
        new Vector3(1, 2, 1),
        radiusMetres: 3
    );

    public void Dispose() => _world.Dispose();

    [Fact]
    public void A_ray_straight_down_enters_the_floors_top_voxel_where_it_meets_it()
    {
        // The floor's top solid voxel is 140 to 150 cm (its surface at 152.2 cm lies in the air voxel above).
        var origin = new Vector3(0.537f, 2.5f, 0.462f);
        Assert.True(_world.Raycast(origin, -Vector3.UnitY, 5, out var hit));

        Assert.Equal(new VoxelCoord(50, 140, 40), hit.Voxel);
        Assert.Equal(new VoxelCoord(53, 149, 46), hit.Entry(origin, -Vector3.UnitY));
    }

    [Fact]
    public void A_slanted_ray_enters_half_a_centimetre_along_itself_inside_the_struck_voxel()
    {
        var origin = new Vector3(0.21f, 2.3f, 1.13f);
        var direction = new Vector3(0.6f, -0.7f, 0.2f);
        Assert.True(_world.Raycast(origin, direction, 5, out var hit));

        var entry = hit.Entry(origin, direction);
        var expected = origin + Vector3.Normalize(direction) * (hit.DistanceMetres + 0.005f);
        Assert.Equal(VoxelCoord.FromMetres(expected), entry);
        // Inside the struck voxel, where the material is read.
        Assert.Equal(hit.Voxel.X, entry.X / 10 * 10);
        Assert.Equal(hit.Voxel.Y, entry.Y / 10 * 10);
        Assert.Equal(hit.Voxel.Z, entry.Z / 10 * 10);
        Assert.True(_world.TryGetMaterial(entry, out var material));
        Assert.Equal(hit.Material, material);
    }
}
