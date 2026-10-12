using System.Numerics;
using Itera.Engine.Loading;
using Itera.Engine.Rendering;
using Itera.Engine.Voxels;
using Microsoft.Extensions.Logging;

namespace Itera.Engine.Objects;

/// <summary>
/// A match's object kinds as models (scoped; with voxel terrain): at the match's start (<see cref="Initialize"/>) it seals
/// the run's kinds (<see cref="VoxelObjectKinds"/>) for the world's grid and makes each kind's parts into models of three
/// levels that keep their densities, in the world's voxel memory, which the renderer then meshes. Main thread.
/// </summary>
public sealed partial class VoxelObjects(
    VoxelObjectKinds kinds,
    VoxelWorld world,
    VoxelModels models,
    ILogger<VoxelObjects> logger
)
{
    private ObjectPartModel[][] _parts = [];

    /// <summary>True once the kinds' models are made.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>The run's kinds.</summary>
    public VoxelObjectKinds Kinds => kinds;

    /// <summary>
    /// Seals the kinds for the world's grid (a kind whose voxels are not finer than the world's throws) and makes their
    /// models. At the match's start, once the world's memory exists.
    /// </summary>
    public void Initialize()
    {
        kinds.Seal(world.Grid);
        var materials = new byte[VoxelConstants.ChunkVoxels];
        var densities = new byte[VoxelConstants.ChunkVoxels];
        _parts = new ObjectPartModel[kinds.Count][];
        var parts = 0;
        for (var k = 0; k < kinds.Count; k++)
        {
            var made = MakeParts(models, kinds[new VoxelObjectKindId((ushort)k)], materials, densities);
            _parts[k] = made;
            parts += made.Length;
        }

        IsInitialized = true;
        if (kinds.Count > 0)
        {
            LogMade(kinds.Count, parts);
        }
    }

    /// <summary>
    /// The models of <paramref name="kind"/>'s parts, each with its box's corner from the kind's anchor in metres
    /// (unturned). Once <see cref="Initialize"/>d.
    /// </summary>
    public ReadOnlySpan<ObjectPartModel> PartsOf(VoxelObjectKindId kind) => _parts[kind.Value];

    /// <summary>
    /// Makes <paramref name="kind"/>'s parts into models of <paramref name="models"/>: three levels with densities, each
    /// owning its own voxels past its border, with its box's corner from the kind's anchor; through two chunk-sized
    /// buffers.
    /// </summary>
    internal static ObjectPartModel[] MakeParts(
        VoxelModels models,
        VoxelObjectKind kind,
        Span<byte> materials,
        Span<byte> densities
    )
    {
        Span<ModelTree> trees = stackalloc ModelTree[VoxelModels.MaxLevels];
        var made = new ObjectPartModel[kind.Parts.Length];
        var unit = kind.UnitCentimetres;
        for (var p = 0; p < kind.Parts.Length; p++)
        {
            var part = kind.Parts[p];
            for (var level = 0; level < VoxelModels.MaxLevels; level++)
            {
                part.Decode(level, materials, densities);
                trees[level] = models.Encode(materials, densities, part.SizeAt(level));
            }

            var model = models.Add(trees, part.Size, 0, unit, densities: true);
            // It owns its own voxels, past its border of its neighbours'.
            const int border = ObjectPart.BorderVoxels;
            models.SetOwned(
                model,
                new VoxelCoord(border, border, border),
                new VoxelCoord(border + part.OwnSize.X, border + part.OwnSize.Y, border + part.OwnSize.Z),
                part.OpenLow
            );
            // From the kind's anchor to the corner of the part's box (its border's), in metres.
            var corner =
                new Vector3(
                    (part.Offset.X - border) * unit - kind.AnchorCentimetres.X,
                    (part.Offset.Y - border) * unit - kind.AnchorCentimetres.Y,
                    (part.Offset.Z - border) * unit - kind.AnchorCentimetres.Z
                ) / VoxelConstants.CentimetresPerMetre;
            made[p] = new ObjectPartModel(model, corner);
        }

        return made;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Objects: {Kinds} kind(s) made into {Parts} part model(s)")]
    private partial void LogMade(int kinds, int parts);

    /// <summary>True when every kind's every model is on the GPU.</summary>
    public bool AllUploaded()
    {
        foreach (var parts in _parts)
        {
            foreach (var part in parts)
            {
                if (!models.IsUploaded(part.Model))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>One part of a kind as a model: its handle, and its box's corner (its border's) from the kind's anchor in metres.</summary>
public readonly record struct ObjectPartModel(int Model, Vector3 Corner);

/// <summary>
/// The object kinds' models made and, where the match is drawn, on the GPU. Done at once with no kinds; without graphics
/// once they are made.
/// </summary>
public sealed class ObjectsLoadingTask(VoxelObjects objects, IWorldOnGpu uploader) : ILoadingTask
{
    public string Name => "Objects";

    public LoadingProgress Progress =>
        LoadingProgress.When(objects.IsInitialized && (uploader is NoGpuWorld || objects.AllUploaded()));
}
