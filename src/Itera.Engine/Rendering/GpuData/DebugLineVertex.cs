using System.Numerics;

namespace Itera.Engine.Rendering.GpuData;

/// <summary>
/// One end of a debug line (<c>debug-lines.vert</c>, <c>layout(scalar)</c> storage buffer at set 0, binding 0): a point
/// in world metres and its colour as four bytes, red lowest (GLSL <c>unpackUnorm4x8</c>). Field order and types match
/// the GLSL struct; <c>DebugLinesTests</c> checks them.
/// </summary>
public struct DebugLineVertex
{
    public Vector3 Position;

    public uint Colour;
}
