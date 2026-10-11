using System.Numerics;
using Itera.Engine.Physics;
using Itera.Engine.Tests.Systems;
using Itera.Engine.Tests.Voxels;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// The ground a moved body stands on (<see cref="CharacterMoveResult.GroundMaterial"/> and
/// <see cref="CharacterMoveResult.GroundTags"/>): a mesh floor's collider material, read as it is now, and its tags; with
/// the feet's centre past the floor's edge, the floor beside it; voxels' physics material by their voxel material and
/// their surface tags; none in the air; and reading it allocates nothing.
/// </summary>
public sealed class GroundMaterialTests
{
    private static readonly CharacterShape Body = new(1.7f, 0.3f, 0.3f);

    [Fact]
    public void On_a_mesh_floor_it_reads_its_colliders_material_now_and_its_tags()
    {
        var materials = new PhysicsMaterials();
        var ice = materials.Register("ice", new PhysicsMaterial(0.05f, 0));
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance, null, materials);
        physics.Initialize();
        using var colliders = new MeshColliders(physics, NullLogger<MeshColliders>.Instance);
        var floor = new TestTriangles().Ground(-4, -4, 0, 4, 0).AddTo(colliders, tags: 4);
        colliders.SetSettings(floor, new PhysicsBodySettings { Material = ice });
        var mover = new VoxelCharacterMover(new NothingPassable(), colliders, 10) { Materials = materials };

        var onIt = Stand(mover, new Vector3(-1, 0, 0));
        // The feet's centre 10 cm past the edge, the body still on the floor.
        var overEdge = Stand(mover, new Vector3(0.1f, 0, 0));
        colliders.SetSettings(floor, default);
        var changed = Stand(mover, new Vector3(-1, 0, 0));
        var inAir = Stand(mover, new Vector3(-1, 2, 0));

        Assert.True(onIt.Grounded);
        Assert.Equal((ice, 4u), (onIt.GroundMaterial, onIt.GroundTags));
        Assert.True(overEdge.Grounded);
        Assert.Equal((ice, 4u), (overEdge.GroundMaterial, overEdge.GroundTags));
        Assert.Equal((PhysicsMaterialId.Default, 4u), (changed.GroundMaterial, changed.GroundTags));
        Assert.False(inAir.Grounded);
        Assert.Equal((PhysicsMaterialId.Default, 0u), (inAir.GroundMaterial, inAir.GroundTags));
    }

    [Fact]
    public void On_voxels_it_reads_their_voxel_materials_physics_material_and_tags()
    {
        var materials = new PhysicsMaterials();
        var mud = materials.Register("mud", new PhysicsMaterial(0.6f, 0));
        materials.MapVoxel(FlatGenerator.Stone, mud);
        using var voxels = TestWorlds.Load(new FlatGenerator(), Vector3.Zero, radiusMetres: 3);
        var mover = new VoxelCharacterMover(voxels, new NothingPassable())
        {
            Materials = materials,
            SurfaceTags = new StoneTagged(),
        };

        var standing = Stand(mover, new Vector3(0.5f, 0, 0.5f));
        var plain = Stand(new VoxelCharacterMover(voxels, new NothingPassable()), new Vector3(0.5f, 0, 0.5f));

        Assert.True(standing.Grounded);
        Assert.Equal((mud, 8u), (standing.GroundMaterial, standing.GroundTags));
        Assert.Equal((PhysicsMaterialId.Default, 0u), (plain.GroundMaterial, plain.GroundTags));
    }

    [Fact]
    public void Reading_the_ground_allocates_nothing()
    {
        var materials = new PhysicsMaterials();
        materials.MapVoxel(FlatGenerator.Stone, materials.Register("mud", new PhysicsMaterial(0.6f, 0)));
        using var voxels = TestWorlds.Load(new FlatGenerator(), Vector3.Zero, radiusMetres: 3);
        var mover = new VoxelCharacterMover(voxels, new NothingPassable())
        {
            Materials = materials,
            SurfaceTags = new StoneTagged(),
        };
        Stand(mover, new Vector3(0.5f, 0, 0.5f));

        var before = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            Stand(mover, new Vector3(0.5f + i * 0.01f, 0, 0.5f));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>One still step of a body with its feet at <paramref name="feet"/>.</summary>
    private static CharacterMoveResult Stand(VoxelCharacterMover mover, Vector3 feet)
    {
        var velocity = Vector3.Zero;
        return mover.Move(ref feet, ref velocity, in Body, 1f / 60);
    }

    private sealed class StoneTagged : IVoxelSurfaceTags
    {
        public uint TagsOf(byte material) => material == FlatGenerator.Stone ? 8u : 0;
    }
}
