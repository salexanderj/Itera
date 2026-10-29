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
/// by its transform, with the tags of their material; a ray through a column of many colliders meets what testing every
/// triangle meets; a removed collider is met no more; a ray allocates nothing; a ball
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
    public void A_ray_through_a_column_of_many_colliders_meets_the_nearest_triangle_of_all()
    {
        // Sixty boxes, turned and sized at random, stacked 50 m high over a few buckets: what a tower's plates make.
        var random = new Random(7);
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C, int Collider)>();
        var middles = new List<Vector3>();
        var (box, indices) = BoxTriangles();
        for (var i = 0; i < 60; i++)
        {
            var transform =
                Matrix4x4.CreateScale(
                    0.3f + 2 * random.NextSingle(),
                    0.1f + random.NextSingle(),
                    0.5f + random.NextSingle()
                )
                * Matrix4x4.CreateRotationY(MathF.Tau * random.NextSingle())
                * Matrix4x4.CreateTranslation(
                    12 * random.NextSingle() - 6,
                    50 * random.NextSingle(),
                    12 * random.NextSingle() - 6
                );
            var collider = _colliders.Add(box, indices, in transform);
            middles.Add(transform.Translation);
            for (var t = 0; t < indices.Length; t += 3)
            {
                triangles.Add(
                    (
                        Vector3.Transform(box[indices[t]], transform),
                        Vector3.Transform(box[indices[t + 1]], transform),
                        Vector3.Transform(box[indices[t + 2]], transform),
                        collider
                    )
                );
            }
        }

        var (hits, misses) = (0, 0);
        for (var i = 0; i < 500; i++)
        {
            var angle = MathF.Tau * random.NextSingle();
            var from = new Vector3(20 * MathF.Cos(angle), 60 * random.NextSingle(), 20 * MathF.Sin(angle));
            // Half towards a box's middle, half anywhere in the column.
            var towards =
                i % 2 == 0
                    ? middles[random.Next(middles.Count)]
                    : new Vector3(8 * random.NextSingle() - 4, 55 * random.NextSingle(), 8 * random.NextSingle() - 4);
            var direction = Vector3.Normalize(towards - from);
            var (nearest, second, collider) = (float.MaxValue, float.MaxValue, -1);
            foreach (var (a, b, c, owner) in triangles)
            {
                if (Meets(from, direction, a, b, c, out var t) && t < 40)
                {
                    (nearest, second, collider) =
                        t < nearest ? (t, nearest, owner) : (nearest, MathF.Min(second, t), collider);
                }
            }

            var met = _colliders.Raycast(from, direction, 40, out var hit);
            Assert.Equal(collider >= 0, met);
            if (!met)
            {
                misses++;
                continue;
            }

            hits++;
            Assert.Equal(nearest, hit.Distance, 1e-3f);
            if (second - nearest > 1e-3f)
            {
                Assert.Equal(collider, hit.Collider);
            }
        }

        // Rays that meet the column and rays that pass it, over and under its boxes.
        Assert.True(hits > 100 && misses > 100, $"{hits} hits, {misses} misses");
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

    /// <summary>A 1 m box round its middle, its twelve triangles.</summary>
    private static (Vector3[] Positions, uint[] Indices) BoxTriangles() =>
        (
            [
                new(-0.5f, -0.5f, -0.5f),
                new(0.5f, -0.5f, -0.5f),
                new(0.5f, 0.5f, -0.5f),
                new(-0.5f, 0.5f, -0.5f),
                new(-0.5f, -0.5f, 0.5f),
                new(0.5f, -0.5f, 0.5f),
                new(0.5f, 0.5f, 0.5f),
                new(-0.5f, 0.5f, 0.5f),
            ],
            [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5]
        );

    /// <summary>Where a ray meets a triangle from either side (Möller and Trumbore's test), each triangle alone.</summary>
    private static bool Meets(Vector3 from, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        var (e1, e2) = (b - a, c - a);
        var p = Vector3.Cross(direction, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f)
        {
            return false;
        }

        var s = from - a;
        var u = Vector3.Dot(s, p) / det;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(direction, q) / det;
        t = Vector3.Dot(e2, q) / det;
        return u >= 0 && v >= 0 && u + v <= 1 && t >= 0;
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
