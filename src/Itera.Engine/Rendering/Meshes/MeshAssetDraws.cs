using System.Diagnostics;
using System.Numerics;
using Itera.Engine.Rendering.GpuData;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// Turns the frame's mesh instances (<see cref="MeshInstances"/>) into the mesh pass's asset draws for a frame slot
/// (<see cref="MeshAssetPass"/>): each instance's bounding sphere (its asset's, placed by its transform, the radius times
/// its largest scale) is culled against the frame's view and its two shadow cascades (<see cref="MeshView"/>) as its
/// <see cref="ModelShadow"/> allows, then each list's draws are grouped by primitive with a counting sort, in the layout's
/// order (so by pipeline range: <see cref="MeshAssetRange"/>): one indexed indirect command per primitive drawn, whose
/// instances are its draws (<see cref="MeshAssetDraw"/>, one per instance and primitive), the camera's nearest first by
/// a counting sort of their distances into 16 buckets. Draws and commands are built in
/// arrays of its own and copied into the slot's regions in address order. An instance that would take a list past
/// <see cref="MeshAssetPass.MaxDraws"/> draws is left out of it, and the primitives past
/// <see cref="MeshAssetPass.MaxCommands"/> commands (the last in the layout's order) are not drawn in it, each with one
/// Warning a run. A posed instance (<see cref="MeshInstance.Posed"/>) any list draws is bounded by its joints' posed places
/// and reaches, gets a skin group (<see cref="MeshSkinGroup"/>) and its skinning matrices in the slot's regions, and its
/// draws read its skinned copy (<see cref="MeshAssetDraw.VertexDelta"/>); when the slot's room cannot take every one,
/// those marked <see cref="MeshInstance.SkinFirst"/> are skinned first, then the rest, each nearest the camera first, and
/// those left over are not drawn that frame, with one Warning a run. Blended primitives (<see cref="MeshAssetRange.Blended"/>) are in no list's ranges and
/// cast no shadow: each seen instance's are drawn in the slot's blended list (<see cref="MeshAssetPass.BlendedList"/>),
/// instances sorted back to front by their sphere's centre, each primitive one draw and command (a two-sided one two: its
/// back faces, then its front faces, <see cref="MeshAssetDraw.Sides"/>), at most <see cref="MeshAssetPass.MaxCommands"/>
/// commands, past which they are left out (in the order given, not by distance) with one Warning a run. Main thread, after the frame group, between the view's
/// start and end; allocates nothing once its arrays have grown to the run's assets.
/// </summary>
public sealed partial class MeshAssetDraws
{
    private readonly IMeshAssetTarget _target;
    private readonly ILogger<MeshAssetDraws> _logger;

    // The lists (bit 0 the camera's, 1 the near cascade's, 2 the far cascade's, then one an image view drawn this frame,
    // from ViewBit) each instance is drawn in this frame.
    private readonly byte[] _lists = new byte[MeshInstances.MaxInstances];

    /// <summary>The first image view's list bit; the others follow in their order this frame.</summary>
    private const int ViewBit = 3;

    /// <summary>The bits of the lists that see an instance (rather than take its shadow): the camera's and the views'.</summary>
    private const byte SeenBits = 1 | (((1 << RenderViews.MostViews) - 1) << ViewBit);

    // The image views drawn this frame (RenderViewPass), and each instance's centre's key nearest first in each.
    private readonly IRenderViewLists[] _views = new IRenderViewLists[RenderViews.MostViews];
    private readonly float[][] _viewKeys = NewViewKeys();
    private int _viewCount;
    private readonly int[] _deltas = new int[MeshInstances.MaxInstances];

    // The posed instances to skin this frame, as their keys (SkinKey); an instance's index is a key's low bits.
    private readonly long[] _skinKeys = new long[MeshInstances.MaxInstances];
    private const int SkinIndexBits = 14;
    private const long SkinIndexMask = (1L << SkinIndexBits) - 1;
    private readonly MeshAssetDraw[] _draws = new MeshAssetDraw[MeshAssetPass.MaxDraws];
    private readonly DrawIndexedIndirectCommand[] _commands = new DrawIndexedIndirectCommand[MeshAssetPass.MaxCommands];

    // Each instance's centre's key nearest first (MeshView.NearFirstKey), while it is seen; and the blended list's entries (an
    // instance and a primitive each), their sort keys (farthest first), and the draws and commands they become.
    private readonly float[] _distances = new float[MeshInstances.MaxInstances];
    private readonly long[] _blendKeys = new long[MeshAssetPass.MaxCommands];
    private readonly int[] _blendOrder = new int[MeshAssetPass.MaxCommands];
    private readonly int[] _blendInstances = new int[MeshAssetPass.MaxCommands];
    private readonly int[] _blendPrimitives = new int[MeshAssetPass.MaxCommands];
    private readonly MeshAssetDraw[] _blendDraws = new MeshAssetDraw[MeshAssetPass.MaxCommands];
    private readonly DrawIndexedIndirectCommand[] _blendCommands = new DrawIndexedIndirectCommand[
        MeshAssetPass.MaxCommands
    ];

    // The camera's list's instances nearest first, a counting sort of their keys into NearBuckets buckets (NearBucket).
    private const int NearBuckets = 16;
    private readonly int[] _nearOrder = new int[MeshInstances.MaxInstances];
    private readonly int[] _nearStarts = new int[NearBuckets + 1];
    private bool _blendFullReported;
    private int[] _counts = [];
    private int[] _starts = [];
    private int[] _filled = [];
    private bool _fullReported;
    private bool _commandsFullReported;
    private bool _viewFullReported;
    private bool _viewCommandsFullReported;
    private bool _viewBlendFullReported;
    private bool _skinFullReported;
    private bool _jointsReported;
    private bool _unknownReported;

    public MeshAssetDraws(MeshAssetPass pass, ILogger<MeshAssetDraws> logger)
        : this((IMeshAssetTarget)pass, logger) { }

    /// <summary>The draws over a target of a test's own.</summary>
    internal MeshAssetDraws(IMeshAssetTarget target, ILogger<MeshAssetDraws> logger)
    {
        Debug.Assert(
            MeshInstances.MaxInstances <= 1 << SkinIndexBits,
            "An instance's index must fit a skin key's bits."
        );
        _target = target;
        _logger = logger;
    }

    /// <summary>
    /// Writes the draws of <paramref name="instances"/> into <paramref name="slot"/>'s regions, culled by
    /// <paramref name="view"/> (after its <see cref="MeshView.Begin"/> for the frame), the posed ones' skin groups and
    /// skinning matrices (theirs in <paramref name="palettes"/>, <see cref="MeshInstances.Palettes"/>), and returns what it
    /// wrote.
    /// </summary>
    public MeshAssetFrame Write(
        int slot,
        MeshView view,
        ReadOnlySpan<MeshInstance> instances,
        ReadOnlySpan<Matrix4x4> palettes = default
    ) => Write(slot, view, instances, palettes, default);

    /// <summary>
    /// <see cref="Write(int, MeshView, ReadOnlySpan{MeshInstance}, ReadOnlySpan{Matrix4x4})"/>, also writing what each
    /// image view drawn this frame sees (<paramref name="views"/>, after their <see cref="MeshView.Begin"/>) into its
    /// records: its list in the four ranges and its blended list, back to front in it. A posed instance only a view sees
    /// is skinned too.
    /// </summary>
    internal MeshAssetFrame Write(
        int slot,
        MeshView view,
        ReadOnlySpan<MeshInstance> instances,
        ReadOnlySpan<Matrix4x4> palettes,
        ReadOnlySpan<IRenderViewLists> views
    )
    {
        ArgumentNullException.ThrowIfNull(view);
        _viewCount = Math.Min(views.Length, RenderViews.MostViews);
        views[.._viewCount].CopyTo(_views);
        var layout = _target.Layout;
        if (_counts.Length != layout.Primitives.Length)
        {
            _counts = new int[layout.Primitives.Length];
            _starts = new int[layout.Primitives.Length];
            _filled = new int[layout.Primitives.Length];
        }

        var count = Math.Min(instances.Length, _lists.Length);
        for (var i = 0; i < count; i++)
        {
            _lists[i] = ListsOf(in instances[i], layout, view, palettes, i, out _distances[i]);
        }

        var groups = WriteSkins(slot, instances[..count], layout, palettes, out var mostVertices, out var joints);
        for (var v = 0; v < _viewCount; v++)
        {
            var target = _views[v];
            ref var frame = ref target.FrameOf(slot);
            const int seen = RenderViewTarget.AssetSeenList;
            const int blended = RenderViewTarget.AssetBlendedList;
            frame.Assets = WriteList(
                ViewBit + v,
                instances[..count],
                layout,
                target.AssetDrawsOf(slot, seen),
                target.AssetCommandsOf(slot, seen),
                RenderViewTarget.AssetDrawBase(slot, seen),
                RenderViewTarget.MaxAssetDraws,
                RenderViewTarget.MaxAssetCommands
            );
            frame.Blended = WriteBlended(
                ViewBit + v,
                _viewKeys[v],
                instances[..count],
                layout,
                target.AssetDrawsOf(slot, blended),
                target.AssetCommandsOf(slot, blended),
                RenderViewTarget.AssetDrawBase(slot, blended),
                RenderViewTarget.MaxAssetCommands
            );
        }

        return new MeshAssetFrame(
            WriteList(slot, 0, instances[..count], layout),
            WriteList(slot, 1, instances[..count], layout),
            WriteList(slot, 2, instances[..count], layout),
            groups,
            mostVertices,
            joints,
            WriteBlended(slot, instances[..count], layout)
        );
    }

    /// <summary>
    /// For each posed instance a list draws, its skin group and skinning matrices in the slot's regions (written once) and
    /// its vertex delta; others draw their asset's vertices. When the slot's room cannot take them all, those marked
    /// <see cref="MeshInstance.SkinFirst"/> go first, then the rest, each nearest the camera first, as long as each fits,
    /// and those left over are drawn as their asset stores them. Returns the groups written.
    /// </summary>
    private int WriteSkins(
        int slot,
        ReadOnlySpan<MeshInstance> instances,
        MeshAssetLayout layout,
        ReadOnlySpan<Matrix4x4> palettes,
        out int mostVertices,
        out int joints
    )
    {
        mostVertices = 0;
        joints = 0;
        Array.Clear(_deltas, 0, instances.Length);
        var capacity = _target.SkinVertexCapacity;
        if (capacity == 0)
        {
            return 0;
        }

        var groups = _target.SkinGroupsOf(slot);
        var region = _target.PalettesOf(slot);
        var outputBase = _target.SkinVertexBase(slot);
        var paletteBase = MeshAssetPass.PaletteBase(slot);

        // Each posed instance to skin, and what skinning them all would take.
        var candidates = 0;
        long needVertices = 0,
            needJoints = 0;
        var tooLarge = false;
        for (var i = 0; i < instances.Length; i++)
        {
            ref readonly var instance = ref instances[i];
            if (
                !instance.Posed
                || !Draws(_lists[i], instance.Asset, layout)
                || !PaletteFits(in instance, layout, palettes)
            )
            {
                continue;
            }

            ref readonly var place = ref layout.Assets[instance.Asset];
            if (place.VertexCount > MeshAssetPass.MostSkinnedVertices || place.VertexCount > capacity)
            {
                tooLarge = true;
                continue;
            }

            _skinKeys[candidates++] = SkinKey(in instance, _distances[i], i);
            needVertices += place.VertexCount;
            needJoints += place.Joints;
        }

        var full = needVertices > capacity || needJoints > region.Length || candidates > groups.Length;
        if (full)
        {
            // The marked first, then the nearest: the order of their keys.
            Array.Sort(_skinKeys, 0, candidates);
        }

        int written = 0,
            vertices = 0;
        for (var c = 0; c < candidates; c++)
        {
            var i = (int)(_skinKeys[c] & SkinIndexMask);
            ref readonly var instance = ref instances[i];
            ref readonly var place = ref layout.Assets[instance.Asset];
            if (
                vertices + place.VertexCount > capacity
                || written == groups.Length
                || joints + place.Joints > region.Length
            )
            {
                // Left out this frame, as a figure in its rest pose would show more than a missing one.
                _lists[i] = 0;
                continue;
            }

            palettes.Slice(instance.FirstJoint, place.Joints).CopyTo(region[joints..]);
            groups[written++] = new MeshSkinGroup
            {
                SourceFirst = (uint)place.FirstVertex,
                SkinFirst = (uint)place.FirstSkin,
                OutputFirst = (uint)(outputBase + vertices),
                PaletteFirst = (uint)(paletteBase + joints),
                VertexCount = (uint)place.VertexCount,
            };
            _deltas[i] = outputBase + vertices - place.FirstVertex;
            vertices += place.VertexCount;
            joints += place.Joints;
            mostVertices = Math.Max(mostVertices, place.VertexCount);
        }

        if ((full || tooLarge) && !_skinFullReported)
        {
            _skinFullReported = true;
            LogSkinFull(capacity, written, candidates + (tooLarge ? 1 : 0));
        }

        return written;
    }

    /// <summary>
    /// A posed instance's place in the order skinning takes them when the room is short, smallest first: not
    /// <see cref="MeshInstance.SkinFirst"/> in the top bit, then its centre's key nearest first (a float never below 0
    /// orders as its bits), then its index, so each key is distinct and names its instance.
    /// </summary>
    private static long SkinKey(in MeshInstance instance, float distance, int index)
    {
        var bits = (long)BitConverter.SingleToInt32Bits(MathF.Max(distance, 0)) & int.MaxValue;
        var later = instance.SkinFirst ? 0L : 1L;
        return (later << (31 + SkinIndexBits)) | (bits << SkinIndexBits) | (uint)index;
    }

    /// <summary>
    /// True when an instance in <paramref name="lists"/> draws anything: it is seen, or a cascade takes it and its asset
    /// has a primitive that is not blended (blended ones cast no shadow).
    /// </summary>
    private static bool Draws(byte lists, int asset, MeshAssetLayout layout)
    {
        if ((lists & SeenBits) != 0)
        {
            return true;
        }

        if (lists == 0)
        {
            return false;
        }

        ref readonly var place = ref layout.Assets[asset];
        for (var p = 0; p < place.PrimitiveCount; p++)
        {
            if (layout.Primitives[layout.AssetPrimitives[place.FirstPrimitive + p]].Range < MeshAssetRange.Blended)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a posed instance's asset is skinned with as many joints as it gave matrices, and they lie in
    /// <paramref name="palettes"/>; else it is drawn as its asset stores it, with one Warning a run.
    /// </summary>
    private bool PaletteFits(in MeshInstance instance, MeshAssetLayout layout, ReadOnlySpan<Matrix4x4> palettes)
    {
        ref readonly var place = ref layout.Assets[instance.Asset];
        if (
            place.FirstSkin >= 0
            && instance.Joints == place.Joints
            && instance.FirstJoint >= 0
            && instance.FirstJoint + instance.Joints <= palettes.Length
        )
        {
            return true;
        }

        if (!_jointsReported)
        {
            _jointsReported = true;
            LogJointsMismatch(instance.Asset, instance.Joints, place.Joints);
        }

        return false;
    }

    /// <summary>
    /// The lists an instance is drawn in: the camera's and each image view's where its sphere may be seen, the cascades'
    /// where it may cast; and its sphere's centre's key for the blended sort (<see cref="MeshView.NearFirstKey"/>: the
    /// squared distance from the camera, or an orthographic view's depth), and each view's (in its keys at
    /// <paramref name="index"/>).
    /// </summary>
    private byte ListsOf(
        in MeshInstance instance,
        MeshAssetLayout layout,
        MeshView view,
        ReadOnlySpan<Matrix4x4> palettes,
        int index,
        out float distance
    )
    {
        distance = 0;
        if ((uint)instance.Asset >= (uint)layout.Assets.Length)
        {
            if (!_unknownReported)
            {
                _unknownReported = true;
                LogUnknownAsset(instance.Asset, layout.Assets.Length);
            }

            return 0;
        }

        ref readonly var place = ref layout.Assets[instance.Asset];
        if (place.PrimitiveCount == 0)
        {
            return 0;
        }

        var (localCentre, localRadius) =
            instance.Posed && PaletteFits(in instance, layout, palettes)
                ? MeshBounds.Posed(
                    layout.JointPositions.AsSpan(place.FirstJoint, place.Joints),
                    layout.JointReaches.AsSpan(place.FirstJoint, place.Joints),
                    palettes.Slice(instance.FirstJoint, instance.Joints),
                    place.StaticCentre,
                    place.StaticRadius,
                    place.Centre,
                    place.Radius
                )
                : (place.Centre, place.Radius);
        var transform = instance.Transform;
        var centre = Vector3.Transform(localCentre, transform);
        var radius = localRadius * MeshBounds.LargestScale(in transform);
        var seen = instance.Shadow != ModelShadow.Only && view.InView(centre, radius);
        distance = view.NearFirstKey(centre);
        var casts = instance.Shadow != ModelShadow.None && view.Shadows;
        var near = casts && view.NearCascade.Holds(centre, radius);
        var far = casts && view.FarCascade.Holds(centre, radius);
        var lists = (seen ? 1 : 0) | (near ? 2 : 0) | (far ? 4 : 0);
        // The views show the player's own body (Only) but not what the camera alone shows (None: the first-person arms).
        for (var v = 0; v < _viewCount && instance.Shadow != ModelShadow.None; v++)
        {
            var other = _views[v].View;
            if (other.InView(centre, radius))
            {
                lists |= 1 << (ViewBit + v);
                _viewKeys[v][index] = other.NearFirstKey(centre);
            }
        }

        return (byte)lists;
    }

    private static float[][] NewViewKeys()
    {
        var keys = new float[RenderViews.MostViews][];
        for (var v = 0; v < keys.Length; v++)
        {
            keys[v] = new float[MeshInstances.MaxInstances];
        }

        return keys;
    }

    /// <summary>
    /// One list: counts each primitive's draws, places them by primitive (a counting sort), writes them and one command
    /// per primitive drawn into the slot's regions, and returns the list's counts.
    /// </summary>
    private MeshAssetList WriteList(int slot, int list, ReadOnlySpan<MeshInstance> instances, MeshAssetLayout layout) =>
        WriteList(
            list,
            instances,
            layout,
            _target.DrawsOf(slot, list),
            _target.CommandsOf(slot, list),
            MeshAssetPass.DrawBase(slot, list),
            MeshAssetPass.MaxDraws,
            MeshAssetPass.MaxCommands
        );

    /// <summary>
    /// The list of bit <paramref name="bitIndex"/> into <paramref name="drawsTarget"/> and <paramref name="commandsTarget"/>
    /// (the slot's list, or an image view's), whose first draw the shaders find at <paramref name="drawBase"/>, at most
    /// <paramref name="mostDraws"/> draws and <paramref name="mostCommands"/> commands.
    /// </summary>
    private MeshAssetList WriteList(
        int bitIndex,
        ReadOnlySpan<MeshInstance> instances,
        MeshAssetLayout layout,
        Span<MeshAssetDraw> drawsTarget,
        Span<DrawIndexedIndirectCommand> commandsTarget,
        int drawBase,
        int mostDraws,
        int mostCommands
    )
    {
        var bit = (byte)(1 << bitIndex);
        Array.Clear(_counts);
        var total = 0;
        for (var i = 0; i < instances.Length; i++)
        {
            if ((_lists[i] & bit) == 0)
            {
                continue;
            }

            ref readonly var place = ref layout.Assets[instances[i].Asset];
            if (total + place.PrimitiveCount > mostDraws)
            {
                _lists[i] &= (byte)~bit;
                ref var reported = ref bitIndex < ViewBit ? ref _fullReported : ref _viewFullReported;
                if (!reported)
                {
                    reported = true;
                    LogListFull(mostDraws);
                }

                continue;
            }

            total += place.PrimitiveCount;
            for (var p = 0; p < place.PrimitiveCount; p++)
            {
                var k = layout.AssetPrimitives[place.FirstPrimitive + p];
                if (layout.Primitives[k].Range < MeshAssetRange.Blended)
                {
                    _counts[k]++;
                }
            }
        }

        var next = 0;
        for (var k = 0; k < _counts.Length; k++)
        {
            _starts[k] = next;
            _filled[k] = 0;
            next += _counts[k];
        }

        // The camera's draws nearest first in each primitive's run, so the depth test rejects hidden pixels before they are
        // shaded; the cascades' and the views' keep the instances' order.
        var ordered = bitIndex == 0 ? SortNearFirst(instances.Length) : instances.Length;
        for (var n = 0; n < ordered; n++)
        {
            var i = bitIndex == 0 ? _nearOrder[n] : n;
            if ((_lists[i] & bit) == 0)
            {
                continue;
            }

            ref readonly var instance = ref instances[i];
            ref readonly var place = ref layout.Assets[instance.Asset];
            for (var p = 0; p < place.PrimitiveCount; p++)
            {
                var k = layout.AssetPrimitives[place.FirstPrimitive + p];
                if (layout.Primitives[k].Range >= MeshAssetRange.Blended)
                {
                    continue;
                }

                _draws[_starts[k] + _filled[k]++] = new MeshAssetDraw
                {
                    Transform = instance.Transform,
                    Material = (uint)layout.Primitives[k].Material,
                    Tint = instance.Tint,
                    VertexDelta = _deltas[i],
                };
            }
        }

        var commands = 0;
        int opaque = 0,
            opaqueTwoSided = 0,
            masked = 0,
            maskedTwoSided = 0;
        for (var k = 0; k < _counts.Length; k++)
        {
            if (_counts[k] == 0)
            {
                continue;
            }

            if (commands == mostCommands)
            {
                ref var reported = ref bitIndex < ViewBit ? ref _commandsFullReported : ref _viewCommandsFullReported;
                if (!reported)
                {
                    reported = true;
                    LogCommandsFull(mostCommands);
                }

                break;
            }

            ref readonly var primitive = ref layout.Primitives[k];
            _commands[commands++] = new DrawIndexedIndirectCommand(
                (uint)primitive.IndexCount,
                (uint)_counts[k],
                (uint)primitive.FirstIndex,
                primitive.FirstVertex,
                (uint)(drawBase + _starts[k])
            );
            switch (primitive.Range)
            {
                case MeshAssetRange.Opaque:
                    opaque++;
                    break;
                case MeshAssetRange.OpaqueTwoSided:
                    opaqueTwoSided++;
                    break;
                case MeshAssetRange.Masked:
                    masked++;
                    break;
                default:
                    maskedTwoSided++;
                    break;
            }
        }

        _draws.AsSpan(0, next).CopyTo(drawsTarget);
        _commands.AsSpan(0, commands).CopyTo(commandsTarget);
        return new MeshAssetList(opaque, opaqueTwoSided, masked, maskedTwoSided, next);
    }

    /// <summary>
    /// Writes the camera's list's instances (bit 0 of their lists) into <c>_nearOrder</c> nearest first, by a counting sort
    /// of their keys into <see cref="NearBuckets"/> buckets (<see cref="NearBucket"/>; in the order given within one), and
    /// returns how many.
    /// </summary>
    private int SortNearFirst(int count)
    {
        Array.Clear(_nearStarts);
        for (var i = 0; i < count; i++)
        {
            if ((_lists[i] & 1) != 0)
            {
                _nearStarts[NearBucket(_distances[i]) + 1]++;
            }
        }

        for (var b = 1; b <= NearBuckets; b++)
        {
            _nearStarts[b] += _nearStarts[b - 1];
        }

        var total = _nearStarts[NearBuckets];
        for (var i = 0; i < count; i++)
        {
            if ((_lists[i] & 1) != 0)
            {
                _nearOrder[_nearStarts[NearBucket(_distances[i])]++] = i;
            }
        }

        return total;
    }

    /// <summary>
    /// The bucket of a key nearest first (<see cref="MeshView.NearFirstKey"/>), 0 to <see cref="NearBuckets"/> - 1: its
    /// float's exponent from 1 up. Bucket 0 holds the squared distances under 2 (nearer than about 1.41 m), and each one
    /// after reaches √2 times as far, the last from about 181 m on (an orthographic view's depths: from 2 m, each twice
    /// as far, the last from 32 km).
    /// </summary>
    private static int NearBucket(float key) =>
        Math.Clamp((BitConverter.SingleToInt32Bits(MathF.Max(key, 0)) >> 23) - 127, 0, NearBuckets - 1);

    /// <summary>
    /// The blended list: each seen instance's blended primitives, instances sorted back to front (farthest first, then in
    /// the order given) and each one's primitives in its asset's order, as draws and commands in the slot's blended
    /// region. Returns its commands.
    /// </summary>
    private int WriteBlended(int slot, ReadOnlySpan<MeshInstance> instances, MeshAssetLayout layout) =>
        WriteBlended(
            0,
            _distances,
            instances,
            layout,
            _target.DrawsOf(slot, MeshAssetPass.BlendedList),
            _target.CommandsOf(slot, MeshAssetPass.BlendedList),
            MeshAssetPass.DrawBase(slot, MeshAssetPass.BlendedList),
            MeshAssetPass.MaxCommands
        );

    /// <summary>
    /// The blended list of the instances bit <paramref name="bitIndex"/> sees, back to front by <paramref name="keys"/>,
    /// into <paramref name="drawsTarget"/> and <paramref name="commandsTarget"/> (the slot's, or an image view's), whose
    /// first draw the shaders find at <paramref name="drawBase"/>, at most <paramref name="mostCommands"/>.
    /// </summary>
    private int WriteBlended(
        int bitIndex,
        float[] keys,
        ReadOnlySpan<MeshInstance> instances,
        MeshAssetLayout layout,
        Span<MeshAssetDraw> drawsTarget,
        Span<DrawIndexedIndirectCommand> commandsTarget,
        int drawBase,
        int mostCommands
    )
    {
        var bit = (byte)(1 << bitIndex);
        var entries = 0;
        var commands = 0;
        for (var i = 0; i < instances.Length; i++)
        {
            if ((_lists[i] & bit) == 0)
            {
                continue;
            }

            ref readonly var place = ref layout.Assets[instances[i].Asset];
            for (var p = 0; p < place.PrimitiveCount; p++)
            {
                var k = layout.AssetPrimitives[place.FirstPrimitive + p];
                var range = layout.Primitives[k].Range;
                if (range < MeshAssetRange.Blended)
                {
                    continue;
                }

                var need = range == MeshAssetRange.BlendedTwoSided ? 2 : 1;
                if (commands + need > mostCommands)
                {
                    ref var reported = ref bitIndex < ViewBit ? ref _blendFullReported : ref _viewBlendFullReported;
                    if (!reported)
                    {
                        reported = true;
                        LogBlendedFull(mostCommands);
                    }

                    continue;
                }

                // Farther first: a key's bits (never below 0) order as its value, and the entry's place keeps ties in order.
                var bits = BitConverter.SingleToInt32Bits(MathF.Max(keys[i], 0));
                _blendKeys[entries] = ((long)(int.MaxValue - bits) << 16) | (uint)entries;
                _blendOrder[entries] = entries;
                _blendInstances[entries] = i;
                _blendPrimitives[entries] = k;
                entries++;
                commands += need;
            }
        }

        Array.Sort(_blendKeys, _blendOrder, 0, entries);
        var written = 0;
        for (var j = 0; j < entries; j++)
        {
            var entry = _blendOrder[j];
            var i = _blendInstances[entry];
            var k = _blendPrimitives[entry];
            ref readonly var primitive = ref layout.Primitives[k];
            if (primitive.Range == MeshAssetRange.BlendedTwoSided)
            {
                WriteBlendedDraw(
                    in instances[i],
                    in primitive,
                    _deltas[i],
                    MeshAssetDraw.BackOnly,
                    drawBase,
                    written++
                );
            }

            WriteBlendedDraw(in instances[i], in primitive, _deltas[i], MeshAssetDraw.FrontOnly, drawBase, written++);
        }

        _blendDraws.AsSpan(0, written).CopyTo(drawsTarget);
        _blendCommands.AsSpan(0, written).CopyTo(commandsTarget);
        return written;
    }

    /// <summary>One blended draw of a primitive of an instance, showing <paramref name="sides"/>, and its command.</summary>
    private void WriteBlendedDraw(
        in MeshInstance instance,
        in MeshAssetLayout.PrimitivePlace primitive,
        int delta,
        uint sides,
        int drawBase,
        int index
    )
    {
        _blendDraws[index] = new MeshAssetDraw
        {
            Transform = instance.Transform,
            Material = (uint)primitive.Material,
            Tint = instance.Tint,
            VertexDelta = delta,
            Sides = sides,
        };
        _blendCommands[index] = new DrawIndexedIndirectCommand(
            (uint)primitive.IndexCount,
            1,
            (uint)primitive.FirstIndex,
            primitive.FirstVertex,
            (uint)(drawBase + index)
        );
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: a list of the frame's draws is full ({Draws} draws); the instances past it are not drawn"
    )]
    private partial void LogListFull(int draws);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: a list of the frame's commands is full ({Commands} primitives); the primitives past it are not drawn"
    )]
    private partial void LogCommandsFull(int commands);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: the frame's blended list is full ({Commands} commands); the blended primitives past it are not drawn"
    )]
    private partial void LogBlendedFull(int commands);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: an instance names asset {Asset}, but the run has {Count}; it is not drawn"
    )]
    private partial void LogUnknownAsset(int asset, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: the frame slot's room for skinned vertices ({Vertices} vertices; Engine:Renderer:SkinMegabytes) took {Groups} of {Posed} posed instance(s), or an asset has more vertices than one skinning takes; those marked to skin first and then the nearest were skinned, and the rest are not drawn"
    )]
    private partial void LogSkinFull(int vertices, int groups, int posed);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: a posed instance of asset {Asset} gave {Given} skinning matrices for {Joints} joints; it is drawn unposed"
    )]
    private partial void LogJointsMismatch(int asset, int given, int joints);
}
