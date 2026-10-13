using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Systems;

/// <summary>
/// The third-person camera's place and aim. The camera orbits a pivot over a shoulder (the eye plus
/// <see cref="RightMetres"/> to the look's level right, or as far left with <see cref="ThirdPersonView.Left"/> 1, and
/// <see cref="UpMetres"/> up), <see cref="ThirdPersonView.Metres"/> back along the look, pitch included; a surface in the
/// way (from the eye to the pivot, then from the pivot to the camera), a voxel of the world or a voxel object
/// (<see cref="ObjectRays"/>) or a mesh collider's triangle (<see cref="MeshColliders"/>), where given, pulls it in to
/// <see cref="WallGapMetres"/> short of it, at once both ways, so the fixed step's aim and the frame's camera agree. The aim
/// goes from the eye towards what the camera's middle ray meets first past the point level with the eye (nothing between
/// the camera and the player counts), in the voxels, the objects, and the colliders. The rays hit leaves, so a bush behind the player pulls the camera in front of
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
        ObjectRays? objects = null,
        MeshColliders? meshes = null,
        float left = 0
    )
    {
        var forward = Forward(yaw, pitch);
        // Forward at yaw y is (-sin y, 0, -cos y) and right is (cos y, 0, -sin y).
        var right = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var side = 1 - 2 * Math.Clamp(float.IsFinite(left) ? left : 0, 0, 1);
        var pivot = eye + right * (RightMetres * side) + new Vector3(0, UpMetres, 0);
        pivot = Clear(voxels, objects, meshes, eye, pivot);
        return Clear(voxels, objects, meshes, pivot, pivot - forward * metres);
    }

    /// <summary>
    /// The direction from <paramref name="eye"/> towards what the third-person camera's middle points at
    /// (<see cref="AimPoint"/>): the first voxel (the world's or an object's) or mesh collider on its ray past the point
    /// level with the eye, or, given <paramref name="bodies"/>, the first character's hit shape there at
    /// <paramref name="tick"/> but those of <paramref name="ignore"/> (the aimer's own), or <see cref="AimMetres"/> along
    /// it. The world's voxels are read at coarser levels where level 0 is not resident. Without the bodies a crosshair on a
    /// character 20 m off sends a shot from the eye about 0.4 m beside it, the camera being over the shoulder.
    /// </summary>
    public static Vector3 Aim(
        VoxelWorld? voxels,
        Vector3 eye,
        float yaw,
        float pitch,
        float metres,
        ObjectRays? objects = null,
        MeshColliders? meshes = null,
        float left = 0,
        HitHistory? bodies = null,
        double tick = 0,
        Entity? ignore = null
    )
    {
        var towards = AimPoint(voxels, eye, yaw, pitch, metres, objects, meshes, left, bodies, tick, ignore) - eye;
        return towards.LengthSquared() > 1e-8f ? Vector3.Normalize(towards) : Forward(yaw, pitch);
    }

    /// <summary>
    /// The point the third-person camera's middle points at, as <see cref="Aim"/> finds it: for a shot that leaves from
    /// somewhere other than the eye (a weapon's muzzle) towards what the crosshair covers.
    /// </summary>
    public static Vector3 AimPoint(
        VoxelWorld? voxels,
        Vector3 eye,
        float yaw,
        float pitch,
        float metres,
        ObjectRays? objects = null,
        MeshColliders? meshes = null,
        float left = 0,
        HitHistory? bodies = null,
        double tick = 0,
        Entity? ignore = null
    )
    {
        var camera = Position(voxels, eye, yaw, pitch, metres, objects, meshes, left);
        var forward = Forward(yaw, pitch);
        var start = camera + forward * MathF.Max(0, Vector3.Dot(eye - camera, forward));
        return start + forward * Reach(voxels, start, forward, objects, meshes, bodies, tick, ignore);
    }

    /// <summary>
    /// The point straight along the look from <paramref name="eye"/> that it first meets (<see cref="AimPoint"/>'s rules,
    /// with no camera over the shoulder): for an aimer that aims from its eye.
    /// </summary>
    public static Vector3 LookPoint(
        VoxelWorld? voxels,
        Vector3 eye,
        float yaw,
        float pitch,
        ObjectRays? objects = null,
        MeshColliders? meshes = null,
        HitHistory? bodies = null,
        double tick = 0,
        Entity? ignore = null
    )
    {
        var forward = Forward(yaw, pitch);
        return eye + forward * Reach(voxels, eye, forward, objects, meshes, bodies, tick, ignore);
    }

    /// <summary>How far along <paramref name="forward"/> from <paramref name="start"/> the first thing met lies, at most <see cref="AimMetres"/>.</summary>
    private static float Reach(
        VoxelWorld? voxels,
        Vector3 start,
        Vector3 forward,
        ObjectRays? objects,
        MeshColliders? meshes,
        HitHistory? bodies,
        double tick,
        Entity? ignore
    )
    {
        var far = AimMetres;
        if (voxels is not null && voxels.Raycast(start, forward, far, out var hit, coarse: true))
        {
            far = hit.DistanceMetres;
        }

        if (objects is not null && objects.Raycast(start, forward, far, out var met))
        {
            far = met.DistanceMetres;
        }

        if (meshes is not null && meshes.Raycast(start, forward, far, out var surface))
        {
            far = surface.Distance;
        }

        if (bodies is not null && bodies.Raycast(start, forward, far, tick, ignore ?? Entity.Null, out var body))
        {
            far = body.Distance;
        }

        return far;
    }

    /// <summary>
    /// The forward the local player's tools and attacks go along from its eye at <paramref name="eye"/>: the look's
    /// (<see cref="Forward"/>) in first person or <paramref name="flying"/> (the fly camera's look), else
    /// <see cref="Aim"/> for its third-person camera (<see cref="ThirdPersonView"/>).
    /// </summary>
    public static Vector3 Look(
        World world,
        VoxelWorld? voxels,
        Vector3 eye,
        float yaw,
        float pitch,
        bool flying,
        ObjectRays? objects = null,
        MeshColliders? meshes = null
    )
    {
        if (flying)
        {
            return Forward(yaw, pitch);
        }

        var find = new FindView();
        world.InlineQuery<FindView, ThirdPersonView>(in s_view, ref find);
        return find.On ? Aim(voxels, eye, yaw, pitch, find.Metres, objects, meshes, find.Left) : Forward(yaw, pitch);
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
        public float Left;

        public void Update(ref ThirdPersonView view)
        {
            On = view.On;
            Metres = view.Metres > 0 ? view.Metres : StartMetres;
            Left = view.Left;
        }
    }

    /// <summary>
    /// <paramref name="to"/>, or <see cref="WallGapMetres"/> short of the first voxel, the world's or an object's, or the
    /// first mesh collider, from <paramref name="from"/> towards it.
    /// </summary>
    private static Vector3 Clear(
        VoxelWorld? voxels,
        ObjectRays? objects,
        MeshColliders? meshes,
        Vector3 from,
        Vector3 to
    )
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
        if (voxels is not null && voxels.Raycast(from, direction, reach, out var hit))
        {
            reach = hit.DistanceMetres;
            met = true;
        }

        if (objects is not null && objects.Raycast(from, direction, reach, out var objectHit))
        {
            reach = objectHit.DistanceMetres;
            met = true;
        }

        if (meshes is not null && meshes.Raycast(from, direction, reach, out var surface))
        {
            reach = surface.Distance;
            met = true;
        }

        return met ? from + direction * MathF.Max(0, reach - WallGapMetres) : to;
    }
}
