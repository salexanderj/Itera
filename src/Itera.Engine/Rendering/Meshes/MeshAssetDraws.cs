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
/// instances are its draws (<see cref="MeshAssetDraw"/>, one per instance and primitive). Draws and commands are built in
/// arrays of its own and copied into the slot's regions in address order. An instance that would take a list past
/// <see cref="MeshAssetPass.MaxDraws"/> draws is left out of it, and the primitives past
/// <see cref="MeshAssetPass.MaxCommands"/> commands (the last in the layout's order) are not drawn in it, each with one
/// Warning a run. A posed instance (<see cref="MeshInstance.Posed"/>) any list draws is bounded by its joints' posed places
/// and reaches, gets a skin group (<see cref="MeshSkinGroup"/>) and its skinning matrices in the slot's regions, and its
/// draws read its skinned copy (<see cref="MeshAssetDraw.VertexDelta"/>); past the slot's room it is drawn as its asset
/// stores it, with one Warning a run. Main thread, after the
/// frame group, between the view's start and end; allocates nothing once its arrays have grown to the run's assets.
/// </summary>
public sealed partial class MeshAssetDraws
{
    private readonly IMeshAssetTarget _target;
    private readonly ILogger<MeshAssetDraws> _logger;

    // The lists (bit 0 the camera's, 1 the near cascade's, 2 the far cascade's) each instance is drawn in this frame.
    private readonly byte[] _lists = new byte[MeshInstances.MaxInstances];
    private readonly int[] _deltas = new int[MeshInstances.MaxInstances];
    private readonly MeshAssetDraw[] _draws = new MeshAssetDraw[MeshAssetPass.MaxDraws];
    private readonly DrawIndexedIndirectCommand[] _commands = new DrawIndexedIndirectCommand[MeshAssetPass.MaxCommands];
    private int[] _counts = [];
    private int[] _starts = [];
    private int[] _filled = [];
    private bool _fullReported;
    private bool _commandsFullReported;
    private bool _skinFullReported;
    private bool _jointsReported;
    private bool _unknownReported;

    public MeshAssetDraws(MeshAssetPass pass, ILogger<MeshAssetDraws> logger)
        : this((IMeshAssetTarget)pass, logger) { }

    /// <summary>The draws over a target of a test's own.</summary>
    internal MeshAssetDraws(IMeshAssetTarget target, ILogger<MeshAssetDraws> logger)
    {
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
    )
    {
        ArgumentNullException.ThrowIfNull(view);
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
            _lists[i] = ListsOf(in instances[i], layout, view, palettes);
        }

        var groups = WriteSkins(slot, instances[..count], layout, palettes, out var mostVertices, out var joints);
        return new MeshAssetFrame(
            WriteList(slot, 0, instances[..count], layout),
            WriteList(slot, 1, instances[..count], layout),
            WriteList(slot, 2, instances[..count], layout),
            groups,
            mostVertices,
            joints
        );
    }

    /// <summary>
    /// For each posed instance a list draws, its skin group and skinning matrices in the slot's regions (written once, in
    /// order) and its vertex delta; others draw their asset's vertices. Returns the groups written.
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
        var capacity = _target.SkinVertexCapacity;
        if (capacity == 0)
        {
            Array.Clear(_deltas, 0, instances.Length);
            return 0;
        }

        var groups = _target.SkinGroupsOf(slot);
        var region = _target.PalettesOf(slot);
        var outputBase = _target.SkinVertexBase(slot);
        var paletteBase = MeshAssetPass.PaletteBase(slot);
        int written = 0,
            vertices = 0;
        for (var i = 0; i < instances.Length; i++)
        {
            _deltas[i] = 0;
            ref readonly var instance = ref instances[i];
            if (!instance.Posed || _lists[i] == 0 || !PaletteFits(in instance, layout, palettes))
            {
                continue;
            }

            ref readonly var place = ref layout.Assets[instance.Asset];
            if (
                vertices + place.VertexCount > capacity
                || place.VertexCount > MeshAssetPass.MostSkinnedVertices
                || written == groups.Length
                || joints + place.Joints > region.Length
            )
            {
                if (!_skinFullReported)
                {
                    _skinFullReported = true;
                    LogSkinFull(capacity, written);
                }

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

        return written;
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
    /// A posed instance's bounding sphere in its asset's space: the box round each joint's posed place grown by its reach
    /// (times the joint's scale), and the asset's unmoved vertices' sphere; the asset's own sphere when no joint reaches.
    /// </summary>
    private static (Vector3 Centre, float Radius) PosedSphere(
        in MeshAssetLayout.AssetPlace place,
        MeshAssetLayout layout,
        ReadOnlySpan<Matrix4x4> palette
    )
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var j = 0; j < place.Joints; j++)
        {
            var reach = layout.JointReaches[place.FirstJoint + j];
            if (!(reach >= 0))
            {
                continue;
            }

            var centre = Vector3.Transform(layout.JointPositions[place.FirstJoint + j], palette[j]);
            var grow = new Vector3(reach * LargestScale(in palette[j]));
            min = Vector3.Min(min, centre - grow);
            max = Vector3.Max(max, centre + grow);
        }

        if (place.StaticRadius >= 0)
        {
            min = Vector3.Min(min, place.StaticCentre - new Vector3(place.StaticRadius));
            max = Vector3.Max(max, place.StaticCentre + new Vector3(place.StaticRadius));
        }

        return min.X > max.X ? (place.Centre, place.Radius) : ((min + max) / 2, (max - min).Length() / 2);
    }

    /// <summary>The lists an instance is drawn in: the camera's where its sphere may be seen, the cascades' where it may cast.</summary>
    private byte ListsOf(
        in MeshInstance instance,
        MeshAssetLayout layout,
        MeshView view,
        ReadOnlySpan<Matrix4x4> palettes
    )
    {
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
                ? PosedSphere(in place, layout, palettes.Slice(instance.FirstJoint, instance.Joints))
                : (place.Centre, place.Radius);
        var transform = instance.Transform;
        var centre = Vector3.Transform(localCentre, transform);
        var radius = localRadius * LargestScale(in transform);
        var seen = instance.Shadow != ModelShadow.Only && view.InView(centre, radius);
        var casts = instance.Shadow != ModelShadow.None && view.Shadows;
        var near = casts && view.NearCascade.Holds(centre, radius);
        var far = casts && view.FarCascade.Holds(centre, radius);
        return (byte)((seen ? 1 : 0) | (near ? 2 : 0) | (far ? 4 : 0));
    }

    /// <summary>
    /// One list: counts each primitive's draws, places them by primitive (a counting sort), writes them and one command
    /// per primitive drawn into the slot's regions, and returns the list's counts.
    /// </summary>
    private MeshAssetList WriteList(int slot, int list, ReadOnlySpan<MeshInstance> instances, MeshAssetLayout layout)
    {
        var bit = (byte)(1 << list);
        Array.Clear(_counts);
        var total = 0;
        for (var i = 0; i < instances.Length; i++)
        {
            if ((_lists[i] & bit) == 0)
            {
                continue;
            }

            ref readonly var place = ref layout.Assets[instances[i].Asset];
            if (total + place.PrimitiveCount > MeshAssetPass.MaxDraws)
            {
                _lists[i] &= (byte)~bit;
                if (!_fullReported)
                {
                    _fullReported = true;
                    LogListFull(MeshAssetPass.MaxDraws);
                }

                continue;
            }

            total += place.PrimitiveCount;
            for (var p = 0; p < place.PrimitiveCount; p++)
            {
                _counts[layout.AssetPrimitives[place.FirstPrimitive + p]]++;
            }
        }

        var next = 0;
        for (var k = 0; k < _counts.Length; k++)
        {
            _starts[k] = next;
            _filled[k] = 0;
            next += _counts[k];
        }

        for (var i = 0; i < instances.Length; i++)
        {
            if ((_lists[i] & bit) == 0)
            {
                continue;
            }

            ref readonly var instance = ref instances[i];
            ref readonly var place = ref layout.Assets[instance.Asset];
            for (var p = 0; p < place.PrimitiveCount; p++)
            {
                var k = layout.AssetPrimitives[place.FirstPrimitive + p];
                _draws[_starts[k] + _filled[k]++] = new MeshAssetDraw
                {
                    Transform = instance.Transform,
                    Material = (uint)layout.Primitives[k].Material,
                    Tint = instance.Tint,
                    VertexDelta = _deltas[i],
                };
            }
        }

        var drawBase = MeshAssetPass.DrawBase(slot, list);
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

            if (commands == MeshAssetPass.MaxCommands)
            {
                if (!_commandsFullReported)
                {
                    _commandsFullReported = true;
                    LogCommandsFull(MeshAssetPass.MaxCommands);
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

        _draws.AsSpan(0, total).CopyTo(_target.DrawsOf(slot, list));
        _commands.AsSpan(0, commands).CopyTo(_target.CommandsOf(slot, list));
        return new MeshAssetList(opaque, opaqueTwoSided, masked, maskedTwoSided, total);
    }

    /// <summary>The largest length an axis of the asset's space takes in the world's: the scale its bounding sphere grows by.</summary>
    private static float LargestScale(in Matrix4x4 m)
    {
        var x = m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13;
        var y = m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23;
        var z = m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33;
        return MathF.Sqrt(Math.Max(x, Math.Max(y, z)));
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
        Message = "Mesh assets: an instance names asset {Asset}, but the run has {Count}; it is not drawn"
    )]
    private partial void LogUnknownAsset(int asset, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: the frame slot's room for skinned vertices ({Vertices} vertices; Engine:Renderer:SkinMegabytes) is full after {Groups} instance(s), or an asset has more vertices than one skinning takes; the posed instances that do not fit are drawn unposed"
    )]
    private partial void LogSkinFull(int vertices, int groups);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Mesh assets: a posed instance of asset {Asset} gave {Given} skinning matrices for {Joints} joints; it is drawn unposed"
    )]
    private partial void LogJointsMismatch(int asset, int given, int joints);
}
