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
using Itera.Engine.Rendering.Meshes;
using Itera.Engine.Voxels;
using Itera.Modding;

namespace Itera.Engine.Systems;

/// <summary>
/// Hands every placed mesh asset to the renderer (<see cref="MeshInstances"/>) (frame group, after
/// <c>CameraExtractSystem</c>, the systems that ask for image views or set the atmosphere, and
/// <see cref="LooseBodyModelSystem"/>): each entity with a
/// <see cref="MeshPlacement"/> and a <see cref="DrawnMesh"/> is drawn where it is between the last two fixed steps, its
/// pose blended by <see cref="FrameTime.Alpha"/> and scaled; one that also has a <see cref="MeshAnimator"/> is posed by
/// its clip at the blend of its last two times (its rig's skinning matrices, <c>Rig.WritePalette</c>), and while it fades
/// from another clip by both, each joint blended by the fade (rotations along the shorter way); one with a
/// <see cref="ShapedPose"/> is then shaped by the game's <see cref="IMeshPoseShaper"/>, and one with a
/// <see cref="SmoothedPose"/> has its changes smoothed as it is drawn, after the shaping (<see cref="PoseSmoothing"/>: each
/// starts from the pose drawn the frame before, moving as it was). An entity with a <see cref="MeshAttachment"/> is drawn
/// at its holder's joint as the holder is posed this frame (<see cref="MeshPoses"/>; a smoothed holder as it was
/// drawn). A hidden mesh (<see cref="ModelShadow.Hidden"/>) is not posed or drawn, nor is one attached to a hidden
/// holder. With a <see cref="MeshPoseCulling"/>, an animated mesh is posed only when some list of the frame may draw it
/// (the camera's view, a shadow cascade it casts into, an image view due), tested by a sphere round its every pose
/// (<see cref="MeshBounds.EveryPose"/>), or when what it holds may be drawn; one left out keeps its smoothing
/// (<see cref="PoseSmoothing.Hold"/>), and what it holds is left out too. A shaped one is posed always, unless the game's
/// shaper says it keeps that one near its clips (<see cref="IMeshPoseShaper.KeepsNearClips"/>): a shaper may move its
/// joints anywhere. Without a camera set this frame, or a picture, every one is posed. Reads those; writes only the
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

    /// <summary>
    /// The share an asset's sphere round its sampled poses grows by before it culls: for poses between the samples, blends
    /// of clips, and a game's shaping.
    /// </summary>
    public const float BoundShare = 0.25f;

    /// <summary>How much wider than its grown sphere a mesh is culled, in metres: past a cascade's snap of half a texel.</summary>
    public const float CullMarginMetres = 0.5f;

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
    private readonly MeshPoseCulling? _culling;
    private readonly PoseSmoothing _smoothing = new();
    private JointTransform[] _locals = [];
    private JointTransform[] _fromLocals = [];
    private Matrix4x4[] _globals = [];

    // Each asset's sphere round every pose in its own space (MeshBounds.EveryPose, grown by BoundShare), and how far from
    // its origin an attached one reaches (its bound sphere's centre's distance plus its radius).
    private Vector3[] _boundCentres = [];
    private float[] _boundRadii = [];
    private float[] _originReaches = [];

    // This frame: whether the culling runs, and the holders that a mesh attached to them may be drawn with, posed then
    // whatever their own test says, sorted by their ids (beside them).
    private bool _culls;
    private Entity[] _holders = new Entity[16];
    private int[] _holderIds = new int[16];
    private int _holderCount;
    private int _posed;
    private int _culled;

    /// <param name="world">The ECS world.</param>
    /// <param name="instances">The frame's mesh instances.</param>
    /// <param name="assets">The run's mesh assets, which <c>meshes</c> lists and places.</param>
    /// <param name="voxels">The match's voxel world, whose ground <c>meshes</c> places on; none for a world without voxels.</param>
    /// <param name="commands">The console's commands, for <c>meshes</c>; none in tests.</param>
    /// <param name="shaper">The game's shaping of the poses with a <see cref="ShapedPose"/>; none leaves them as sampled.</param>
    /// <param name="shifts">The match's origin, which <c>meshes place NAME at</c> reads world positions from; none refuses it.</param>
    /// <param name="culling">
    /// What the frame's lists may draw, by which a posed mesh no list would draw is neither posed nor drawn; none poses
    /// every one.
    /// </param>
    public MeshDrawSystem(
        World world,
        MeshInstances instances,
        MeshAssets assets,
        VoxelWorld? voxels = null,
        ConsoleCommands? commands = null,
        IMeshPoseShaper? shaper = null,
        OriginShifts? shifts = null,
        MeshPoseCulling? culling = null
    )
        : base(world)
    {
        _culling = culling;
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
        // Only a system that culls needs the spheres round every pose.
        if (_culling is not null)
        {
            FindBounds();
        }

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

    /// <summary>The posed meshes the last frame posed: those some list may draw, or every one without culling.</summary>
    public int LastPosed { get; private set; }

    /// <summary>The posed meshes the last frame left out: no list would draw them, nor what they hold.</summary>
    public int LastCulled { get; private set; }

    public override void Update(in FrameTime time)
    {
        _culls = _culling is not null && _culling.Begin();
        _posed = 0;
        _culled = 0;
        _holderCount = 0;
        if (_culls)
        {
            FindHolders(time.Alpha);
        }

        var still = new AddMeshes { Instances = _instances, Alpha = time.Alpha };
        World.InlineQuery<AddMeshes, MeshPlacement, DrawnMesh>(in s_static, ref still);
        foreach (ref var chunk in World.Query(in s_posed))
        {
            Pose(ref chunk, in time);
        }

        foreach (ref var chunk in World.Query(in s_shaped))
        {
            Pose(ref chunk, in time);
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

        // A smoothed mesh neither drawn nor held this frame is gone, or no longer smoothed.
        _smoothing.Sweep(time.FrameIndex);
        LastPosed = _posed;
        LastCulled = _culled;
    }

    /// <summary>
    /// Poses each mesh of a chunk of animated ones that some list may draw: its clip sampled at the blend of its last two
    /// times (wrapped by a looping clip's length), shaped where it has a <see cref="ShapedPose"/>, smoothed where it has a
    /// <see cref="SmoothedPose"/>, and its rig's skinning matrices written where <see cref="MeshInstances.AddPosed"/> keeps
    /// them; a static asset, or a rig larger than the room made at the start, is drawn as its asset stores it. One no list
    /// would draw (<see cref="MayDraw"/>) and holding nothing drawn is left out, a smoothed one's slot held; a shaped one
    /// only where the shaper keeps it near its clips (<see cref="KeepsNear"/>), as a shaper may move its joints anywhere.
    /// </summary>
    private void Pose(ref Chunk chunk, in FrameTime time)
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
                _culls
                && (!shaped || KeepsNear(entity))
                && !MayDraw(mesh.Asset, in transform, mesh.Shadow)
                && !Holds(entity)
            )
            {
                _culled++;
                if (smoothed)
                {
                    ref readonly var held = ref smoothings[index];
                    _smoothing.Hold(
                        entity,
                        held.Slot,
                        animators[index].Changes,
                        held.Seconds,
                        time.DeltaSeconds,
                        time.FrameIndex
                    );
                }

                continue;
            }

            _posed++;
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

    /// <summary>
    /// The sphere round each asset's every pose (<see cref="MeshBounds.EveryPose"/>, once for the assets that share a rig
    /// and vertices: a model's variants), grown by <see cref="BoundShare"/>, and how far an attached one reaches from its
    /// origin.
    /// </summary>
    private void FindBounds()
    {
        _boundCentres = new Vector3[_assets.Count];
        _boundRadii = new float[_assets.Count];
        _originReaches = new float[_assets.Count];
        var shared = new Dictionary<(Rig, Array), (Vector3, float)>();
        for (var i = 0; i < _assets.Count; i++)
        {
            if (!_assets.TryGet(i, out var asset))
            {
                continue;
            }

            (Vector3 Centre, float Radius) bound;
            if (asset.Rig is not { } rig)
            {
                bound = MeshBounds.EveryPose(asset);
            }
            else if (!shared.TryGetValue((rig, asset.Vertices), out bound))
            {
                bound = MeshBounds.EveryPose(asset);
                shared[(rig, asset.Vertices)] = bound;
            }

            _boundCentres[i] = bound.Centre;
            _boundRadii[i] = bound.Radius * (1 + BoundShare);
            var (centre, radius) = MeshBounds.Bind(asset);
            _originReaches[i] = centre.Length() + radius;
        }
    }

    /// <summary>
    /// True when a mesh of <paramref name="asset"/> placed by <paramref name="transform"/>, in any pose, may be drawn in a
    /// list this frame as its <paramref name="shadow"/> allows (<see cref="MeshPoseCulling.MayDraw"/>): its sphere round
    /// every pose, scaled, and <see cref="CullMarginMetres"/> wider.
    /// </summary>
    private bool MayDraw(int asset, in Matrix4x4 transform, ModelShadow shadow)
    {
        if ((uint)asset >= (uint)_boundRadii.Length)
        {
            return true;
        }

        var centre = Vector3.Transform(_boundCentres[asset], transform);
        var radius = _boundRadii[asset] * MeshBounds.LargestScale(in transform) + CullMarginMetres;
        return _culling!.MayDraw(centre, radius, shadow);
    }

    /// <summary>
    /// True when <paramref name="mesh"/>, attached to a holder of <paramref name="holderAsset"/> placed by
    /// <paramref name="placed"/>, may be drawn this frame: the holder's sphere round every pose (which holds its joints)
    /// grown by the attachment's offset and the mesh's reach from its origin, scaled, and <see cref="CullMarginMetres"/>
    /// wider.
    /// </summary>
    private bool AttachedMayDraw(in MeshAttachment attachment, in DrawnMesh mesh, int holderAsset, in Matrix4x4 placed)
    {
        if ((uint)holderAsset >= (uint)_boundRadii.Length || (uint)mesh.Asset >= (uint)_originReaches.Length)
        {
            return true;
        }

        var centre = Vector3.Transform(_boundCentres[holderAsset], placed);
        var reach = _boundRadii[holderAsset] + attachment.Offset.Length() + _originReaches[mesh.Asset];
        return _culling!.MayDraw(centre, reach * MeshBounds.LargestScale(in placed) + CullMarginMetres, mesh.Shadow);
    }

    /// <summary>
    /// The holders that may be culled (<see cref="MayCull"/>) and that a mesh attached to them may be drawn with this frame
    /// (<see cref="AttachedMayDraw"/>), sorted by their ids: each is posed whatever its own test says, so what it holds is
    /// drawn at its posed joint.
    /// </summary>
    private void FindHolders(float alpha)
    {
        var count = World.CountEntities(in s_attached);
        if (_holders.Length < count)
        {
            _holders = new Entity[Math.Max(count, _holders.Length * 2)];
            _holderIds = new int[_holders.Length];
        }

        foreach (ref var chunk in World.Query(in s_attached))
        {
            var attachments = chunk.GetSpan<MeshAttachment>();
            var meshes = chunk.GetSpan<DrawnMesh>();
            foreach (var index in chunk)
            {
                ref readonly var attachment = ref attachments[index];
                ref readonly var mesh = ref meshes[index];
                if (
                    _holderCount < _holders.Length
                    && HolderOf(in attachment, in mesh, alpha, out var holder)
                    && MayCull(attachment.Holder)
                    && AttachedMayDraw(in attachment, in mesh, holder.Asset, holder.Placed)
                )
                {
                    _holders[_holderCount] = attachment.Holder;
                    _holderIds[_holderCount++] = attachment.Holder.Id;
                }
            }
        }

        Array.Sort(_holderIds, _holders, 0, _holderCount);
    }

    /// <summary>True when <paramref name="entity"/> holds a mesh that may be drawn this frame (<see cref="FindHolders"/>).</summary>
    private bool Holds(Entity entity)
    {
        if (_holderCount == 0)
        {
            return false;
        }

        // Entries of one id are one holder, listed once for each mesh it holds.
        var found = Array.BinarySearch(_holderIds, 0, _holderCount, entity.Id);
        return found >= 0 && _holders[found] == entity;
    }

    /// <summary>
    /// True when the culling may leave out <paramref name="holder"/>: it is animated, and not shaped or shaped near its
    /// clips (<see cref="KeepsNear"/>).
    /// </summary>
    private bool MayCull(Entity holder) =>
        World.Has<MeshAnimator>(holder) && (!World.Has<ShapedPose>(holder) || KeepsNear(holder));

    /// <summary>
    /// True when the shaping keeps <paramref name="entity"/>'s joints near its clips' poses
    /// (<see cref="IMeshPoseShaper.KeepsNearClips"/>); with no shaper it is left as sampled.
    /// </summary>
    private bool KeepsNear(Entity entity) => _shaper?.KeepsNearClips(World, entity) ?? true;

    /// <summary>
    /// The holder of an attached mesh that is drawn: the mesh and its holder not hidden, and the holder alive and placed.
    /// Gives the holder's asset, its shadow, and its placement's transform at <paramref name="alpha"/>.
    /// </summary>
    private bool HolderOf(in MeshAttachment attachment, in DrawnMesh mesh, float alpha, out HeldBy holder)
    {
        var entity = attachment.Holder;
        holder = default;
        if (
            mesh.Shadow == ModelShadow.Hidden
            || !World.IsAlive(entity)
            || !World.Has<MeshPlacement>(entity)
            || !World.Has<DrawnMesh>(entity)
            || World.Get<DrawnMesh>(entity).Shadow == ModelShadow.Hidden
        )
        {
            return false;
        }

        ref readonly var drawn = ref World.Get<DrawnMesh>(entity);
        holder = new HeldBy(drawn.Asset, drawn.Shadow, MeshPoses.Placed(in World.Get<MeshPlacement>(entity), alpha));
        return true;
    }

    /// <summary>
    /// True when an attached mesh's holder is left out this frame (<see cref="Pose"/>): the culling may leave it out, no
    /// list would draw it, and nothing it holds may be drawn.
    /// </summary>
    private bool HolderCulled(Entity entity, in HeldBy holder) =>
        MayCull(entity) && !MayDraw(holder.Asset, holder.Placed, holder.Shadow) && !Holds(entity);

    /// <summary>
    /// A mesh at its holder's joint, the holder posed (and shaped) as it is drawn this frame; none when its holder is left
    /// out (<see cref="HolderCulled"/>), so the holder is never posed for it then.
    /// </summary>
    private void DrawAttached(in MeshAttachment attachment, in DrawnMesh mesh, float alpha, ulong frame)
    {
        var holder = attachment.Holder;
        if (!HolderOf(in attachment, in mesh, alpha, out var held) || (_culls && HolderCulled(holder, in held)))
        {
            return;
        }

        var asset = held.Asset;
        var placed = held.Placed;
        var at = placed;
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

    /// <summary>An attached mesh's holder as it is drawn this frame: its asset, its shadow, and its placement's transform.</summary>
    private readonly record struct HeldBy(int Asset, ModelShadow Shadow, Matrix4x4 Placed);
}
