using System.Numerics;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;

namespace Itera.Engine.Tests.Voxels;

public sealed class VoxelRaycastTests : IDisposable
{
    private const ulong Seed = 17;
    private readonly VoxelWorld _world;

    public VoxelRaycastTests()
    {
        var settings = new VoxelSettings
        {
            StreamingRadiusMetres = 3,
            Levels = 1,
            MemoryBudgetMegabytes = 64,
            WorkerThreads = 2,
            Seed = Seed,
        };
        _world = new VoxelWorld(settings, 0, new SlopeGenerator(), NullLogger<VoxelWorld>.Instance);
        _world.Initialize();
        _world.SetCenters([Vector3.Zero]);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_world.PendingColumns > 0 || _world.ResidentColumns == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The world did not load.");
            _world.Update(0);
            Thread.Sleep(1);
        }
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public void Straight_down_hits_the_ground_top_face_at_the_right_distance()
    {
        var ground = SlopeGenerator.Height(30, 40, Seed);
        var origin = new Vector3(0.305f, (ground + 170) / 100f, 0.405f);

        Assert.True(_world.Raycast(origin, -Vector3.UnitY, 10, out var hit));

        Assert.Equal(new VoxelCoord(30, ground - 1, 40), hit.Voxel);
        Assert.Equal(new VoxelCoord(0, 1, 0), hit.Normal);
        Assert.Equal(SlopeGenerator.Dirt, hit.Material);
        Assert.Equal(1.70f, hit.DistanceMetres, 3);
    }

    [Fact]
    public void Random_rays_hit_what_an_exact_walk_hits()
    {
        var random = new Random(31);
        int hits = 0,
            rays = 0;
        for (var i = 0; i < 1500; i++)
        {
            var (origin, direction) = RandomRay(random);
            var found = _world.Raycast(origin, direction, 5, out var hit);
            var expected = ExactWalk(origin, direction, 5, out var voxel, out var material);
            rays++;
            Assert.Equal(expected, found);
            if (found)
            {
                hits++;
                AssertNear(voxel, hit.Voxel, i);
                Assert.Equal(material, hit.Material);
            }
        }

        Assert.True(hits > rays / 4, $"{hits} hits of {rays}.");
    }

    [Fact]
    public void A_ray_through_a_dug_hole_hits_the_bottom_of_the_hole()
    {
        var ground = SlopeGenerator.Height(0, 0, Seed);
        _world.Apply(VoxelEdit.Sphere(new VoxelCoord(0, ground, 0), 40, VoxelEditOperation.Clear));
        var origin = new Vector3(0.005f, (ground + 100) / 100f, 0.005f);

        Assert.True(_world.Raycast(origin, -Vector3.UnitY, 5, out var hit));

        Assert.Equal(ground - 41, hit.Voxel.Y);
        Assert.Equal(new VoxelCoord(0, 1, 0), hit.Normal);
    }

    [Fact]
    public void Columns_that_are_not_resident_count_as_air()
    {
        // Level with the ground far out, where nothing is loaded: the ray passes above the slope near the origin
        // and then leaves the loaded columns.
        var origin = new Vector3(0, 10, 0);

        Assert.False(_world.Raycast(origin, Vector3.UnitX, 100, out _));
        Assert.False(_world.Raycast(new Vector3(40, -5, 40), -Vector3.UnitY, 20, out _));
    }

    [Fact]
    public void Casting_allocates_nothing()
    {
        var random = new Random(32);
        var rays = new (Vector3, Vector3)[200];
        for (var i = 0; i < rays.Length; i++)
        {
            rays[i] = RandomRay(random);
        }

        foreach (var (origin, direction) in rays)
        {
            _world.Raycast(origin, direction, 5, out _);
        }

        var before = SystemAllocation.StartMeasurement();
        foreach (var (origin, direction) in rays)
        {
            _world.Raycast(origin, direction, 5, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static (Vector3 Origin, Vector3 Direction) RandomRay(Random random)
    {
        var x = (float)(random.NextDouble() * 4 - 2);
        var z = (float)(random.NextDouble() * 4 - 2);
        var ground = SlopeGenerator.Height((int)(x * 100), (int)(z * 100), Seed) / 100f;
        var origin = new Vector3(x, ground + (float)(random.NextDouble() * 2 + 0.02), z);
        var pitch = -Math.Pow(random.NextDouble(), 2) * Math.PI / 2;
        var yaw = random.NextDouble() * Math.PI * 2;
        var direction = new Vector3(
            (float)(Math.Cos(yaw) * Math.Cos(pitch)),
            (float)Math.Sin(pitch),
            (float)(Math.Sin(yaw) * Math.Cos(pitch))
        );
        return (origin, direction);
    }

    /// <summary>A voxel-by-voxel walk (Amanatides and Woo) reading every voxel through <see cref="VoxelWorld.TryGetMaterial(VoxelCoord, out byte)"/>.</summary>
    private bool ExactWalk(
        Vector3 originMetres,
        Vector3 direction,
        float maxMetres,
        out VoxelCoord hit,
        out byte material
    )
    {
        var d = Vector3.Normalize(direction);
        double[] origin = [originMetres.X * 100.0, originMetres.Y * 100.0, originMetres.Z * 100.0];
        double[] dir = [d.X, d.Y, d.Z];
        var voxel = new int[3];
        var tMax = new double[3];
        var tDelta = new double[3];
        for (var a = 0; a < 3; a++)
        {
            voxel[a] = (int)Math.Floor(origin[a]);
            var boundary = voxel[a] + (dir[a] >= 0 ? 1 : 0);
            tMax[a] = dir[a] == 0 ? double.PositiveInfinity : (boundary - origin[a]) / dir[a];
            tDelta[a] = dir[a] == 0 ? double.PositiveInfinity : 1 / Math.Abs(dir[a]);
        }

        double t = 0;
        while (t <= maxMetres * 100.0)
        {
            var here = new VoxelCoord(voxel[0], voxel[1], voxel[2]);
            if (_world.TryGetMaterial(here, out material) && material != VoxelConstants.Air)
            {
                hit = here;
                return true;
            }

            var axis = tMax[0] < tMax[1] ? (tMax[0] < tMax[2] ? 0 : 2) : (tMax[1] < tMax[2] ? 1 : 2);
            t = tMax[axis];
            tMax[axis] += tDelta[axis];
            voxel[axis] += dir[axis] >= 0 ? 1 : -1;
        }

        hit = default;
        material = VoxelConstants.Air;
        return false;
    }

    /// <summary>The same voxel, or a neighbour when the ray passes exactly through an edge or corner.</summary>
    private static void AssertNear(VoxelCoord expected, VoxelCoord actual, int ray)
    {
        var far = Math.Max(
            Math.Abs(expected.X - actual.X),
            Math.Max(Math.Abs(expected.Y - actual.Y), Math.Abs(expected.Z - actual.Z))
        );
        Assert.True(far <= 1, $"Ray {ray}: the exact walk hit {expected}, the raycast hit {actual}.");
    }
}
