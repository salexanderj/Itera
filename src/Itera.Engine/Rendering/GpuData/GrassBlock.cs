namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One of the camera's blocks the grass grows on this frame (<c>grass-data.glsl</c>'s <c>GrassBlock</c>, member for
/// member): its first index in the mesh heap, its triangles, its draw record (the camera command's first instance, which
/// names its <see cref="MeshDraw"/>), and the triangles of the blocks listed before it, by which
/// <c>grass-place.comp</c> finds a triangle's block. Written by <c>GrassPlacePass</c>, one region per frame slot.
/// </summary>
public struct GrassBlock
{
    public uint FirstIndex;

    public uint Triangles;

    public uint Draw;

    public uint FirstTriangle;
}
