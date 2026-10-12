namespace Itera.Engine.Voxels;

/// <summary>
/// The 32-voxel blocks each level of a model is meshed in (the renderer's, <c>WorldMeshes.Models.cs</c>): the blocks start
/// <see cref="Shift"/> voxel before the level's box (a block meshes only the faces whose lower voxel is in it), indexed
/// <c>x + countX * (y + countY * z)</c>, and each reads its voxels with an apron of <see cref="VoxelWorld.BlockApron"/>
/// round it. What a block's mesh depends on lies in what it reads, so a block none of whose read voxels changed meshes as
/// it did (<see cref="VoxelModels.ShareBlocks"/>).
/// </summary>
internal static class ModelBlocks
{
    /// <summary>Voxels along one edge of a block.</summary>
    public const int Size = VoxelWorld.BlockEdge;

    /// <summary>How far before a level's box its first block starts, in voxels.</summary>
    public const int Shift = 1;

    /// <summary>The blocks along each axis of a level whose box is <paramref name="size"/>.</summary>
    public static (int X, int Y, int Z) Counts(VoxelCoord size) =>
        ((size.X + Shift + Size - 1) / Size, (size.Y + Shift + Size - 1) / Size, (size.Z + Shift + Size - 1) / Size);

    /// <summary>
    /// The first and last blocks, along one axis, whose reads hold voxel <paramref name="voxel"/> of the level: block b
    /// reads voxels <c>32b - Apron - Shift</c> to <c>32b - Apron - Shift + BlockSampleEdge - 1</c>.
    /// </summary>
    public static (int First, int Last) Reading(int voxel)
    {
        var start = VoxelWorld.BlockApron + Shift;
        var last = Maths.DeterministicMath.FloorDivide(voxel + start, Size);
        var first = Maths.DeterministicMath.FloorDivide(
            voxel + start - VoxelWorld.BlockSampleEdge + 1 + Size - 1,
            Size
        );
        return ((int)first, (int)last);
    }
}
