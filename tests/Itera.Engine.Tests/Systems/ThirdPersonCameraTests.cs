using System.Numerics;
using Itera.Engine.Systems;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Itera.Engine.Tests.Systems;

/// <summary>
/// <see cref="ThirdPersonCamera"/>: the camera behind the right shoulder at its distance, orbiting it with the look's
/// pitch; pulled in short of a wall behind and of one at the shoulder; the aim from the eye towards what the camera's
/// middle meets, which the eye's own look would miss; the wheel within its limits; and no allocation.
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
        ThirdPersonCamera.Aim(_voxels, s_eye, 0, 0, 2.5f);

        var start = SystemAllocation.StartMeasurement();
        ThirdPersonCamera.Position(_voxels, s_eye, 0.3f, -0.2f, 2.5f);
        ThirdPersonCamera.Aim(_voxels, s_eye, 0, 0, 2.5f);
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
