using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Objects;
using Itera.Engine.Voxels;

namespace Itera.Engine.Systems;

/// <summary>
/// The third-person camera's place and aim. The camera orbits a pivot over the right shoulder (the eye plus
/// <see cref="RightMetres"/> to the look's level right and <see cref="UpMetres"/> up), <see cref="ThirdPersonView.Metres"/>
/// back along the look, pitch included; a voxel in the way (from the eye to the pivot, then from the pivot to the camera),
/// the world's or a voxel object's (<see cref="ObjectRays"/>, where given), pulls it in to <see cref="WallGapMetres"/>
/// short of it, at once both ways, so the fixed step's aim and the frame's camera agree. The aim goes from the eye towards
/// what the camera's middle ray meets first past the point level with the eye (nothing between the camera and the player
/// counts), in the voxels and the objects. The rays hit leaves, so a bush behind the player pulls the camera in front of
/// it. Every number is a placeholder. Allocates nothing.
/// </summary>
public static class ThirdPersonCamera
{
    private static readonly QueryDescription s_view = new QueryDescription().WithAll<LocalPlayer, ThirdPersonView>();

    /// <summary>How far to the right of the eye the pivot is, level, in metres.</summary>
    public const float RightMetres = 0.45f;

    /// <summary>How far above the eye the pivot is, in metres.</summary>
    public const float UpMetres = 0.3f;

    /// <summary>How far behind the pivot the camera starts, in metres.</summary>
    public const float StartMetres = 2.5f;

    /// <summary>The nearest the wheel brings the camera, in metres.</summary>
    public const float LeastMetres = 1.5f;

    /// <summary>The farthest the wheel takes the camera, in metres.</summary>
    public const float MostMetres = 5;

    /// <summary>How far one step of the wheel moves the camera, in metres.</summary>
    public const float WheelStepMetres = 0.25f;

    /// <summary>How far short of a voxel in its way the camera stops, in metres.</summary>
    public const float WallGapMetres = 0.2f;

    /// <summary>Pulled in nearer the eye than this, in metres, the player's body is not drawn: the camera would be in it.</summary>
    public const float BodyHiddenMetres = 0.6f;

    /// <summary>How far the aim looks for what the screen's middle points at, in metres.</summary>
    public const float AimMetres = 200;

    /// <summary>Where the camera is for an eye at <paramref name="eye"/> looking at <paramref name="yaw"/> and <paramref name="pitch"/>, <paramref name="metres"/> back; without voxels, nothing pulls it in.</summary>
    public static Vector3 Position(
        VoxelWorld? voxels,
        Vector3 eye,
        float yaw,
        float pitch,
        float metres,
        ObjectRays? objects = null
    )
    {
        var forward = Forward(yaw, pitch);
        // Forward at yaw y is (-sin y, 0, -cos y) and right is (cos y, 0, -sin y).
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var pivot = eye + right * RightMetres + new Vector3(0, UpMetres, 0);
        if (voxels is null)
        {
            return pivot - forward * metres;
        }

        pivot = Clear(voxels, objects, eye, pivot);
        return Clear(voxels, objects, pivot, pivot - forward * metres);
    }

    /// <summary>
    /// The direction from <paramref name="eye"/> towards what the third-person camera's middle points at: the first
    /// voxel (the world's or an object's) on its ray past the point level with the eye, or <see cref="AimMetres"/> along
    /// it. The world's voxels are read at coarser levels where level 0 is not resident.
    /// </summary>
    public static Vector3 Aim(
        VoxelWorld voxels,
        Vector3 eye,
        float yaw,
        float pitch,
        float metres,
        ObjectRays? objects = null
    )
    {
        var camera = Position(voxels, eye, yaw, pitch, metres, objects);
        var forward = Forward(yaw, pitch);
        var start = camera + forward * MathF.Max(0, Vector3.Dot(eye - camera, forward));
        var far = AimMetres;
        if (voxels.Raycast(start, forward, far, out var hit, coarse: true))
        {
            far = hit.DistanceMetres;
        }

        if (objects is not null && objects.Raycast(start, forward, far, out var met))
        {
            far = met.DistanceMetres;
        }

        var towards = start + forward * far - eye;
        return towards.LengthSquared() > 1e-8f ? Vector3.Normalize(towards) : forward;
    }

    /// <summary>
    /// The forward the local player's tools and attacks go along from its eye at <paramref name="eye"/>: the look's
    /// (<see cref="Forward"/>) in first person or <paramref name="flying"/> (the fly camera's look), else
    /// <see cref="Aim"/> for its third-person camera (<see cref="ThirdPersonView"/>).
    /// </summary>
    public static Vector3 Look(
        World world,
        VoxelWorld voxels,
        Vector3 eye,
        float yaw,
        float pitch,
        bool flying,
        ObjectRays? objects = null
    )
    {
        if (flying)
        {
            return Forward(yaw, pitch);
        }

        var find = new FindView();
        world.InlineQuery<FindView, ThirdPersonView>(in s_view, ref find);
        return find.On ? Aim(voxels, eye, yaw, pitch, find.Metres, objects) : Forward(yaw, pitch);
    }

    /// <summary>The look's forward at <paramref name="yaw"/> and <paramref name="pitch"/>.</summary>
    public static Vector3 Forward(float yaw, float pitch) =>
        Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromYawPitchRoll(yaw, pitch, 0));

    /// <summary>The wheel's <paramref name="steps"/> (away from the user positive: nearer) applied to <paramref name="metres"/>.</summary>
    public static float Wheel(float metres, float steps) =>
        Math.Clamp(metres - steps * WheelStepMetres, LeastMetres, MostMetres);

    /// <summary>The local player's third-person camera: whether it is on, and how far back.</summary>
    private struct FindView : IForEach<ThirdPersonView>
    {
        public bool On;
        public float Metres;

        public void Update(ref ThirdPersonView view)
        {
            On = view.On;
            Metres = view.Metres > 0 ? view.Metres : StartMetres;
        }
    }

    /// <summary>
    /// <paramref name="to"/>, or <see cref="WallGapMetres"/> short of the first voxel, the world's or an object's, from
    /// <paramref name="from"/> towards it.
    /// </summary>
    private static Vector3 Clear(VoxelWorld voxels, ObjectRays? objects, Vector3 from, Vector3 to)
    {
        var along = to - from;
        var length = along.Length();
        if (length < 1e-4f)
        {
            return to;
        }

        var direction = along / length;
        var reach = length + WallGapMetres;
        var met = false;
        if (voxels.Raycast(from, direction, reach, out var hit))
        {
            reach = hit.DistanceMetres;
            met = true;
        }

        if (objects is not null && objects.Raycast(from, direction, reach, out var objectHit))
        {
            reach = objectHit.DistanceMetres;
            met = true;
        }

        return met ? from + direction * MathF.Max(0, reach - WallGapMetres) : to;
    }
}
