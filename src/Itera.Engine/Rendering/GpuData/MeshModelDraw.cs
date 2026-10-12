using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One block of one model drawn (<c>mesh-data.glsl</c>'s <c>MeshModelDraw</c>, read by <c>mesh.vert</c> and
/// <c>mesh-shadow.vert</c> compiled with <c>MESH_MODEL</c>, at <c>gl_InstanceIndex</c>): where the model's box stands
/// and how it is turned, how large its voxels are at the level drawn, where the block lies in the model and where its
/// vertices start in the mesh heap, how far its materials blend to their tint colours, and the share of its pixels it shows
/// (a voxel object's model levels fading into each other, and into its far copy). The draws of one block of
/// one model level lie together, so one indexed indirect command draws them all as instances. Written per frame, one
/// region per frame slot and list, by <c>WorldMeshes</c>.
/// </summary>
public struct MeshModelDraw
{
    /// <summary>The corner of the model's box (its voxel (0, 0, 0)) in world metres.</summary>
    public Vector3 Corner;

    /// <summary>The edge of one of the model's voxels at the level drawn, in metres.</summary>
    public float VoxelMetres;

    /// <summary>The turn from the model's axes to the world's.</summary>
    public Quaternion Turn;

    /// <summary>The first word of the block's vertices in the mesh heap (<see cref="MeshVertex"/>, three words each).</summary>
    public uint VertexBase;

    /// <summary>The block's place in the model, in blocks of 32 voxels: x, y, and z in bits 0, 8, and 16.</summary>
    public uint Block;

    /// <summary>
    /// How far the materials blend from their base to their tint colour, 0 to 1; below 0, the model is shaded as the
    /// world's surfaces are (its materials' texture sets and the tint map at its points, its mesh's material weights, the
    /// world's look): a voxel object's part, so it matches its copy in the world's voxels.
    /// </summary>
    public float Tint;

    /// <summary>
    /// The share of the pixels it covers that the draw shows, as a range of a screen-space dither's thresholds, 0 to 1,
    /// in 65,536ths (<see cref="Shows"/>): <c>mesh.frag</c>'s model variant draws a pixel whose threshold lies in it, so
    /// two draws of one object fading into each other, one showing the thresholds under a share and the other those over
    /// it, share its pixels between them (a voxel object's model levels, and its coarsest fading out over its far copy).
    /// 0, <see cref="ShowsAll"/>, for every pixel.
    /// </summary>
    public uint Shown;

    /// <summary>Every pixel: thresholds from 0 to 1.</summary>
    public const uint ShowsAll = 0;

    /// <summary>No pixel.</summary>
    public const uint ShowsNone = uint.MaxValue;

    /// <summary>
    /// The pixels whose thresholds lie from <paramref name="from"/> up to <paramref name="to"/> (0 to 1, each rounded to
    /// 65,536ths, so two draws given one share as an end and a start meet exactly): the start's 65,536ths low, and the
    /// 65,536ths above the end high; <see cref="ShowsNone"/> where the rounded range is empty.
    /// </summary>
    public static uint Shows(float from, float to)
    {
        const float Steps = 65536;
        var start = (uint)MathF.Round(Math.Clamp(from, 0, 1) * Steps);
        var end = (uint)MathF.Round(Math.Clamp(to, 0, 1) * Steps);
        return end <= start ? ShowsNone : start | ((65536 - end) << 16);
    }

    /// <summary>The packed <see cref="Block"/> of block (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>).</summary>
    public static uint PackBlock(int x, int y, int z) => (uint)x | ((uint)y << 8) | ((uint)z << 16);
}
