using System.Numerics;

namespace Itera.Engine.Rendering;

/// <summary>
/// The mesh assets to draw in the frame being built (<see cref="Assets.MeshAssets"/>): frame systems <see cref="Add"/>
/// them (the engine's <c>MeshDrawSystem</c>, a game's own), <c>MeshAssetDraws</c> turns them into the mesh pass's draws
/// after the frame group, and <c>GameLoop</c> clears the list before the next frame's systems run. Main thread;
/// allocates nothing.
/// </summary>
public sealed class MeshInstances
{
    /// <summary>Mesh assets a frame can draw; more are dropped (<see cref="Dropped"/>).</summary>
    public const int MaxInstances = 16_384;

    private readonly MeshInstance[] _instances = new MeshInstance[MaxInstances];

    /// <summary>Instances added this frame, up to <see cref="MaxInstances"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Instances added this frame past <see cref="MaxInstances"/>, which are not drawn.</summary>
    public int Dropped { get; private set; }

    /// <summary>The instances added this frame.</summary>
    public ReadOnlySpan<MeshInstance> Instances => _instances.AsSpan(0, Count);

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

    /// <summary>The tint that leaves the colours as they are: white, opaque.</summary>
    public const uint NoTint = 0xFFFFFFFFu;

    /// <summary>Empties the list for the next frame.</summary>
    public void Clear()
    {
        Count = 0;
        Dropped = 0;
    }
}

/// <summary>A mesh asset to draw (<see cref="MeshInstances.Add"/>).</summary>
/// <param name="Asset">The asset's index in <see cref="Assets.MeshAssets"/>.</param>
/// <param name="Transform">From the asset's space to the world's.</param>
/// <param name="Tint">Linear RGBA bytes its colours are multiplied by, red in the low byte.</param>
/// <param name="Shadow">Whether it is drawn and whether it casts the sun's shadow.</param>
public readonly record struct MeshInstance(int Asset, Matrix4x4 Transform, uint Tint, ModelShadow Shadow);
