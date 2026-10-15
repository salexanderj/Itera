using System.Numerics;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="ThirdPersonCamera"/>: the camera behind the right shoulder at its distance, orbiting it with the look's
/// pitch, over the left one mirrored and between them part of the way; pulled in short of a wall behind and of one at
/// the shoulder, a mesh collider's too; the aim from the eye towards what the camera's middle meets, which the eye's own
/// look would miss, a mesh collider among it, and a character's hit shape but the aimer's own; the wheel within its
/// limits; and no allocation.
/// </summary>
public sealed class ThirdPersonCameraTests : IDisposable
{
    private static readonly Vector3 s_eye = new(0, 1.6f, 0);
    private readonly VoxelWorld _voxels = LoadWorld();

    public void Dispose() => _voxels.Dispose();

    [Fact]
    public void The_camera_sits_behind_the_right_shoulder_and_orbits_it_with_the_pitch()
    {
        // Looking along -z: right is +x, behind is +z.
        var level = ThirdPersonCamera.Position(_voxels, s_eye, 0, 0, 2.5f);
        var pivot = s_eye + new Vector3(ThirdPersonCamera.RightMetres, ThirdPersonCamera.UpMetres, 0);
        Assert.Equal(pivot + new Vector3(0, 0, 2.5f), level);

        // Looking down, the camera rises behind the pivot, the same distance from it.
        var down = ThirdPersonCamera.Position(_voxels, s_eye, 0, -0.5f, 2.5f);
        Assert.Equal(2.5f, Vector3.Distance(pivot, down), 3);
        Assert.True(down.Y > pivot.Y + 1, $"{down}");

        // Turned a quarter left (yaw +90 degrees, looking along -x): right is -z, behind is +x.
        var turned = ThirdPersonCamera.Position(_voxels, s_eye, MathF.PI / 2, 0, 2.5f);
        Assert.Equal(2.5f, turned.X, 3);
        Assert.Equal(-ThirdPersonCamera.RightMetres, turned.Z, 3);

        // Without voxels, nothing pulls it in.
        Assert.Equal(level, ThirdPersonCamera.Position(null, s_eye, 0, 0, 2.5f));
    }

    [Fact]
    public void The_left_shoulder_mirrors_the_right_and_a_share_between_goes_part_of_the_way()
    {
        var right = ThirdPersonCamera.Position(null, s_eye, 0, 0, 2.5f);
        var left = ThirdPersonCamera.Position(null, s_eye, 0, 0, 2.5f, left: 1);
        var between = ThirdPersonCamera.Position(null, s_eye, 0, 0, 2.5f, left: 0.5f);

        Assert.Equal(right with { X = -right.X }, left);
        Assert.Equal(right with { X = 0 }, between);
        Assert.Equal(-ThirdPersonCamera.RightMetres, left.X, 5);
    }

    /// <summary>A mesh collider's wall behind pulls the camera in short of it; one ahead is where the aim points.</summary>
    [Fact]
    public void A_mesh_colliders_wall_pulls_the_camera_in_and_stops_the_aim()
    {
        using var harness = new PhysicsTestHarness();
        using var colliders = new Itera.Engine.Physics.MeshColliders(
            harness.Physics,
            NullLogger<Itera.Engine.Physics.MeshColliders>.Instance
        );
        Vector3[] quad = [new(-5, 0, 0), new(5, 0, 0), new(5, 5, 0), new(-5, 5, 0)];
        uint[] triangles = [0, 1, 2, 0, 2, 3];
        colliders.Add(quad, triangles, Matrix4x4.CreateTranslation(0, 0, 1.5f), 0);
        colliders.Add(quad, triangles, Matrix4x4.CreateTranslation(0, 0, -10), 0);

        var behind = ThirdPersonCamera.Position(null, s_eye, 0, 0, 2.5f, meshes: colliders);
        var aim = ThirdPersonCamera.Aim(null, s_eye, 0, 0, 2.5f, meshes: colliders);

        Assert.Equal(1.5f - ThirdPersonCamera.WallGapMetres, behind.Z, 3);
        var met = new Vector3(ThirdPersonCamera.RightMetres, s_eye.Y + ThirdPersonCamera.UpMetres, -10);
        Assert.True(Vector3.Distance(Vector3.Normalize(met - s_eye), aim) < 1e-4f, $"{aim}");
    }

    /// <summary>
    /// Through the match's shared ray the aim and the look stop at a solid model (a gate's leaf 10 m ahead), which the aim
    /// over the voxels, objects, and colliders alone passes.
    /// </summary>
    [Fact]
    public void Through_the_shared_ray_the_aim_stops_at_a_solid_model()
    {
        using var harness = new PhysicsTestHarness();
        using var colliders = new Itera.Engine.Physics.MeshColliders(
            harness.Physics,
            NullLogger<Itera.Engine.Physics.MeshColliders>.Instance
        );
        var surfaces = new Itera.Engine.Physics.CollisionSources(
            colliders,
            new Itera.Engine.Voxels.NoVoxelSurfaceTags(),
            harness.Voxels,
            bodies: harness.BodyVoxels,
            worldOrigin: harness.Voxels.Origin
        );
        var chunk = new byte[VoxelConstants.ChunkVoxels];
        for (var z = 0; z < 10; z++)
        {
            for (var y = 0; y < 50; y++)
            {
                for (var x = 0; x < 100; x++)
                {
                    chunk[x + VoxelConstants.ChunkSize * (y + VoxelConstants.ChunkSize * z)] = 1;
                }
            }
        }

        var solids = harness.BodyVoxels.Solids;
        var leaf = solids.Add(
            Itera.Engine.Physics.SolidColumns.Build(chunk, new VoxelCoord(100, 50, 10)),
            0,
            Vector3.Zero,
            unitCentimetres: 10
        );
        solids.SetPose(leaf, new Itera.Engine.Maths.WorldPoint(-5, 0, -10.5), 0);
        var none = new Itera.Engine.Physics.IgnoreEntity(Arch.Core.Entity.Null);

        var point = ThirdPersonCamera.AimPoint(surfaces, s_eye, 0, 0, 2.5f, 0, null, 0, in none);
        var look = ThirdPersonCamera.LookPoint(surfaces, s_eye, 0, 0, null, 0, in none);
        var past = ThirdPersonCamera.AimPoint(harness.Voxels, s_eye, 0, 0, 2.5f);

        Assert.Equal(-9.5f, point.Z, 3);
        Assert.Equal(-9.5f, look.Z, 3);
        Assert.True(past.Z < -50, $"{past}");
        solids.Remove(leaf);
    }

    [Fact]
    public void A_characters_hit_shape_on_the_crosshair_stops_the_aim_and_the_aimers_own_does_not()
    {
        using var world = Arch.Core.World.Create();
        var aimer = world.Create();
        var other = world.Create();
        var bodies = new Itera.Engine.Physics.HitHistory(new Itera.Engine.Maths.WorldOrigin());
        // A character 20 m off where the right shoulder's crosshair covers it, and the aimer's own body round its eye.
        var met = new Vector3(ThirdPersonCamera.RightMetres, s_eye.Y + ThirdPersonCamera.UpMetres, -20);
        bodies.Record(other, 4, [new(met + new Vector3(0, -0.5f, 0), met + new Vector3(0, 0.5f, 0), 0.3f, 1)]);
        bodies.Record(aimer, 4, [new(new Vector3(0, 0, 0), s_eye + Vector3.UnitY, 0.4f, 1)]);

        var past = ThirdPersonCamera.Aim(null, s_eye, 0, 0, 2.5f);
        var aim = ThirdPersonCamera.Aim(null, s_eye, 0, 0, 2.5f, bodies: bodies, tick: 4, ignore: aimer);
        var early = ThirdPersonCamera.Aim(null, s_eye, 0, 0, 2.5f, bodies: bodies, tick: 40, ignore: aimer);

        // To the front of the capsule: from the eye along the aim, the round passes through the character.
        var front = met + new Vector3(0, 0, 0.3f);
        Assert.True(Vector3.Distance(Vector3.Normalize(front - s_eye), aim) < 1e-3f, $"{aim}");
        // Without it, the aim goes on to its farthest, and the line from the eye passes the character 0.4 m aside.
        var beside = s_eye + past * (20 / -past.Z);
        Assert.InRange(MathF.Abs(beside.X - met.X), 0.35f, 0.45f);
        // At a tick where no character was, nothing stops it.
        Assert.True(Vector3.Distance(past, early) < 1e-6f);
    }

    [Fact]
    public void A_filter_lets_the_aim_pass_the_characters_it_leaves_out()
    {
        using var world = Arch.Core.World.Create();
        var friend = world.Create();
        var foe = world.Create();
        var bodies = new Itera.Engine.Physics.HitHistory(new Itera.Engine.Maths.WorldOrigin());
        var near = new Vector3(ThirdPersonCamera.RightMetres, s_eye.Y + ThirdPersonCamera.UpMetres, -10);
        var far = new Vector3(ThirdPersonCamera.RightMetres, s_eye.Y + ThirdPersonCamera.UpMetres, -30);
        bodies.Record(friend, 4, [new(near + new Vector3(0, -0.5f, 0), near + new Vector3(0, 0.5f, 0), 0.3f, 1)]);
        bodies.Record(foe, 4, [new(far + new Vector3(0, -0.5f, 0), far + new Vector3(0, 0.5f, 0), 0.3f, 1)]);
        var passes = new Passing(friend);

        var stopped = ThirdPersonCamera.AimPoint(null, s_eye, 0, 0, 2.5f, bodies: bodies, tick: 4);
        var past = ThirdPersonCamera.AimPoint(null, s_eye, 0, 0, 2.5f, null, null, 0, bodies, 4, in passes);
        var aim = ThirdPersonCamera.Aim(null, s_eye, 0, 0, 2.5f, null, null, 0, bodies, 4, in passes);
        var look = ThirdPersonCamera.LookPoint(null, s_eye, 0, 0, null, null, bodies, 4, in passes);

        Assert.True(Vector3.Distance(near + new Vector3(0, 0, 0.3f), stopped) < 1e-3f, $"{stopped}");
        Assert.True(Vector3.Distance(far + new Vector3(0, 0, 0.3f), past) < 1e-3f, $"{past}");
        Assert.True(Vector3.Distance(Vector3.Normalize(past - s_eye), aim) < 1e-5f, $"{aim}");
        // Straight along the look from the eye, nothing is in the way of either: the farthest point.
        Assert.Equal(ThirdPersonCamera.AimMetres, Vector3.Distance(s_eye, look), 2);

        var start = SystemAllocation.StartMeasurement();
        for (var i = 0; i < 100; i++)
        {
            ThirdPersonCamera.AimPoint(null, s_eye, 0, 0, 2.5f, null, null, 0, bodies, 4, in passes);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    /// <summary>A filter that leaves out one character.</summary>
    private readonly record struct Passing(Arch.Core.Entity Left) : Itera.Engine.Physics.IHitFilter
    {
        public bool Includes(Arch.Core.Entity entity) => entity != Left;
    }

    [Fact]
    public void The_aim_point_is_where_the_crosshair_meets_a_shape_and_the_look_point_where_the_look_does()
    {
        using var world = Arch.Core.World.Create();
        var aimer = world.Create();
        var other = world.Create();
        var bodies = new Itera.Engine.Physics.HitHistory(new Itera.Engine.Maths.WorldOrigin());
        var met = new Vector3(ThirdPersonCamera.RightMetres, s_eye.Y + ThirdPersonCamera.UpMetres, -20);
        bodies.Record(other, 4, [new(met + new Vector3(0, -0.5f, 0), met + new Vector3(0, 0.5f, 0), 0.3f, 1)]);
        bodies.Record(aimer, 4, [new(new Vector3(0, 0, 0), s_eye + Vector3.UnitY, 0.4f, 1)]);
        var straight = s_eye + new Vector3(0, 0, -30);
        bodies.Record(world.Create(), 4, [new(straight - Vector3.UnitY, straight + Vector3.UnitY, 0.3f, 1)]);

        var point = ThirdPersonCamera.AimPoint(null, s_eye, 0, 0, 2.5f, bodies: bodies, tick: 4, ignore: aimer);
        var aim = ThirdPersonCamera.Aim(null, s_eye, 0, 0, 2.5f, bodies: bodies, tick: 4, ignore: aimer);
        var look = ThirdPersonCamera.LookPoint(null, s_eye, 0, 0, bodies: bodies, tick: 4, ignore: aimer);
        var open = ThirdPersonCamera.LookPoint(null, s_eye, MathF.PI, 0);

        // The crosshair's ray meets the front of the capsule; the aim is the direction from the eye to that point.
        Assert.True(Vector3.Distance(met + new Vector3(0, 0, 0.3f), point) < 1e-3f, $"{point}");
        Assert.True(Vector3.Distance(Vector3.Normalize(point - s_eye), aim) < 1e-5f, $"{aim}");
        // From the eye straight along the look, the second character, past the aimer's own.
        Assert.True(Vector3.Distance(straight + new Vector3(0, 0, 0.3f), look) < 1e-3f, $"{look}");
        Assert.Equal(ThirdPersonCamera.AimMetres, Vector3.Distance(s_eye, open), 2);
    }

    [Fact]
    public void A_wall_behind_or_at_the_shoulder_pulls_the_camera_in_short_of_it()
    {
        // A wall 1.5 m behind the eye, its near face at z = 1.5.
        Wall(new VoxelCoord(-200, 0, 150), new VoxelCoord(400, 300, 20));
        var behind = ThirdPersonCamera.Position(_voxels, s_eye, 0, 0, 2.5f);
        Assert.Equal(1.5f - ThirdPersonCamera.WallGapMetres, behind.Z, 2);
        Assert.Equal(ThirdPersonCamera.RightMetres, behind.X, 3);

        // A wall 0.3 m to the right: the pivot stops short of it along the line from the eye, and the camera is behind
        // that.
        Wall(new VoxelCoord(30, 0, -100), new VoxelCoord(20, 300, 240));
        var shoulder = ThirdPersonCamera.Position(_voxels, s_eye, 0, 0, 2.5f);
        var across =
            ThirdPersonCamera.RightMetres
            / new Vector2(ThirdPersonCamera.RightMetres, ThirdPersonCamera.UpMetres).Length();
        Assert.Equal(0.3f - ThirdPersonCamera.WallGapMetres * across, shoulder.X, 2);
        Assert.Equal(1.5f - ThirdPersonCamera.WallGapMetres, shoulder.Z, 2);
    }

    [Fact]
    public void The_aim_goes_from_the_eye_to_what_the_screens_middle_points_at()
    {
        // A wall 5 m ahead: the camera's middle meets it over the shoulder, right of and above where the eye's own look
        // would; the aim goes from the eye to that point.
        Wall(new VoxelCoord(-200, 0, -520), new VoxelCoord(400, 400, 20));
        var aim = ThirdPersonCamera.Aim(_voxels, s_eye, 0, 0, 2.5f);
        var point = s_eye + new Vector3(ThirdPersonCamera.RightMetres, ThirdPersonCamera.UpMetres, -5);
        var expected = Vector3.Normalize(point - s_eye);
        Assert.True(Vector3.Dot(aim, expected) > 0.99999f, $"{aim} against {expected}");
        Assert.True(aim.X > 0.05f && aim.Y > 0.03f, $"{aim}");

        // Nothing ahead: along the camera's middle, 200 m out, nearly the look itself.
        var open = ThirdPersonCamera.Aim(_voxels, s_eye, MathF.PI, 0, 2.5f);
        Assert.True(Vector3.Dot(open, ThirdPersonCamera.Forward(MathF.PI, 0)) > 0.999f, $"{open}");
    }

    /// <summary>
    /// A wall 30 m ahead, past the level-0 ring where only the coarser level is loaded, stops the aim as it stops a round:
    /// the aim passed through it to 200 m, 0.4 to 0.5 m off the round's point.
    /// </summary>
    [Fact]
    public void A_wall_past_the_finest_ring_stops_the_aim()
    {
        using var voxels = LoadWorld(levels: 2, radiusMetres: 16);
        voxels.Apply(
            VoxelEdit.Box(
                new VoxelCoord(-500, 0, -3010),
                new VoxelCoord(1000, 500, 10),
                VoxelEditOperation.Set,
                FlatGenerator.Stone
            )
        );
        voxels.ApplyPendingEdits(int.MaxValue);

        var aim = ThirdPersonCamera.Aim(voxels, s_eye, 0, 0, 2.5f);

        // From the eye towards the camera's ray at the wall's face, 30 m out.
        var camera = ThirdPersonCamera.Position(voxels, s_eye, 0, 0, 2.5f);
        var wall = new Vector3(camera.X, camera.Y, -30);
        Assert.True(Vector3.Dot(aim, Vector3.Normalize(wall - s_eye)) > 0.99999f, $"aims along {aim}");
    }

    [Fact]
    public void The_wheel_moves_the_camera_within_its_limits()
    {
        Assert.Equal(2.25f, ThirdPersonCamera.Wheel(2.5f, 1), 4);
        Assert.Equal(3.0f, ThirdPersonCamera.Wheel(2.5f, -2), 4);
        Assert.Equal(ThirdPersonCamera.LeastMetres, ThirdPersonCamera.Wheel(2.5f, 40));
        Assert.Equal(ThirdPersonCamera.MostMetres, ThirdPersonCamera.Wheel(2.5f, -40));
    }

    [Fact]
    public void Placing_and_aiming_allocate_nothing()
    {
        Wall(new VoxelCoord(-200, 0, -520), new VoxelCoord(400, 400, 20));
        using var world = Arch.Core.World.Create();
        var other = world.Create();
        var bodies = new Itera.Engine.Physics.HitHistory(new Itera.Engine.Maths.WorldOrigin());
        bodies.Record(other, 1, [new(new Vector3(0.45f, 1, -3), new Vector3(0.45f, 3, -3), 0.3f, 1)]);
        ThirdPersonCamera.Aim(_voxels, s_eye, 0, 0, 2.5f);
        ThirdPersonCamera.Aim(_voxels, s_eye, 0, 0, 2.5f, bodies: bodies, tick: 1);

        var start = SystemAllocation.StartMeasurement();
        ThirdPersonCamera.Position(_voxels, s_eye, 0.3f, -0.2f, 2.5f);
        ThirdPersonCamera.Aim(_voxels, s_eye, 0, 0, 2.5f);
        ThirdPersonCamera.Aim(_voxels, s_eye, 0, 0, 2.5f, bodies: bodies, tick: 1, ignore: other);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    private void Wall(VoxelCoord min, VoxelCoord size)
    {
        _voxels.Apply(VoxelEdit.Box(min, size, VoxelEditOperation.Set, FlatGenerator.Stone));
        _voxels.ApplyPendingEdits(int.MaxValue);
    }

    private static VoxelWorld LoadWorld(int levels = 1, int radiusMetres = 8)
    {
        var options = new EngineOptions
        {
            Voxels = new VoxelSettings
            {
                StreamingRadiusMetres = radiusMetres,
                Levels = levels,
                WorkerThreads = 2,
            },
        };
        var voxels = new VoxelWorld(Options.Create(options), new FlatGenerator(), NullLogger<VoxelWorld>.Instance);
        voxels.Initialize();
        voxels.SetCenters([Vector3.Zero]);
        VoxelLoading.WaitUntilLoaded(voxels);

        return voxels;
    }
}
