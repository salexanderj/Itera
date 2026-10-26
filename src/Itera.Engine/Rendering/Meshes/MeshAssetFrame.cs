using Itera.Engine.Rendering.GpuData;
using Silk.NET.Vulkan;

namespace Itera.Engine.Rendering.Meshes;

/// <summary>
/// What <see cref="MeshAssetDraws"/> wrote into a frame slot's regions for each of the mesh pass's three lists (the
/// camera's, the near cascade's, the far cascade's): its commands per pipeline range and its draws; the blended draws,
/// sorted back to front, one command each (<see cref="MeshAssetPass.BlendedList"/>); and the posed instances to skin
/// (<see cref="MeshSkinGroup"/>), with the vertices they skin in all and the skinning matrices they take. Carried to the
/// render thread in <c>RenderFrame</c>.
/// </summary>
public readonly record struct MeshAssetFrame(
    MeshAssetList Camera,
    MeshAssetList Near,
    MeshAssetList Far,
    int SkinGroups = 0,
    int SkinVertices = 0,
    int SkinJoints = 0,
    int Blended = 0
)
{
    /// <summary>List 0 (the camera's), 1 (the near cascade's), or 2 (the far cascade's).</summary>
    public MeshAssetList List(int list) =>
        list switch
        {
            0 => Camera,
            1 => Near,
            _ => Far,
        };
}

/// <summary>One list's commands, by pipeline range (<see cref="MeshAssetRange"/>, in that order), and its draws.</summary>
public readonly record struct MeshAssetList(int Opaque, int OpaqueTwoSided, int Masked, int MaskedTwoSided, int Draws)
{
    public int Commands => Opaque + OpaqueTwoSided + Masked + MaskedTwoSided;

    /// <summary>The commands of <paramref name="range"/>, one of the four list ranges (blended ones are in no list).</summary>
    public int Of(MeshAssetRange range) =>
        range switch
        {
            MeshAssetRange.Opaque => Opaque,
            MeshAssetRange.OpaqueTwoSided => OpaqueTwoSided,
            MeshAssetRange.Masked => Masked,
            MeshAssetRange.MaskedTwoSided => MaskedTwoSided,
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Blended primitives are in no list."),
        };

    /// <summary>The first command of <paramref name="range"/> in the list, one of the four list ranges.</summary>
    public int FirstOf(MeshAssetRange range) =>
        range switch
        {
            MeshAssetRange.Opaque => 0,
            MeshAssetRange.OpaqueTwoSided => Opaque,
            MeshAssetRange.Masked => Opaque + OpaqueTwoSided,
            MeshAssetRange.MaskedTwoSided => Opaque + OpaqueTwoSided + Masked,
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Blended primitives are in no list."),
        };
}

/// <summary>
/// Where <see cref="MeshAssetDraws"/> writes a frame's asset draws: <see cref="MeshAssetPass"/>, or memory of a test's
/// own.
/// </summary>
internal interface IMeshAssetTarget
{
    /// <summary>Where the run's assets lie in the pass's buffers.</summary>
    MeshAssetLayout Layout { get; }

    /// <summary>The draws of list <paramref name="list"/> of a frame slot, <see cref="MeshAssetPass.MaxDraws"/> long.</summary>
    Span<MeshAssetDraw> DrawsOf(int slot, int list);

    /// <summary>
    /// The commands of list <paramref name="list"/> of a frame slot, <see cref="MeshAssetPass.MaxCommands"/> long; a
    /// command's first instance is <see cref="MeshAssetPass.DrawBase"/> plus its first draw's place in the list.
    /// </summary>
    Span<DrawIndexedIndirectCommand> CommandsOf(int slot, int list);

    /// <summary>The skin groups of a frame slot, <see cref="MeshAssetPass.MaxSkinGroups"/> long.</summary>
    Span<MeshSkinGroup> SkinGroupsOf(int slot);

    /// <summary>The skinning matrices of a frame slot, <see cref="MeshInstances.MaxPaletteJoints"/> long.</summary>
    Span<System.Numerics.Matrix4x4> PalettesOf(int slot);

    /// <summary>The skinned vertices a frame slot's region of the skinned streams holds (0 without skinned assets).</summary>
    int SkinVertexCapacity { get; }

    /// <summary>The first vertex of a frame slot's region in the skinned streams.</summary>
    int SkinVertexBase(int slot);
}
