using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using Itera.Engine.Assets;
using Itera.Engine.Physics;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Physics;

/// <summary>
/// <see cref="MeshColliders"/>: rays meet a collider's triangles from either side, nearest first across buckets, placed
/// by its transform, with the tags of their material; a removed collider is met no more; a ray allocates nothing; a ball
/// lands on a collider's Bepu static whatever its voxel level, and falls through once it is removed.
/// </summary>
public sealed class MeshCollidersTests : IDisposable
{
    private const uint Rock = 1;
    private const uint Climbable = 2;

    private readonly PhysicsWorld _physics = new(NullLogger<PhysicsWorld>.Instance);
    private readonly MeshColliders _colliders;

    public MeshCollidersTests()
    {
        _physics.Initialize();
        _colliders = new MeshColliders(_physics, NullLogger<MeshColliders>.Instance);
    }

    public void Dispose()
    {
        _colliders.Dispose();
        _physics.Dispose();
    }

    [Fact]
    public void A_ray_meets_a_triangle_from_either_side_with_its_materials_tags()
    {
        _colliders.Add(Ground(), Matrix4x4.Identity, TagsOf);

        // The quad's diagonal runs from (0, 0) to (1, 1): the first triangle lies where x < z, the second where x > z.
        Assert.True(_colliders.Raycast(new Vector3(0.25f, 3, 0.75f), -Vector3.UnitY, 10, out var above));
        Assert.True(_colliders.Raycast(new Vector3(0.75f, -2, 0.25f), Vector3.UnitY, 10, out var below));

        Assert.Equal(3, above.Distance, 1e-5f);
        Assert.Equal(new Vector3(0.25f, 0, 0.75f), above.Point);
        Assert.Equal(Vector3.UnitY, above.Normal);
        // The quad's two triangles: one rock, one climbable.
        Assert.Equal(Rock, above.Tags);
        Assert.Equal(-Vector3.UnitY, below.Normal);
        Assert.Equal(Climbable, below.Tags);
        Assert.Equal((SurfaceSource.Mesh, 0), (above.Source, above.Collider));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void A_ray_across_buckets_meets_the_nearest_wall(int way)
    {
        // Walls across x at 10 m and 30 m, and one 100 m wide (in many buckets) at 50 m, each way.
        foreach (var x in new[] { 10f, 30f, 50f })
        {
            var wide = x == 50 ? 50f : 1f;
            _colliders.Add(Wall(wide), Matrix4x4.CreateTranslation(way * x, 0, 0));
        }

        var origin = new Vector3(0, 0.5f, 0.25f);
        Assert.True(_colliders.Raycast(origin, new Vector3(way, 0, 0), 100, out var first));
        Assert.Equal(10, first.Distance, 1e-4f);
        Assert.True(
            _colliders.Raycast(origin + new Vector3(way * 20, 0, 0), new Vector3(way, 0.01f, 0), 100, out var second)
        );
        Assert.Equal(10, second.Distance, 1e-2f);
        Assert.True(_colliders.Raycast(new Vector3(way * 40, 0.5f, 40), new Vector3(way, 0, 0), 100, out var far));
        Assert.Equal(10, far.Distance, 1e-4f);
        Assert.False(_colliders.Raycast(origin, new Vector3(way, 0, 0), 9, out _));
    }

    [Fact]
    public void A_collider_is_placed_by_its_transform_and_a_removed_one_is_met_no_more()
    {
        var transform = Matrix4x4.CreateScale(4) * Matrix4x4.CreateTranslation(100, 5, -100);
        var collider = _colliders.Add(Ground(), in transform, TagsOf);

        // Four times the size, from (100, -100) to (104, -96) over x and z, 5 m up.
        Assert.True(_colliders.Raycast(new Vector3(103, 9, -97), -Vector3.UnitY, 10, out var hit));
        Assert.Equal(5, hit.Point.Y, 1e-4f);
        Assert.False(_colliders.Raycast(new Vector3(105, 9, -97), -Vector3.UnitY, 10, out _));

        _colliders.Remove(collider);

        Assert.False(_colliders.Raycast(new Vector3(103, 9, -97), -Vector3.UnitY, 10, out _));
        Assert.Equal(0, _colliders.Count);
    }

    [Fact]
    public void A_ray_allocates_nothing()
    {
        _colliders.Add(Ground(), Matrix4x4.CreateScale(50), TagsOf);
        _colliders.Raycast(Vector3.UnitY, -Vector3.UnitY, 10, out _);

        var bytes = SystemAllocation.BytesAllocatedWithoutCollection(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                _colliders.Raycast(new Vector3(i % 40, 3, 7), new Vector3(1, -1, 0.3f), 30, out _);
            }
        });

        Assert.Equal(0, bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_ball_lands_on_a_colliders_static_whatever_its_voxel_level(int level)
    {
        var collider = _colliders.Add(Ground(), Matrix4x4.CreateScale(20) * Matrix4x4.CreateTranslation(-10, 0, -10));
        var ball = DropBall(level);

        Step(120);
        var rested = _physics.Simulation.Bodies[ball].Pose.Position.Y;
        _colliders.Remove(collider);
        _physics.Simulation.Awakener.AwakenBody(ball);
        Step(60);

        Assert.InRange(rested, 0.15f, 0.3f);
        Assert.True(_physics.Simulation.Bodies[ball].Pose.Position.Y < -1);
    }

    [Fact]
    public void A_collider_added_before_the_physics_exists_is_refused()
    {
        using var physics = new PhysicsWorld(NullLogger<PhysicsWorld>.Instance);
        using var colliders = new MeshColliders(physics, NullLogger<MeshColliders>.Instance);

        Assert.Throws<InvalidOperationException>(() => colliders.Add(Ground(), Matrix4x4.Identity));
    }

    private static uint TagsOf(string material) => material == "rock" ? Rock : Climbable;

    private BodyHandle DropBall(int level)
    {
        var sphere = new Sphere(0.2f);
        var ball = _physics.Simulation.Bodies.Add(
            BodyDescription.CreateDynamic(
                new RigidPose(new Vector3(0, 2, 0)),
                sphere.ComputeInertia(1),
                new CollidableDescription(_physics.Simulation.Shapes.Add(sphere), 0.1f),
                new BodyActivityDescription(0.01f)
            )
        );
        _physics.Kinds.Set(ball, BodyKind.Loose, level);
        return ball;
    }

    private void Step(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            _physics.Step(1f / 60);
        }
    }

    /// <summary>
    /// A 1 m quad at y = 0 from (0, 0) to (1, 1) over x and z, its front up: one triangle of material "rock", the other
    /// of "moss".
    /// </summary>
    private static MeshAsset Ground()
    {
        Vector3[] corners = [new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0)];
        var vertices = new MeshAssetVertex[4];
        for (var i = 0; i < 4; i++)
        {
            vertices[i] = MeshAssetVertex.Pack(
                corners[i],
                Vector3.UnitY,
                new Vector4(1, 0, 0, 1),
                Vector2.Zero,
                Vector2.Zero,
                Vector4.One
            );
        }

        return new MeshAsset(
            "ground",
            vertices,
            [0, 1, 2, 0, 2, 3],
            [new MeshPrimitive(0, 3, 0), new MeshPrimitive(3, 3, 1)],
            [MeshMaterialData.Default with { Name = "rock" }, MeshMaterialData.Default with { Name = "moss" }],
            []
        );
    }

    /// <summary>A wall in the plane x = 0, <paramref name="halfWidth"/> metres each side along z, from y = 0 to 1 m.</summary>
    private static (Vector3[] Positions, uint[] Indices) WallTriangles(float halfWidth) =>
        (
            [new(0, 0, -halfWidth), new(0, 1, -halfWidth), new(0, 1, halfWidth), new(0, 0, halfWidth)],
            [0, 1, 2, 0, 2, 3]
        );

    private static MeshAsset Wall(float halfWidth)
    {
        var (positions, indices) = WallTriangles(halfWidth);
        var vertices = new MeshAssetVertex[positions.Length];
        for (var i = 0; i < positions.Length; i++)
        {
            vertices[i] = MeshAssetVertex.Pack(
                positions[i],
                Vector3.UnitX,
                new Vector4(0, 0, 1, 1),
                Vector2.Zero,
                Vector2.Zero,
                Vector4.One
            );
        }

        return new MeshAsset("wall", vertices, indices, [new MeshPrimitive(0, 6, 0)], [MeshMaterialData.Default], []);
    }
}
