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
/// Warning a run. Main thread, after the
/// frame group, between the view's start and end; allocates nothing once its arrays have grown to the run's assets.
/// </summary>
public sealed partial class MeshAssetDraws
{
    private readonly IMeshAssetTarget _target;
    private readonly ILogger<MeshAssetDraws> _logger;

    // The lists (bit 0 the camera's, 1 the near cascade's, 2 the far cascade's) each instance is drawn in this frame.
    private readonly byte[] _lists = new byte[MeshInstances.MaxInstances];
    private readonly MeshAssetDraw[] _draws = new MeshAssetDraw[MeshAssetPass.MaxDraws];
    private readonly DrawIndexedIndirectCommand[] _commands = new DrawIndexedIndirectCommand[MeshAssetPass.MaxCommands];
    private int[] _counts = [];
    private int[] _starts = [];
    private int[] _filled = [];
    private bool _fullReported;
    private bool _commandsFullReported;
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
    /// <paramref name="view"/> (after its <see cref="MeshView.Begin"/> for the frame), and returns what it wrote.
    /// </summary>
    public MeshAssetFrame Write(int slot, MeshView view, ReadOnlySpan<MeshInstance> instances)
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
            _lists[i] = ListsOf(in instances[i], layout, view);
        }

        return new MeshAssetFrame(
            WriteList(slot, 0, instances[..count], layout),
            WriteList(slot, 1, instances[..count], layout),
            WriteList(slot, 2, instances[..count], layout)
        );
    }

    /// <summary>The lists an instance is drawn in: the camera's where its sphere may be seen, the cascades' where it may cast.</summary>
    private byte ListsOf(in MeshInstance instance, MeshAssetLayout layout, MeshView view)
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

        var transform = instance.Transform;
        var centre = Vector3.Transform(place.Centre, transform);
        var radius = place.Radius * LargestScale(in transform);
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
}
