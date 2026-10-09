using System.Numerics;

namespace Itera.Engine.Rendering.Voxels;

/// <summary>How each material id is drawn (<see cref="VoxelLook"/>). The game registers one.</summary>
public interface IVoxelPalette
{
    /// <summary>
    /// Makes the palette ready before its looks are read: <c>WorldMeshes</c> calls it once, at the start of the renderer's
    /// start-up step, before any material's look is read. A palette whose colours come from a file (a game's image) loads it here. Main thread.
    /// </summary>
    void Prepare() { }

    /// <summary>How <paramref name="material"/> is drawn. Material 0 is air and unused.</summary>
    VoxelLook Look(byte material);
}

/// <summary>Draws every material grey. Used when the game registers no palette.</summary>
public sealed class GreyVoxelPalette : IVoxelPalette
{
    public VoxelLook Look(byte material) => VoxelLook.Plain(new Vector3(0.5f));
}
