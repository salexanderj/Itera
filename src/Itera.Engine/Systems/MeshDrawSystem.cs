using System.Globalization;
using System.Numerics;
using Arch.Core;
using Arch.System;
using Itera.Engine.Assets;
using Itera.Engine.Commands;
using Itera.Engine.Components;
using Itera.Engine.Ecs;
using Itera.Engine.Matches;
using Itera.Engine.Maths;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Hands every placed mesh asset to the renderer (<see cref="MeshInstances"/>) (frame group, after
/// <c>CameraExtractSystem</c> and <see cref="LooseBodyModelSystem"/>, before <c>AtmosphereSystem</c>): each entity with a
/// <see cref="MeshPlacement"/> and a <see cref="DrawnMesh"/> is drawn where it is between the last two fixed steps, its
/// pose blended by <see cref="FrameTime.Alpha"/> and scaled; one that also has a <see cref="MeshAnimator"/> is posed by
/// its clip at the blend of its last two times (its rig's skinning matrices, <c>Rig.WritePalette</c>), and while it fades
/// from another clip by both, each joint blended by the fade (rotations along the shorter way); one with a
/// <see cref="ShapedPose"/> is then shaped by the game's <see cref="IMeshPoseShaper"/>, and one with a
/// <see cref="SmoothedPose"/> has its changes smoothed as it is drawn, after the shaping (<see cref="PoseSmoothing"/>: each
/// starts from the pose drawn the frame before, moving as it was). An entity with a <see cref="MeshAttachment"/> is drawn
/// at its holder's joint as the holder is posed this frame (<see cref="MeshPoses"/>; a smoothed holder as it was
/// drawn). A hidden mesh (<see cref="ModelShadow.Hidden"/>) is not posed or drawn, nor is one attached to a hidden
/// holder. Reads those; writes only the
/// slot a <see cref="SmoothedPose"/> keeps. Registers the console's <c>meshes</c> (the run's assets and their clips; placing one or many in
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
    private static readonly QueryDescription s_posed = new QueryDescription()
        .WithAll<MeshPlacement, DrawnMesh, MeshAnimator>()
        .WithNone<ShapedPose, SmoothedPose>();
    private static readonly QueryDescription s_shaped = new QueryDescription()
        .WithAll<MeshPlacement, DrawnMesh, MeshAnimator>()
        .WithAny<ShapedPose, SmoothedPose>();
    private static readonly QueryDescription s_attached = new QueryDescription().WithAll<MeshAttachment, DrawnMesh>();
    private static readonly QueryDescription s_placed = new QueryDescription().WithAll<ConsoleMesh>();
    private static readonly QueryDescription s_camera = new QueryDescription().WithAll<CameraPose, ActiveCamera>();

    private readonly MeshInstances _instances;
    private readonly MeshAssets _assets;
    private readonly VoxelWorld? _voxels;
    private readonly ConsoleCommands? _commands;
    private readonly IMeshPoseShaper? _shaper;
    private readonly OriginShifts? _shifts;
    private readonly PoseSmoothing _smoothing = new();
    private JointTransform[] _locals = [];
    private JointTransform[] _fromLocals = [];
    private Matrix4x4[] _globals = [];

    /// <param name="world">The ECS world.</param>
    /// <param name="instances">The frame's mesh instances.</param>
    /// <param name="assets">The run's mesh assets, which <c>meshes</c> lists and places.</param>
    /// <param name="voxels">The match's voxel world, whose ground <c>meshes</c> places on; none for a world without voxels.</param>
    /// <param name="commands">The console's commands, for <c>meshes</c>; none in tests.</param>
    /// <param name="shaper">The game's shaping of the poses with a <see cref="ShapedPose"/>; none leaves them as sampled.</param>
    /// <param name="shifts">The match's origin, which <c>meshes place NAME at</c> reads world positions from; none refuses it.</param>
    public MeshDrawSystem(
        World world,
        MeshInstances instances,
        MeshAssets assets,
        VoxelWorld? voxels = null,
        ConsoleCommands? commands = null,
        IMeshPoseShaper? shaper = null,
        OriginShifts? shifts = null
    )
        : base(world)
    {
        _shifts = shifts;
        _shaper = shaper;
        _instances = instances;
        _assets = assets;
        _voxels = voxels;
        _commands = commands;
    }

    public override void Initialize()
    {
        EcsChecks.RequireAll<MeshPlacement, DrawnMesh>(in s_static);
        EcsChecks.RequireAll<MeshPlacement, DrawnMesh, MeshAnimator>(in s_posed);
        EcsChecks.RequireAll<MeshPlacement, DrawnMesh, MeshAnimator>(in s_shaped);
        EcsChecks.RequireAll<MeshAttachment, DrawnMesh>(in s_attached);
        EcsChecks.RequireAll<CameraPose, ActiveCamera>(in s_camera);

        // Room for the largest rig's joints, posed one instance at a time. The run's assets are read by now.
        var most = 0;
        for (var i = 0; i < _assets.Count; i++)
        {
            most = Math.Max(most, _assets.TryGet(i, out var asset) ? asset.Rig?.JointCount ?? 0 : 0);
        }

        _locals = new JointTransform[most];
        _fromLocals = new JointTransform[most];
        _globals = new Matrix4x4[most];
        _commands?.Register(
            "meshes",
            "[place NAME [at X Y Z YAW] [SCALE] [CLIP [SECONDS]] | test COUNT [NAME [CLIP]] | clear]",
            "Lists the run's mesh assets (models from glTF files) and their clips; place puts one 4 m in front of the camera, facing it, on the ground, or at X Y Z in the world's metres turned YAW degrees (at 0 its front faces +z), playing CLIP, or held at SECONDS of it, and prints where in the world; test puts COUNT in rows ahead (the first asset unless named), playing CLIP from different times; clear removes those it placed.",
            CommandScope.Local,
            Meshes
        );
    }

    /// <summary>The smoothed meshes' last poses (for tests).</summary>
    public PoseSmoothing Smoothing => _smoothing;

    public override void Update(in FrameTime time)
    {
        var still = new AddMeshes { Instances = _instances, Alpha = time.Alpha };
        World.InlineQuery<AddMeshes, MeshPlacement, DrawnMesh>(in s_static, ref still);
        var posed = new AddPosed
        {
            Instances = _instances,
            Assets = _assets,
            Locals = _locals,
            FromLocals = _fromLocals,
            Alpha = time.Alpha,
        };
        World.InlineQuery<AddPosed, MeshPlacement, DrawnMesh, MeshAnimator>(in s_posed, ref posed);
        foreach (ref var chunk in World.Query(in s_shaped))
        {
            var placements = chunk.GetSpan<MeshPlacement>();
            var meshes = chunk.GetSpan<DrawnMesh>();
            var animators = chunk.GetSpan<MeshAnimator>();
            var shaped = chunk.Has<ShapedPose>();
            var smoothed = chunk.Has<SmoothedPose>();
            var smoothings = smoothed ? chunk.GetSpan<SmoothedPose>() : default;
            foreach (var index in chunk)
            {
                ref readonly var mesh = ref meshes[index];
                if (mesh.Shadow == ModelShadow.Hidden)
                {
                    continue;
                }

                var entity = chunk.Entity(index);
                var transform = MeshPoses.Placed(in placements[index], time.Alpha);
                if (
                    !MeshPoses.Sample(
                        _assets,
                        mesh.Asset,
                        in animators[index],
                        time.Alpha,
                        _locals,
                        _fromLocals,
                        out var rig
                    )
                )
                {
                    _instances.Add(mesh.Asset, in transform, mesh.Tint, mesh.Shadow);
                    continue;
                }

                var locals = _locals.AsSpan(0, rig.JointCount);
                if (shaped)
                {
                    _shaper?.Shape(World, entity, rig, locals, _globals, time.Alpha, MeshPosePurpose.Drawing);
                }

                // The pose as drawn, shaping and all, so a switch of the game's own layer is smoothed as a clip's change.
                if (smoothed)
                {
                    ref var smoothing = ref smoothings[index];
                    smoothing.Slot = _smoothing.Smooth(
                        entity,
                        smoothing.Slot,
                        animators[index].Changes,
                        smoothing.Seconds,
                        locals,
                        time.DeltaSeconds,
                        time.FrameIndex
                    );
                }

                var palette = _instances.AddPosed(
                    mesh.Asset,
                    in transform,
                    rig.JointCount,
                    mesh.Tint,
                    mesh.Shadow,
                    mesh.SkinFirst
                );
                if (!palette.IsEmpty)
                {
                    rig.WritePalette(locals, palette);
                }
            }
        }

        foreach (ref var chunk in World.Query(in s_attached))
        {
            var attachments = chunk.GetSpan<MeshAttachment>();
            var meshes = chunk.GetSpan<DrawnMesh>();
            foreach (var index in chunk)
            {
                DrawAttached(in attachments[index], in meshes[index], time.Alpha, time.FrameIndex);
            }
        }

        // A smoothed mesh not drawn this frame is gone, or no longer smoothed.
        _smoothing.Sweep(time.FrameIndex);
    }

    /// <summary>A mesh at its holder's joint, the holder posed (and shaped) as it is drawn this frame.</summary>
    private void DrawAttached(in MeshAttachment attachment, in DrawnMesh mesh, float alpha, ulong frame)
    {
        var holder = attachment.Holder;
        if (
            mesh.Shadow == ModelShadow.Hidden
            || !World.IsAlive(holder)
            || !World.Has<MeshPlacement>(holder)
            || !World.Has<DrawnMesh>(holder)
            || World.Get<DrawnMesh>(holder).Shadow == ModelShadow.Hidden
        )
        {
            return;
        }

        var placed = MeshPoses.Placed(in World.Get<MeshPlacement>(holder), alpha);
        var at = placed;
        var asset = World.Get<DrawnMesh>(holder).Asset;
        if (
            _assets.TryGet(asset, out var drawn)
            && drawn.Rig is { } drawnRig
            && drawnRig.JointCount <= _locals.Length
            && World.Has<SmoothedPose>(holder)
            && _smoothing.TryDrawn(
                holder,
                World.Get<SmoothedPose>(holder).Slot,
                frame,
                _locals.AsSpan(0, drawnRig.JointCount)
            )
        )
        {
            // A smoothed holder as it was drawn this frame, shaped and smoothed, so what it holds stays in its hand.
            at = MeshPoses.JointInWorld(
                drawnRig,
                _locals.AsSpan(0, drawnRig.JointCount),
                attachment.Joint,
                in placed,
                _globals
            );
        }
        else if (World.Has<MeshAnimator>(holder))
        {
            if (
                MeshPoses.Sample(
                    _assets,
                    asset,
                    in World.Get<MeshAnimator>(holder),
                    alpha,
                    _locals,
                    _fromLocals,
                    out var rig
                )
            )
            {
                var locals = _locals.AsSpan(0, rig.JointCount);
                if (World.Has<ShapedPose>(holder))
                {
                    _shaper?.Shape(World, holder, rig, locals, _globals, alpha, MeshPosePurpose.Drawing);
                }

                at = MeshPoses.JointInWorld(rig, locals, attachment.Joint, in placed, _globals);
            }
        }
        else if (_assets.TryGet(asset, out var model) && model.Rig is { } rest && rest.JointCount <= _locals.Length)
        {
            var locals = _locals.AsSpan(0, rest.JointCount);
            rest.Pose(-1, 0, locals);
            at = MeshPoses.JointInWorld(rest, locals, attachment.Joint, in placed, _globals);
        }

        // A turn left at its default (all zeros) is none.
        var turn = attachment.Turn == default ? Quaternion.Identity : attachment.Turn;
        var transform = Matrix4x4.CreateFromQuaternion(turn) * Matrix4x4.CreateTranslation(attachment.Offset) * at;
        _instances.Add(mesh.Asset, in transform, mesh.Tint, mesh.Shadow);
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

    /// <summary><c>meshes place NAME [at X Y Z YAW] [SCALE] [CLIP [SECONDS]]</c>.</summary>
    private void Place(CommandContext context, int index, CameraPose camera)
    {
        var argument = 2;
        var forward = ThirdPersonCamera.Forward(camera.Yaw, 0);
        var at = OnGround(camera.Position + forward * PlaceMetres);
        var facing = Facing(camera);
        if (context.Count > argument && context.Is(argument, "at"))
        {
            if (_shifts is null)
            {
                context.PrintError(
                    "meshes: place at needs the match's origin, which this game's composition does not give"
                );
                return;
            }

            if (
                !context.TryGetDouble(argument + 1, out var x)
                || !context.TryGetDouble(argument + 2, out var y)
                || !context.TryGetDouble(argument + 3, out var z)
                || !context.TryGetFloat(argument + 4, out var yawDegrees)
                || !double.IsFinite(x + y + z)
                || !float.IsFinite(yawDegrees)
            )
            {
                context.PrintUsage();
                return;
            }

            at = _shifts.Origin.ToLocal(new WorldPoint(x, y, z));
            facing = Quaternion.CreateFromYawPitchRoll(yawDegrees * MathF.PI / 180, 0, 0);
            argument += 5;
        }

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

        Create(MeshPlacement.At(at, facing, scale), index, animator);
        if (_shifts is null)
        {
            context.Print(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{_assets[index].Name} placed at ({at.X:F2}, {at.Y:F2}, {at.Z:F2}) m"
                )
            );
            return;
        }

        var world = _shifts.Origin.ToWorld(at);
        context.Print(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{_assets[index].Name} placed at {world.X:F2} {world.Y:F2} {world.Z:F2} in the world"
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
            var transform = MeshPoses.Placed(in placement, Alpha);
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
        public JointTransform[] FromLocals;
        public float Alpha;

        public readonly void Update(ref MeshPlacement placement, ref DrawnMesh mesh, ref MeshAnimator animator)
        {
            if (mesh.Shadow == ModelShadow.Hidden)
            {
                return;
            }

            var transform = MeshPoses.Placed(in placement, Alpha);
            if (!MeshPoses.Sample(Assets, mesh.Asset, in animator, Alpha, Locals, FromLocals, out var rig))
            {
                Instances.Add(mesh.Asset, in transform, mesh.Tint, mesh.Shadow);
                return;
            }

            var palette = Instances.AddPosed(
                mesh.Asset,
                in transform,
                rig.JointCount,
                mesh.Tint,
                mesh.Shadow,
                mesh.SkinFirst
            );
            if (!palette.IsEmpty)
            {
                rig.WritePalette(Locals.AsSpan(0, rig.JointCount), palette);
            }
        }
    }
}
