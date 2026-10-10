using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// <see cref="CollisionSources.Raycast"/>: over a flat stone floor (y below 0) and a mesh collider above it, the nearer
/// wins, each hit with its source's tags (the stone's through <see cref="IVoxelSurfaceTags"/>, the collider's material's);
/// without a voxel world, only the colliders.
/// </summary>
public sealed class CollisionSourcesTests : IDisposable
{
    private const uint StoneTag = 4;
    private const uint ShelfTag = 8;

    private readonly PhysicsTestHarness _harness = new();
    private readonly MeshColliders _colliders;

    public CollisionSourcesTests() =>
        _colliders = new MeshColliders(_harness.Physics, NullLogger<MeshColliders>.Instance);

    public void Dispose()
    {
        _colliders.Dispose();
        _harness.Dispose();
    }

    [Fact]
    public void The_nearer_of_a_voxel_and_a_mesh_wins_with_its_tags()
    {
        var sources = new CollisionSources(_colliders, new StoneTags(), _harness.Voxels);
        var shelf = AddShelf();

        Assert.True(sources.Raycast(new Vector3(0.2f, 2, 0.2f), -Vector3.UnitY, 10, out var mesh));
        _colliders.Remove(shelf);
        Assert.True(sources.Raycast(new Vector3(0.2f, 2, 0.2f), -Vector3.UnitY, 10, out var voxel));

        Assert.Equal((SurfaceSource.Mesh, 1.5f, ShelfTag), (mesh.Source, mesh.Distance, mesh.Tags));
        Assert.Equal(
            (SurfaceSource.Voxel, StoneTag, FlatTestGenerator.Stone),
            (voxel.Source, voxel.Tags, voxel.Material)
        );
        Assert.Equal(2, voxel.Distance, 1e-3f);
        Assert.Equal(Vector3.UnitY, voxel.Normal);
    }

    [Fact]
    public void Without_a_voxel_world_only_the_colliders_are_met()
    {
        var sources = new CollisionSources(_colliders, new StoneTags());
        AddShelf();

        Assert.True(sources.Raycast(new Vector3(0.2f, 2, 0.2f), -Vector3.UnitY, 10, out var hit));
        Assert.False(sources.Raycast(new Vector3(5, 2, 5), -Vector3.UnitY, 10, out _));
        Assert.Equal(SurfaceSource.Mesh, hit.Source);
    }

    /// <summary>A 1 m square from (0, 0) to (1, 1) over x and z, half a metre up, its front up, tagged as a shelf.</summary>
    private int AddShelf()
    {
        Vector3[] positions = [new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0)];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        return _colliders.Add(positions, indices, Matrix4x4.CreateTranslation(0, 0.5f, 0), ShelfTag);
    }

    private sealed class StoneTags : IVoxelSurfaceTags
    {
        public uint TagsOf(byte material) => material == FlatTestGenerator.Stone ? StoneTag : 0;
    }
}
