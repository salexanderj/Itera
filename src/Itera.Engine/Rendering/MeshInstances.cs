using System.Numerics;

namespace Itera.Engine.Rendering;

/// <summary>
/// The mesh assets to draw in the frame being built (<see cref="Assets.MeshAssets"/>): frame systems <see cref="Add"/>
/// them, or <see cref="AddPosed"/> a skinned one with its skinning matrices (the engine's <c>MeshDrawSystem</c>, a game's
/// own), <c>MeshAssetDraws</c> turns them into the mesh pass's draws after the frame group, and <c>GameLoop</c> clears
/// the list before the next frame's systems run. Main thread; allocates nothing.
/// </summary>
public sealed class MeshInstances
{
    /// <summary>Mesh assets a frame can draw; more are dropped (<see cref="Dropped"/>).</summary>
    public const int MaxInstances = 16_384;

    /// <summary>Skinning matrices (one a joint of each posed instance) a frame holds; posed instances past them are dropped.</summary>
    public const int MaxPaletteJoints = 32_768;

    private readonly MeshInstance[] _instances = new MeshInstance[MaxInstances];
    private readonly Matrix4x4[] _palettes = new Matrix4x4[MaxPaletteJoints];

    /// <summary>Instances added this frame, up to <see cref="MaxInstances"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Instances added this frame past <see cref="MaxInstances"/>, which are not drawn.</summary>
    public int Dropped { get; private set; }

    /// <summary>The instances added this frame.</summary>
    public ReadOnlySpan<MeshInstance> Instances => _instances.AsSpan(0, Count);

    /// <summary>Skinning matrices the posed instances took this frame.</summary>
    public int PaletteJoints { get; private set; }

    /// <summary>The posed instances' skinning matrices, each instance's from its <see cref="MeshInstance.FirstJoint"/>.</summary>
    public ReadOnlySpan<Matrix4x4> Palettes => _palettes.AsSpan(0, PaletteJoints);

    /// <summary>
    /// Draws mesh asset <paramref name="asset"/> (its index in <see cref="Assets.MeshAssets"/>) placed by
    /// <paramref name="transform"/> from its space to the world's (System.Numerics' row order: scale, then turn, then
    /// move; no mirroring), its colours multiplied by <paramref name="tint"/> (linear RGBA bytes, red in the low byte;
    /// <see cref="NoTint"/> for none); <paramref name="shadow"/> says whether it is drawn and casts the sun's shadow.
    /// </summary>
    public void Add(int asset, in Matrix4x4 transform, uint tint = NoTint, ModelShadow shadow = ModelShadow.Casts)
    {
        if (Count == MaxInstances)
        {
            Dropped++;
            return;
        }

        _instances[Count++] = new MeshInstance(asset, transform, tint, shadow);
    }

    /// <summary>
    /// Draws skinned mesh asset <paramref name="asset"/> posed: as <see cref="Add"/>, and returns where its
    /// <paramref name="joints"/> skinning matrices go (its rig's joint count; <c>Rig.WritePalette</c>), to be written
    /// before the frame is built; an empty span when the frame is full, and the instance is dropped. With
    /// <paramref name="skinFirst"/> it is skinned before the instances without it when the frame cannot skin them all
    /// (<see cref="MeshInstance.SkinFirst"/>).
    /// </summary>
    public Span<Matrix4x4> AddPosed(
        int asset,
        in Matrix4x4 transform,
        int joints,
        uint tint = NoTint,
        ModelShadow shadow = ModelShadow.Casts,
        bool skinFirst = false
    )
    {
        if (Count == MaxInstances || joints <= 0 || PaletteJoints + joints > MaxPaletteJoints)
        {
            Dropped++;
            return Span<Matrix4x4>.Empty;
        }

        var first = PaletteJoints;
        PaletteJoints += joints;
        _instances[Count++] = new MeshInstance(asset, transform, tint, shadow, first, joints, skinFirst);
        return _palettes.AsSpan(first, joints);
    }

    /// <summary>The tint that leaves the colours as they are: white, opaque.</summary>
    public const uint NoTint = 0xFFFFFFFFu;

    /// <summary>Empties the list for the next frame.</summary>
    public void Clear()
    {
        Count = 0;
        Dropped = 0;
        PaletteJoints = 0;
    }
}

/// <summary>A mesh asset to draw (<see cref="MeshInstances.Add"/>, <see cref="MeshInstances.AddPosed"/>).</summary>
/// <param name="Asset">The asset's index in <see cref="Assets.MeshAssets"/>.</param>
/// <param name="Transform">From the asset's space to the world's.</param>
/// <param name="Tint">Linear RGBA bytes its colours are multiplied by, red in the low byte.</param>
/// <param name="Shadow">Whether it is drawn and whether it casts the sun's shadow.</param>
/// <param name="FirstJoint">A posed instance's first skinning matrix in <see cref="MeshInstances.Palettes"/>; -1 for none.</param>
/// <param name="Joints">A posed instance's skinning matrices; 0 for an instance drawn as its asset stores it.</param>
/// <param name="SkinFirst">
/// True for a posed instance skinned before those without it when the frame's room for skinned vertices cannot hold them
/// all (the nearest the camera first within each); those left over are drawn as their asset stores them.
/// </param>
public readonly record struct MeshInstance(
    int Asset,
    Matrix4x4 Transform,
    uint Tint,
    ModelShadow Shadow,
    int FirstJoint = -1,
    int Joints = 0,
    bool SkinFirst = false
)
{
    /// <summary>True for a skinned asset drawn in a pose of its own.</summary>
    public bool Posed => Joints > 0;
}
