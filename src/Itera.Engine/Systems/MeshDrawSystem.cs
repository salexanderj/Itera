using System.Globalization;
using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Hands every placed mesh asset to the renderer (<see cref="MeshInstances"/>) (frame group, after
/// <c>CameraExtractSystem</c> and <see cref="LooseBodyModelSystem"/>, before <c>AtmosphereSystem</c>): each entity with a
/// <see cref="MeshPlacement"/> and a <see cref="DrawnMesh"/> is drawn where it is between the last two fixed steps, its
/// pose blended by <see cref="FrameTime.Alpha"/> and scaled; one that also has a <see cref="MeshAnimator"/> is posed by
/// its clip at the blend of its last two times (its rig's skinning matrices, <c>Rig.WritePalette</c>). Reads those three;
/// writes no components. Registers the console's <c>meshes</c> (the run's assets and their clips; placing one or many in
/// front of the camera, on the ground where a voxel world is, playing a clip or held at a time of it, tagged
/// <see cref="ConsoleMesh"/>; removing those).
/// </summary>
public sealed class MeshDrawSystem : BaseSystem<World, FrameTime>, IFrameUpdateSystem
{
    /// <summary>How far in front of the camera <c>meshes place</c> puts a mesh, in metres.</summary>
    public const float PlaceMetres = 4;

    /// <summary>The most meshes <c>meshes test</c> places.</summary>
    public const int MostTest = 4096;

    private static readonly QueryDescription s_static = new QueryDescription()
        .WithAll<MeshPlacement, DrawnMesh>()
        .WithNone<MeshAnimator>();
    private static readonly QueryDescription s_posed = new QueryDescription().WithAll<
        MeshPlacement,
        DrawnMesh,
        MeshAnimator
    >();
    private static readonly QueryDescription s_placed = new QueryDescription().WithAll<ConsoleMesh>();
    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private readonly MeshInstances _instances;
    private readonly MeshAssets _assets;
    private readonly VoxelWorld? _voxels;
    private readonly ConsoleCommands? _commands;
    private JointTransform[] _locals = [];

    /// <param name="world">The ECS world.</param>
    /// <param name="instances">The frame's mesh instances.</param>
    /// <param name="assets">The run's mesh assets, which <c>meshes</c> lists and places.</param>
    /// <param name="voxels">The match's voxel world, whose ground <c>meshes</c> places on; none for a world without voxels.</param>
    /// <param name="commands">The console's commands, for <c>meshes</c>; none in tests.</param>
    public MeshDrawSystem(
        World world,
        MeshInstances instances,
        MeshAssets assets,
        VoxelWorld? voxels = null,
        ConsoleCommands? commands = null
    )
        : base(world)
    {
        _instances = instances;
        _assets = assets;
        _voxels = voxels;
        _commands = commands;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<MeshPlacement, DrawnMesh>(in s_static);
        EcsChecks.RequireAll<MeshPlacement, DrawnMesh, MeshAnimator>(in s_posed);
        EcsChecks.RequireAll<CameraPose, ActiveCamera>(in s_camera);

        // Room for the largest rig's joints, posed one instance at a time. The run's assets are read by now.
        var most = 0;
        for (var i = 0; i < _assets.Count; i++)
        {
            most = Math.Max(most, _assets.TryGet(i, out var asset) ? asset.Rig?.JointCount ?? 0 : 0);
        }

        _locals = new JointTransform[most];
        _commands?.Register(
            "meshes",
            "[place NAME [SCALE] [CLIP [SECONDS]] | test COUNT [NAME [CLIP]] | clear]",
            "Lists the run's mesh assets (models from glTF files) and their clips; place puts one 4 m in front of the camera, facing it, on the ground, playing CLIP, or held at SECONDS of it; test puts COUNT in rows ahead (the first asset unless named), playing CLIP from different times; clear removes those it placed.",
            CommandScope.Local,
            Meshes
        );
    }

    public override void Update(in FrameTime time)
    {
        var still = new AddMeshes { Instances = _instances, Alpha = time.Alpha };
        World.InlineQuery<AddMeshes, MeshPlacement, DrawnMesh>(in s_static, ref still);
        var posed = new AddPosed
        {
            Instances = _instances,
            Assets = _assets,
            Locals = _locals,
            Alpha = time.Alpha,
        };
        World.InlineQuery<AddPosed, MeshPlacement, DrawnMesh, MeshAnimator>(in s_posed, ref posed);
    }

    /// <summary>A placement's transform between its last two steps: scale, then the blended turn, then the blended place.</summary>
    private static Matrix4x4 Blend(in MeshPlacement placement, float alpha)
    {
        var position = Vector3.Lerp(placement.PreviousPosition, placement.Position, alpha);
        var orientation = Quaternion.Slerp(placement.PreviousOrientation, placement.Orientation, alpha);
        return Matrix4x4.CreateScale(placement.Scale)
            * Matrix4x4.CreateFromQuaternion(orientation)
            * Matrix4x4.CreateTranslation(position);
    }

    private void Meshes(CommandContext context)
    {
        if (context.Count == 0)
        {
            for (var i = 0; i < _assets.Count; i++)
            {
                context.Print(Describe(i, _assets[i]));
            }

            context.Print($"{_assets.Count} mesh asset(s)");
            return;
        }

        if (context.Is(0, "clear"))
        {
            var count = World.CountEntities(in s_placed);
            World.Destroy(in s_placed);
            context.Print($"{count} mesh(es) removed");
            return;
        }

        var place = context.Is(0, "place");
        var test = context.Is(0, "test");
        if (!place && !test)
        {
            context.PrintUsage();
            return;
        }

        if (!FindCamera(out var camera))
        {
            context.PrintError("meshes: no camera to place it in front of");
            return;
        }

        var name = place ? (context.Count > 1 ? context[1] : null) : (context.Count > 2 ? context[2] : null);
        var index = name is null ? (_assets.Count > 0 ? 0 : -1) : _assets.Find(name);
        if (index < 0)
        {
            context.PrintError(
                name is null ? "meshes: the run has no mesh assets" : $"meshes: no mesh asset named {name}"
            );
            return;
        }

        if (place)
        {
            Place(context, index, camera);
        }
        else
        {
            Test(context, index, camera);
        }
    }

    /// <summary><c>meshes place NAME [SCALE] [CLIP [SECONDS]]</c>.</summary>
    private void Place(CommandContext context, int index, CameraPose camera)
    {
        var argument = 2;
        var scale = 1f;
        if (context.TryGetFloat(argument, out var given))
        {
            if (!(given > 0))
            {
                context.PrintUsage();
                return;
            }

            scale = given;
            argument++;
        }

        MeshAnimator? animator = null;
        if (context.Count > argument)
        {
            if (!TryClip(context, index, context[argument], out var clip))
            {
                return;
            }

            argument++;
            if (context.Count > argument)
            {
                if (!context.TryGetFloat(argument, out var seconds) || seconds < 0)
                {
                    context.PrintUsage();
                    return;
                }

                animator = MeshAnimator.Playing(clip, seconds, speed: 0);
                argument++;
            }
            else
            {
                animator = MeshAnimator.Playing(clip);
            }
        }

        if (context.Count > argument)
        {
            context.PrintUsage();
            return;
        }

        var forward = ThirdPersonCamera.Forward(camera.Yaw, 0);
        var at = OnGround(camera.Position + forward * PlaceMetres);
        Create(MeshPlacement.At(at, Facing(camera), scale), index, animator);
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{_assets[index].Name} placed at ({at.X:F2}, {at.Y:F2}, {at.Z:F2}) m"
            )
        );
    }

    /// <summary><c>meshes test COUNT [NAME [CLIP]]</c>: rows ahead of the camera, each playing the clip from its own time.</summary>
    private void Test(CommandContext context, int index, CameraPose camera)
    {
        if (!context.TryGetInt(1, out var many) || many < 1 || context.Count > 4)
        {
            context.PrintUsage();
            return;
        }

        var clip = -1;
        if (context.Count > 3 && !TryClip(context, index, context[3], out clip))
        {
            return;
        }

        many = Math.Min(many, MostTest);
        var asset = _assets[index];
        var size = asset.BoundsMax - asset.BoundsMin;
        var spacing = Math.Max(1.5f, 1.25f * Math.Max(size.X, size.Z));
        var columns = (int)MathF.Ceiling(MathF.Sqrt(many));
        var forward = ThirdPersonCamera.Forward(camera.Yaw, 0);
        var right = new Vector3(-forward.Z, 0, forward.X);
        var facing = Facing(camera);
        var duration = MeshAnimatorSystem.DurationOf(_assets, index, clip);
        for (var i = 0; i < many; i++)
        {
            var row = i / columns;
            var column = i % columns - (columns - 1) / 2f;
            var at = OnGround(camera.Position + forward * (PlaceMetres + row * spacing) + right * (column * spacing));
            // Each from a different time, so they do not move together.
            MeshAnimator? animator =
                clip < 0 ? null : MeshAnimator.Playing(clip, duration > 0 ? i * 0.37f % duration : 0);
            Create(MeshPlacement.At(at, facing), index, animator);
        }

        context.Print($"{many} {asset.Name} placed");
    }

    private void Create(MeshPlacement placement, int asset, MeshAnimator? animator)
    {
        if (animator is { } playing)
        {
            World.Create(placement, DrawnMesh.Of(asset), playing, new ConsoleMesh());
        }
        else
        {
            World.Create(placement, DrawnMesh.Of(asset), new ConsoleMesh());
        }
    }

    /// <summary>The clip of asset <paramref name="index"/>'s rig named <paramref name="name"/>; an error naming its clips when there is none.</summary>
    private bool TryClip(CommandContext context, int index, string name, out int clip)
    {
        var rig = _assets[index].Rig;
        clip = rig?.FindClip(name) ?? -1;
        if (clip >= 0)
        {
            return true;
        }

        context.PrintError(
            rig is null
                ? $"meshes: {_assets[index].Name} has no rig, so no clips"
                : $"meshes: {_assets[index].Name} has no clip named {name}; its clips: {string.Join(", ", rig.Clips.Select(c => c.Name))}"
        );
        return false;
    }

    /// <summary>The list's line for an asset: its triangles, materials, textures, size, and a rig's joints and clips.</summary>
    private static string Describe(int index, MeshAsset asset)
    {
        var extent = asset.BoundsMax - asset.BoundsMin;
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{index}: {asset.Name}, {asset.TriangleCount} triangles, {asset.Materials.Length} material(s), {asset.Textures.Length} texture(s), {extent.X:F2} x {extent.Y:F2} x {extent.Z:F2} m"
        );
        if (asset.Rig is not { } rig)
        {
            return line;
        }

        var clips = string.Join(
            ", ",
            rig.Clips.Select(clip => string.Create(CultureInfo.InvariantCulture, $"{clip.Name} {clip.Duration:F2} s"))
        );
        return $"{line}, {rig.JointCount} joints, clips: {(clips.Length == 0 ? "none" : clips)}";
    }

    /// <summary>A turn about +y that faces an asset's front (glTF's +z) towards the camera.</summary>
    private static Quaternion Facing(CameraPose camera) => Quaternion.CreateFromYawPitchRoll(camera.Yaw, 0, 0);

    /// <summary>
    /// The top of the voxel world's ground under (or over) <paramref name="point"/>: a ray down from 64 m above it, through
    /// coarser levels where level 0 is not loaded. Without a voxel world, with no ground within 192 m below it, or with the
    /// ray starting inside the ground, 1.6 m below it.
    /// </summary>
    private Vector3 OnGround(Vector3 point)
    {
        const float Above = 64;
        const float Reach = 256;
        if (
            _voxels is not null
            && _voxels.Raycast(point + new Vector3(0, Above, 0), -Vector3.UnitY, Reach, out var hit, coarse: true)
            && hit.DistanceMetres > 0
        )
        {
            return point with { Y = point.Y + Above - hit.DistanceMetres };
        }

        return point - new Vector3(0, 1.6f, 0);
    }

    private bool FindCamera(out CameraPose camera)
    {
        var find = new FindActiveCamera();
        World.InlineQuery<FindActiveCamera, CameraPose>(in s_camera, ref find);
        camera = find.Pose;
        return find.Found;
    }

    private struct FindActiveCamera : IForEach<CameraPose>
    {
        public CameraPose Pose;
        public bool Found;

        public void Update(ref CameraPose pose)
        {
            Pose = pose;
            Found = true;
        }
    }

    private struct AddMeshes : IForEach<MeshPlacement, DrawnMesh>
    {
        public MeshInstances Instances;
        public float Alpha;

        public readonly void Update(ref MeshPlacement placement, ref DrawnMesh mesh)
        {
            var transform = Blend(in placement, Alpha);
            Instances.Add(mesh.Asset, in transform, mesh.Tint, mesh.Shadow);
        }
    }

    /// <summary>
    /// A posed mesh: its clip sampled at the blend of its last two times (wrapped by a looping clip's length), its rig's
    /// skinning matrices written where <see cref="MeshInstances.AddPosed"/> keeps them. A static asset, or a rig larger
    /// than the room made at the start, is drawn as its asset stores it.
    /// </summary>
    private struct AddPosed : IForEach<MeshPlacement, DrawnMesh, MeshAnimator>
    {
        public MeshInstances Instances;
        public MeshAssets Assets;
        public JointTransform[] Locals;
        public float Alpha;

        public readonly void Update(ref MeshPlacement placement, ref DrawnMesh mesh, ref MeshAnimator animator)
        {
            var transform = Blend(in placement, Alpha);
            if (!Assets.TryGet(mesh.Asset, out var asset) || asset.Rig is not { } rig || rig.JointCount > Locals.Length)
            {
                Instances.Add(mesh.Asset, in transform, mesh.Tint, mesh.Shadow);
                return;
            }

            var seconds = animator.PreviousSeconds + (animator.Seconds - animator.PreviousSeconds) * Alpha;
            var duration = MeshAnimatorSystem.DurationOf(Assets, mesh.Asset, animator.Clip);
            if (duration > 0 && !animator.Once)
            {
                seconds -= MathF.Floor(seconds / duration) * duration;
            }

            var locals = Locals.AsSpan(0, rig.JointCount);
            rig.Pose(animator.Clip, seconds, locals);
            var palette = Instances.AddPosed(mesh.Asset, in transform, rig.JointCount, mesh.Tint, mesh.Shadow);
            if (!palette.IsEmpty)
            {
                rig.WritePalette(locals, palette);
            }
        }
    }
}
