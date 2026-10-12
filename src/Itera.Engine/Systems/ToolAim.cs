using System.Numerics;
using Arch.Core;
using Itera.Engine.Components;
using Itera.Engine.Maths;
using Itera.Engine.Objects;
using Itera.Engine.Physics;
using Itera.Engine.Voxels;

namespace Itera.Engine.Systems;

/// <summary>The local player as its tools aim from it (<see cref="ToolAim.FindPlayer"/>).</summary>
public struct ToolAimPlayer
{
    /// <summary>True when there is a local player.</summary>
    public bool Found;

    /// <summary>True while it flies the debug camera.</summary>
    public bool Flying;

    /// <summary>Its command's movement buttons.</summary>
    public PlayerButtons Buttons;

    /// <summary>Its command's game buttons (<see cref="PlayerCommand.Actions"/>).</summary>
    public uint Actions;

    /// <summary>Its command's look.</summary>
    public float Yaw;

    public float Pitch;

    /// <summary>Its eye: its feet plus its eye height, without the eye's ease.</summary>
    public Vector3 Eye;
}

/// <summary>
/// Where the local player's tools aim: from the eye along the command's look (towards the screen's middle in third
/// person, <see cref="ThirdPersonCamera.Look"/>), or from the <see cref="ActiveCamera"/> along its look while the player
/// flies the debug camera, a debug case; and what that look meets first within a reach: the world's voxel, or any
/// surface of the match (the terrain, the voxel objects, the mesh colliders; <see cref="CollisionSources"/>). Reads
/// <see cref="PlayerCommand"/>, <see cref="PlayerMode"/>, <see cref="CharacterPosition"/>, <see cref="CharacterSize"/>,
/// and <see cref="CameraPose"/>. Main thread; allocates nothing.
/// </summary>
public static class ToolAim
{
    /// <summary>The most passable voxels a tool's ray goes past from the eye (<c>Raycast</c>): a bush's depth.</summary>
    public const int MostSkippedVoxels = 128;

    /// <summary>How near the ray's start a voxel must begin to count as touching it, in metres.</summary>
    private const float TouchingMetres = 0.001f;

    private static readonly QueryDescription s_player = new QueryDescription().WithAll<
        PlayerCommand,
        PlayerMode,
        CharacterPosition,
        CharacterSize,
        LocalPlayer
    >();

    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    /// <summary>The local player this reads, for a system's <c>EcsChecks.RequireAll</c>.</summary>
    public static ref readonly QueryDescription Players => ref s_player;

    /// <summary>The fly camera this reads.</summary>
    public static ref readonly QueryDescription Cameras => ref s_camera;

    /// <summary>The local player's eye, look, and buttons (<see cref="ToolAimPlayer.Found"/> false without one).</summary>
    public static ToolAimPlayer FindPlayer(World world)
    {
        var job = new FindTool();
        world.InlineQuery<FindTool, PlayerCommand, PlayerMode, CharacterPosition, CharacterSize>(in s_player, ref job);
        return job.Player;
    }

    /// <summary>
    /// Where a player at <paramref name="eye"/> looking at <paramref name="yaw"/> and <paramref name="pitch"/> aims from
    /// and along: from the eye along its look, towards the screen's middle in third person
    /// (<see cref="ThirdPersonCamera.Look"/>, through the voxels and the <paramref name="objects"/>; straight along the look
    /// with no voxel world), or from the <see cref="ActiveCamera"/> along its look while it flies the debug camera (from
    /// the eye with no camera). Every tool of the local player aims through it.
    /// </summary>
    public static (Vector3 From, Vector3 Forward) Look(
        World world,
        VoxelWorld? voxels,
        Vector3 eye,
        float yaw,
        float pitch,
        bool flying,
        ObjectRays? objects = null
    )
    {
        if (flying)
        {
            var camera = new FindCamera();
            world.InlineQuery<FindCamera, CameraPose>(in s_camera, ref camera);
            return camera.Found
                ? (camera.Pose.Position, ThirdPersonCamera.Forward(camera.Pose.Yaw, camera.Pose.Pitch))
                : (eye, ThirdPersonCamera.Forward(yaw, pitch));
        }

        return (
            eye,
            voxels is null
                ? ThirdPersonCamera.Forward(yaw, pitch)
                : ThirdPersonCamera.Look(world, voxels, eye, yaw, pitch, false, objects)
        );
    }

    /// <summary>Where <paramref name="player"/> (<see cref="FindPlayer"/>) aims from and along (<see cref="Look(World, VoxelWorld?, Vector3, float, float, bool, ObjectRays?)"/>).</summary>
    public static (Vector3 From, Vector3 Forward) Look(
        World world,
        VoxelWorld voxels,
        in ToolAimPlayer player,
        ObjectRays? objects = null
    ) => Look(world, voxels, player.Eye, player.Yaw, player.Pitch, player.Flying, objects);

    /// <summary>
    /// The surface a tool's ray from <paramref name="eye"/> along <paramref name="forward"/> meets first within
    /// <paramref name="reachMetres"/> of the match's (<paramref name="sources"/>: the terrain's voxels, the voxel objects'
    /// voxels, and the mesh colliders), past the passable voxels of the terrain and the objects (leaves, flowers:
    /// <paramref name="passability"/>) the eye is in and those touching them along the ray, up to
    /// <see cref="MostSkippedVoxels"/>, as <see cref="Raycast(VoxelWorld, IVoxelPassability, Vector3, Vector3, float, out VoxelHit)"/>
    /// does for the terrain alone: from inside a bush the tools act past it. Its distance is from the eye. Allocates
    /// nothing once the colliders' buckets are made.
    /// </summary>
    public static bool Raycast(
        CollisionSources sources,
        IVoxelPassability passability,
        Vector3 eye,
        Vector3 forward,
        float reachMetres,
        out SurfaceHit hit
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(passability);
        var direction = Vector3.Normalize(forward);
        var from = eye;
        var travelled = 0f;
        for (var skipped = 0; ; skipped++)
        {
            // Each source's first surface within the whole reach left, so a skip past leaves stops short of anything in
            // them.
            var left = reachMetres - travelled;
            var meshMet = sources.Meshes.Raycast(from, direction, left, out var mesh);
            var voxelMet = false;
            var objectMet = false;
            SurfaceHit voxelHit = default,
                objectHit = default;
            float voxelExit = 0,
                objectExit = 0;
            if (sources.Voxels is { } voxels && voxels.Raycast(from, direction, left, out var voxel))
            {
                voxelHit = CollisionSources.FromVoxel(from, direction, in voxel, sources.VoxelTags);
                voxelExit = Exit(voxels.Origin, from, direction, in voxel);
                voxelMet = true;
            }

            if (sources.Objects is { } objects && objects.Raycast(from, direction, left, out var met))
            {
                objectHit = CollisionSources.FromObject(from, direction, in met, sources.VoxelTags);
                objectExit = met.ExitMetres;
                objectMet = true;
            }

            if (!meshMet && !voxelMet && !objectMet)
            {
                hit = default;
                return false;
            }

            // The nearest: the mesh's, then the voxels', then the objects' on a tie.
            hit =
                meshMet ? mesh
                : voxelMet ? voxelHit
                : objectHit;
            hit = voxelMet && voxelHit.Distance < hit.Distance ? voxelHit : hit;
            hit = objectMet && objectHit.Distance < hit.Distance ? objectHit : hit;

            // What touches the start: passable voxels let the ray through to the nearest of their far sides; anything
            // else is what the tool meets.
            var through = float.PositiveInfinity;
            var beyond = float.PositiveInfinity;
            var blocked = false;
            var solid = default(SurfaceHit);
            Weigh(meshMet, in mesh, false, 0, ref through, ref beyond, ref blocked, ref solid);
            Weigh(
                voxelMet,
                in voxelHit,
                passability.IsPassable(voxelHit.Material),
                voxelExit,
                ref through,
                ref beyond,
                ref blocked,
                ref solid
            );
            Weigh(
                objectMet,
                in objectHit,
                passability.IsPassable(objectHit.Material),
                objectExit,
                ref through,
                ref beyond,
                ref blocked,
                ref solid
            );
            if (blocked || float.IsPositiveInfinity(through) || skipped == MostSkippedVoxels)
            {
                hit = (blocked ? solid : hit) with { Distance = (blocked ? solid : hit).Distance + travelled };
                return true;
            }

            // Past the passable voxels, short of the nearest surface beyond the start (at least half a touch on).
            var past = Math.Min(through + TouchingMetres / 2, beyond - TouchingMetres / 2);
            from += direction * past;
            travelled += past;
            if (travelled >= reachMetres)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Weighs one source's first surface for the tools' ray: one touching the start lets the ray through to its far side
    /// (<paramref name="exit"/>) when <paramref name="passable"/>, else stops it (the nearest such is
    /// <paramref name="solid"/>); one past the touch is a limit to how far a skip goes (<paramref name="beyond"/>).
    /// </summary>
    private static void Weigh(
        bool met,
        in SurfaceHit candidate,
        bool passable,
        float exit,
        ref float through,
        ref float beyond,
        ref bool blocked,
        ref SurfaceHit solid
    )
    {
        if (!met)
        {
            return;
        }

        if (candidate.Distance > TouchingMetres)
        {
            beyond = Math.Min(beyond, candidate.Distance);
        }
        else if (passable)
        {
            through = Math.Min(through, Math.Max(exit, 0));
        }
        else if (!blocked || candidate.Distance < solid.Distance)
        {
            solid = candidate;
            blocked = true;
        }
    }

    /// <summary>
    /// The world's voxel a tool's ray from <paramref name="eye"/> along <paramref name="forward"/> meets first within
    /// <paramref name="reachMetres"/>, past the passable voxels (leaves, flowers: <paramref name="passability"/>) the eye
    /// is in and those touching them along the ray, up to <see cref="MostSkippedVoxels"/>: from inside a bush the tools
    /// act past it, not round the head. Its distance is from the eye. Allocates nothing.
    /// </summary>
    public static bool Raycast(
        VoxelWorld voxels,
        IVoxelPassability passability,
        Vector3 eye,
        Vector3 forward,
        float reachMetres,
        out VoxelHit hit
    )
    {
        ArgumentNullException.ThrowIfNull(voxels);
        ArgumentNullException.ThrowIfNull(passability);
        var direction = Vector3.Normalize(forward);
        var from = eye;
        var travelled = 0f;
        for (var skipped = 0; ; skipped++)
        {
            if (!voxels.Raycast(from, direction, reachMetres - travelled, out hit))
            {
                return false;
            }

            hit = hit with { DistanceMetres = hit.DistanceMetres + travelled };
            // Where the ray starts again, and let through: past it.
            if (
                hit.DistanceMetres - travelled > TouchingMetres
                || !passability.IsPassable(hit.Material)
                || skipped == MostSkippedVoxels
            )
            {
                return true;
            }

            var past = Exit(voxels.Origin, from, direction, in hit) + TouchingMetres / 2;
            from += direction * past;
            travelled += past;
            if (travelled >= reachMetres)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// How far along <paramref name="direction"/> (unit) from <paramref name="from"/> (local to
    /// <paramref name="origin"/>) the ray leaves the voxel <paramref name="hit"/> met (its box: corner and edge in
    /// absolute centimetres), in metres.
    /// </summary>
    private static float Exit(WorldOrigin origin, Vector3 from, Vector3 direction, in VoxelHit hit)
    {
        const double perMetre = VoxelConstants.CentimetresPerMetre;
        var size = hit.BoxCentimetres;
        var exit = double.PositiveInfinity;
        exit = Math.Min(exit, Axis(origin.WorldX(from.X) * perMetre, direction.X, hit.BoxMin.X, size));
        exit = Math.Min(exit, Axis(origin.WorldY(from.Y) * perMetre, direction.Y, hit.BoxMin.Y, size));
        exit = Math.Min(exit, Axis(origin.WorldZ(from.Z) * perMetre, direction.Z, hit.BoxMin.Z, size));
        return (float)(Math.Max(0, exit) / perMetre);

        static double Axis(double at, float along, int min, int size) =>
            along > 0 ? (min + size - at) / along
            : along < 0 ? (min - at) / along
            : double.PositiveInfinity;
    }

    private struct FindTool : IForEach<PlayerCommand, PlayerMode, CharacterPosition, CharacterSize>
    {
        public ToolAimPlayer Player;

        public void Update(
            ref PlayerCommand command,
            ref PlayerMode mode,
            ref CharacterPosition position,
            ref CharacterSize size
        )
        {
            Player = new ToolAimPlayer
            {
                Found = true,
                Flying = mode.Flying,
                Buttons = command.Buttons,
                Actions = command.Actions,
                Yaw = command.Yaw,
                Pitch = command.Pitch,
                Eye = position.Feet + new Vector3(0, size.EyeMetres, 0),
            };
        }
    }

    private struct FindCamera : IForEach<CameraPose>
    {
        public CameraPose Pose;
        public bool Found;

        public void Update(ref CameraPose pose)
        {
            Pose = pose;
            Found = true;
        }
    }
}
