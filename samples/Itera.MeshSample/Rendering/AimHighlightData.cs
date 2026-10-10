using System.Numerics;

namespace Itera.MeshSample.Rendering;

/// <summary>
/// What <c>aim-highlight.vert</c> and <c>.frag</c> read of a frame (<c>AimHighlightData</c>, set 0 binding 0, one per
/// frame slot, at <c>view.slot</c>): the highlight's box (a unit cube's transform of uniform scale, used as
/// <c>M * v</c>), its colour (alpha its strength), the seconds its pulse follows, and how far in from the box's edges
/// its glow falls to a third. Field order and types match the GLSL struct; <c>AimHighlightPassTests</c> checks them.
/// </summary>
public struct AimHighlightData
{
    public Matrix4x4 Transform;
    public Vector4 Colour;
    public float Seconds;
    public float EdgeMetres;
}
